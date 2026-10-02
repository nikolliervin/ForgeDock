using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using ForgeDock.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace ForgeDock.Tests;

public class SsoAuthenticationTests
{
    [Theory]
    [InlineData("keycloak")]
    [InlineData("oidc")]
    [InlineData("entra")]
    [InlineData("auth0")]
    [InlineData("okta")]
    [InlineData("authentik")]
    public async Task SsoRejectsBearerTokensAndDoesNotRedirectApiRequests(string mode)
    {
        using var fixture = new SsoFixture(mode);
        using var client = fixture.Client();
        client.DefaultRequestHeaders.Authorization = new("Bearer", new string('x', 64));
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/session")).StatusCode
        );
        var config = await client.GetFromJsonAsync<JsonElement>("/api/auth/config");
        Assert.Equal("sso", config.GetProperty("mode").GetString());
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/auth/logout", new { })).StatusCode
        );
    }

    [Fact]
    public async Task CodeFlowUsesPkceAndSecureCookiesAndRestoresTheIndividualSession()
    {
        using var fixture = new SsoFixture();
        using var client = fixture.Client();
        var login = await client.GetAsync("/api/auth/login");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var query = QueryHelpers.ParseQuery(login.Headers.Location!.Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("https://dashboard.example/api/auth/callback", query["redirect_uri"]);
        foreach (var cookie in login.Headers.GetValues("Set-Cookie"))
        {
            Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        }
        fixture.Nonce = query["nonce"].ToString();
        var callback = await client.PostAsync(
            "/api/auth/callback",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["code"] = "test-code",
                    ["state"] = query["state"].ToString(),
                }
            )
        );
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/dashboard", callback.Headers.Location!.OriginalString);
        var sessionCookie = callback
            .Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith("__Host-ForgeDock.Session="));
        Assert.Contains("secure", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("access-token", sessionCookie);
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.Equal("Alice", session.GetProperty("name").GetString());
        Assert.False(string.IsNullOrEmpty(session.GetProperty("csrfToken").GetString()));
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/auth/logout", new { })).StatusCode
        );
        client.DefaultRequestHeaders.Add(
            "X-CSRF-Token",
            session.GetProperty("csrfToken").GetString()
        );
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/api/auth/logout", new { })).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/session")).StatusCode
        );
        // Replaying the pre-logout cookie fails because its server ticket was removed.
        using var replay = fixture.Client();
        replay.DefaultRequestHeaders.Add("Cookie", sessionCookie.Split(';')[0]);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await replay.GetAsync("/api/session")).StatusCode
        );
    }

    [Theory]
    [InlineData("unapproved", false, "https://issuer.example", "forgedock-test")]
    [InlineData("alice", true, "https://issuer.example", "forgedock-test")]
    [InlineData("alice", false, "https://wrong-issuer.example", "forgedock-test")]
    [InlineData("alice", false, "https://issuer.example", "wrong-client")]
    public async Task UnapprovedSubjectsAndInvalidTokensCannotCreateSessions(
        string subject,
        bool wrongNonce,
        string issuer,
        string audience
    )
    {
        using var fixture = new SsoFixture
        {
            Subject = subject,
            TokenIssuer = issuer,
            TokenAudience = audience,
        };
        using var client = fixture.Client();
        var login = await client.GetAsync("/api/auth/login");
        var query = QueryHelpers.ParseQuery(login.Headers.Location!.Query);
        fixture.Nonce = wrongNonce ? "invalid-nonce" : query["nonce"].ToString();
        var callback = await client.PostAsync(
            "/api/auth/callback",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["code"] = "test-code",
                    ["state"] = query["state"].ToString(),
                }
            )
        );
        Assert.Equal("/?authError=sso", callback.Headers.Location!.OriginalString);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/session")).StatusCode
        );
    }

    [Fact]
    public async Task SessionStoreExpiresAndRevokesTickets()
    {
        var store = new ManagementTicketStore();
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity("test")),
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(1) },
            "test"
        );
        var key = await store.StoreAsync(ticket);
        Assert.NotNull(await store.RetrieveAsync(key));
        await store.RemoveAsync(key);
        Assert.Null(await store.RetrieveAsync(key));
        ticket.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        key = await store.StoreAsync(ticket);
        Assert.Null(await store.RetrieveAsync(key));
    }

    [Theory]
    [InlineData("http://dashboard.example/api/auth/login")]
    [InlineData("http://attacker.example/api/auth/login?returnUrl=https://attacker.example")]
    public async Task HttpLoginRedirectsToConfiguredHttpsOriginBeforeIssuingCookies(string url)
    {
        using var fixture = new SsoFixture();
        using var client = fixture.Client();
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            "https://dashboard.example/api/auth/login",
            response.Headers.Location!.AbsoluteUri
        );
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData("Authority", "http://issuer.example")]
    [InlineData("PublicOrigin", "https://dashboard.example/path")]
    [InlineData("ClientSecret", "")]
    [InlineData("AllowedSubjects:0", "")]
    public void InvalidConfigurationFailsWithoutTokenFallback(string name, string value)
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.Configuration["ForgeDock:Oidc:Enabled"] = "true";
        builder.Configuration["ForgeDock:Oidc:Authority"] = "https://issuer.example";
        builder.Configuration["ForgeDock:Oidc:PublicOrigin"] = "https://dashboard.example";
        builder.Configuration["ForgeDock:Oidc:ClientId"] = "test-client";
        builder.Configuration["ForgeDock:Oidc:ClientSecret"] = "test-secret";
        builder.Configuration["ForgeDock:Oidc:AllowedSubjects:0"] = "alice";
        builder.Configuration[$"ForgeDock:Oidc:{name}"] = value;
        Assert.Throws<InvalidOperationException>(() => builder.AddManagementAuthentication());
    }

    [Fact]
    public async Task LegacyModeStillRequiresTheConfiguredBearerToken()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(
                "ConnectionStrings:ForgeDock",
                "Host=localhost;Database=unused;Username=unused;Password=unused"
            );
            builder.UseSetting("ForgeDock:SecretKey", Convert.ToBase64String(new byte[32]));
            builder.UseSetting("ForgeDock:Oidc:Enabled", "false");
            builder.UseSetting("ForgeDock:ApiToken", new string('x', 64));
        });
        using var client = factory.CreateClient();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/session")).StatusCode
        );
        client.DefaultRequestHeaders.Authorization = new("Bearer", new string('y', 64));
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/session")).StatusCode
        );
        client.DefaultRequestHeaders.Authorization = new("Bearer", new string('x', 64));
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        Assert.Equal("operator", session.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("csrfToken").ValueKind);
    }

    [Fact]
    public void ExplicitModeOverridesLegacyFlagAndUnknownModesFailClosed()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.Configuration["ForgeDock:Oidc:Enabled"] = "true";
        builder.Configuration["ForgeDock:Auth:Mode"] = "token";
        Assert.Equal("token", ManagementAuthentication.GetMode(builder.Configuration));
        builder.Configuration["ForgeDock:Auth:Mode"] = "keycloak";
        builder.Configuration["ForgeDock:Oidc:Enabled"] = "false";
        Assert.Equal("keycloak", ManagementAuthentication.GetMode(builder.Configuration));
        builder.Configuration["ForgeDock:Auth:Mode"] = "unknown-provider";
        Assert.Throws<InvalidOperationException>(() => builder.AddManagementAuthentication());
    }

    private sealed class SsoFixture : IDisposable
    {
        private readonly RSA rsa = RSA.Create(2048);
        private readonly WebApplicationFactory<Program> factory;
        public string Nonce { get; set; } = "";
        public string Subject { get; set; } = "alice";
        public string TokenIssuer { get; set; } = "https://issuer.example";
        public string TokenAudience { get; set; } = "forgedock-test";

        public SsoFixture(string mode = "oidc")
        {
            var key = new RsaSecurityKey(rsa) { KeyId = "test-signing-key" };
            factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureLogging(logging => logging.AddConsole());
                builder.UseSetting(
                    "ConnectionStrings:ForgeDock",
                    "Host=localhost;Database=unused;Username=unused;Password=unused"
                );
                builder.UseSetting("ForgeDock:SecretKey", Convert.ToBase64String(new byte[32]));
                builder.UseSetting(
                    "ForgeDock:RuntimePath",
                    Path.Combine(Path.GetTempPath(), "forgedock-sso-tests")
                );
                builder.UseSetting("ForgeDock:Oidc:Enabled", "true");
                builder.UseSetting("ForgeDock:Auth:Mode", mode);
                builder.UseSetting("ForgeDock:Oidc:Authority", "https://issuer.example");
                builder.UseSetting("ForgeDock:Oidc:PublicOrigin", "https://dashboard.example");
                builder.UseSetting("ForgeDock:Oidc:ClientId", "forgedock-test");
                builder.UseSetting("ForgeDock:Oidc:ClientSecret", "test-client-secret");
                builder.UseSetting("ForgeDock:Oidc:AllowedSubjects:0", "alice");
                builder.ConfigureServices(services =>
                    services.PostConfigure<OpenIdConnectOptions>(
                        ManagementAuthentication.OidcScheme,
                        options =>
                        {
                            var metadata = new OpenIdConnectConfiguration
                            {
                                Issuer = "https://issuer.example",
                                AuthorizationEndpoint = "https://issuer.example/authorize",
                                TokenEndpoint = "https://issuer.example/token",
                            };
                            metadata.SigningKeys.Add(key);
                            options.Configuration = metadata;
                            options.ConfigurationManager =
                                new StaticConfigurationManager<OpenIdConnectConfiguration>(
                                    metadata
                                );
                            options.Backchannel = new HttpClient(
                                new TokenHandler(() =>
                                {
                                    var token = new JsonWebTokenHandler().CreateToken(
                                        new SecurityTokenDescriptor
                                        {
                                            Issuer = TokenIssuer,
                                            Audience = TokenAudience,
                                            Subject = new ClaimsIdentity([
                                                new Claim("sub", Subject),
                                                new Claim("name", "Alice"),
                                                new Claim("nonce", Nonce),
                                            ]),
                                            Expires = DateTime.UtcNow.AddMinutes(5),
                                            SigningCredentials = new SigningCredentials(
                                                key,
                                                SecurityAlgorithms.RsaSha256
                                            ),
                                        }
                                    );
                                    return new
                                    {
                                        id_token = token,
                                        access_token = "access-token",
                                        token_type = "Bearer",
                                        expires_in = 300,
                                    };
                                })
                            );
                        }
                    )
                );
            });
        }

        public HttpClient Client() =>
            factory.CreateClient(
                new WebApplicationFactoryClientOptions
                {
                    BaseAddress = new Uri("https://dashboard.example"),
                    AllowAutoRedirect = false,
                }
            );

        public void Dispose()
        {
            factory.Dispose();
            rsa.Dispose();
        }
    }

    private sealed class TokenHandler(Func<object> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(response()),
                }
            );
    }
}
