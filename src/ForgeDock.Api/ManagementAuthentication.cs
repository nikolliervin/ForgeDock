using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

namespace ForgeDock.Api;

public static class ManagementAuthentication
{
    public const string CookieScheme = "ManagementSession";
    public const string OidcScheme = "ManagementSso";

    public static bool AddManagementAuthentication(this WebApplicationBuilder builder)
    {
        var config = builder.Configuration;
        var mode = GetMode(config);
        var sso = mode != "token";
        if (!sso)
        {
            var token = config["ForgeDock:ApiToken"];
            if (string.IsNullOrWhiteSpace(token) || token.Length < 32)
                throw new InvalidOperationException(
                    "Set ForgeDock__ApiToken to a random token of at least 32 characters, or enable OIDC."
                );
            builder
                .Services.AddAuthentication("ManagementToken")
                .AddScheme<AuthenticationSchemeOptions, ApiTokenHandler>(
                    "ManagementToken",
                    _ => { }
                );
            return false;
        }

        string Required(string name) =>
            !string.IsNullOrWhiteSpace(config[$"ForgeDock:Oidc:{name}"])
                ? config[$"ForgeDock:Oidc:{name}"]!
                : throw new InvalidOperationException($"Set ForgeDock__Oidc__{name} for SSO.");
        var authority = Required("Authority");
        if (
            !Uri.TryCreate(authority, UriKind.Absolute, out var issuer)
            || issuer.Scheme != "https"
            || !string.IsNullOrEmpty(issuer.UserInfo)
            || !string.IsNullOrEmpty(issuer.Query)
            || !string.IsNullOrEmpty(issuer.Fragment)
        )
            throw new InvalidOperationException(
                "ForgeDock__Oidc__Authority must be an HTTPS issuer URL."
            );
        var publicOrigin = Required("PublicOrigin");
        if (
            !Uri.TryCreate(publicOrigin, UriKind.Absolute, out var origin)
            || origin.Scheme != "https"
            || !string.IsNullOrEmpty(origin.UserInfo)
            || origin.AbsolutePath != "/"
            || !string.IsNullOrEmpty(origin.Query)
            || !string.IsNullOrEmpty(origin.Fragment)
        )
            throw new InvalidOperationException(
                "ForgeDock__Oidc__PublicOrigin must be an HTTPS origin without a path, query, or fragment."
            );
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            foreach (
                var proxy in config.GetSection("ForgeDock:Oidc:TrustedProxies").Get<string[]>() ??
                    []
            )
                options.KnownProxies.Add(IPAddress.Parse(proxy));
        });
        var clientId = Required("ClientId");
        var clientSecret = Required("ClientSecret");
        if (AllowedSubjects(config).Length == 0)
            throw new InvalidOperationException(
                "Set ForgeDock__Oidc__AllowedSubjects__0 to an approved user's OIDC subject. SSO denies access by default."
            );

        builder.Services.AddSingleton<ManagementTicketStore>();
        builder
            .Services.AddOptions<CookieAuthenticationOptions>(CookieScheme)
            .Configure<ManagementTicketStore>((options, store) => options.SessionStore = store);
        var runtime =
            config["ForgeDock:RuntimePath"]
            ?? Path.Combine(builder.Environment.ContentRootPath, ".runtime");
        builder
            .Services.AddDataProtection()
            .SetApplicationName("ForgeDock.Management")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(runtime, "auth-keys")));
        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-Token";
            options.Cookie.Name = "__Host-ForgeDock.Csrf";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
        builder
            .Services.AddAuthentication(CookieScheme)
            .AddCookie(
                CookieScheme,
                options =>
                {
                    options.Cookie.Name = "__Host-ForgeDock.Session";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    options.ExpireTimeSpan = TimeSpan.FromHours(8);
                    options.SlidingExpiration = false;
                    options.Events.OnRedirectToLogin = context =>
                    {
                        context.Response.StatusCode = 401;
                        return Task.CompletedTask;
                    };
                    options.Events.OnRedirectToAccessDenied = context =>
                    {
                        context.Response.StatusCode = 403;
                        return Task.CompletedTask;
                    };
                    options.Events.OnValidatePrincipal = async context =>
                    {
                        if (!IsAllowed(context.Principal, config))
                        {
                            context.RejectPrincipal();
                            await context.HttpContext.SignOutAsync(CookieScheme);
                        }
                    };
                }
            )
            .AddOpenIdConnect(
                OidcScheme,
                options =>
                {
                    options.Authority = authority;
                    options.ClientId = clientId;
                    options.ClientSecret = clientSecret;
                    options.SignInScheme = CookieScheme;
                    options.CallbackPath = "/api/auth/callback";
                    options.ResponseType = "code";
                    options.UsePkce = true;
                    options.RequireHttpsMetadata = true;
                    options.SaveTokens = false;
                    options.MapInboundClaims = false;
                    options.Scope.Clear();
                    options.Scope.Add("openid");
                    options.Scope.Add("profile");
                    options.TokenValidationParameters.NameClaimType = "name";
                    options.Events.OnRedirectToIdentityProvider = context =>
                    {
                        context.ProtocolMessage.RedirectUri =
                            origin.GetLeftPart(UriPartial.Authority) + "/api/auth/callback";
                        return Task.CompletedTask;
                    };
                    options.Events.OnTokenValidated = context =>
                    {
                        if (!IsAllowed(context.Principal, config))
                            context.Fail("This account is not authorized for ForgeDock.");
                        return Task.CompletedTask;
                    };
                    options.Events.OnRemoteFailure = context =>
                    {
                        context.HandleResponse();
                        context.Response.Redirect("/?authError=sso");
                        return Task.CompletedTask;
                    };
                }
            );
        return true;
    }

    public static string GetMode(IConfiguration config)
    {
        // Preserve older installations until they explicitly choose a mode.
        var mode = (
            config["ForgeDock:Auth:Mode"]
            ?? (config.GetValue<bool>("ForgeDock:Oidc:Enabled") ? "oidc" : "token")
        )
            .Trim()
            .ToLowerInvariant();
        return mode switch
        {
            "token" or "keycloak" or "oidc" or "entra" or "auth0" or "okta" or "authentik" => mode,
            _ => throw new InvalidOperationException(
                "ForgeDock__Auth__Mode must be token, keycloak, oidc, entra, auth0, okta, or authentik."
            ),
        };
    }

    internal static string[] AllowedSubjects(IConfiguration config) =>
        (config.GetSection("ForgeDock:Oidc:AllowedSubjects").Get<string[]>() ?? [])
            .Where(subject => !string.IsNullOrWhiteSpace(subject))
            .ToArray();

    internal static bool IsAllowed(ClaimsPrincipal? principal, IConfiguration config) =>
        principal?.FindFirst("sub")?.Value is { Length: > 0 } subject
        && AllowedSubjects(config).Contains(subject, StringComparer.Ordinal);

    public static void MapManagementAuthentication(this WebApplication app, bool sso)
    {
        app.MapGet(
                "/api/auth/config",
                () => new { mode = sso ? "sso" : "token", provider = GetMode(app.Configuration) }
            )
            .AllowAnonymous();
        if (!sso)
            return;
        app.MapGet(
                "/api/auth/login",
                (HttpContext context) =>
                {
                    if (!context.Request.IsHttps)
                        return Results.Redirect(
                            app.Configuration["ForgeDock:Oidc:PublicOrigin"]!.TrimEnd('/')
                                + "/api/auth/login"
                        );
                    return Results.Challenge(
                        new AuthenticationProperties { RedirectUri = "/dashboard" },
                        [OidcScheme]
                    );
                }
            )
            .AllowAnonymous();
        app.MapPost(
                "/api/auth/logout",
                async (HttpContext context) =>
                {
                    await context.SignOutAsync(CookieScheme);
                    return Results.NoContent();
                }
            )
            .RequireAuthorization();
    }

    public static async Task ValidateManagementCsrf(HttpContext context, RequestDelegate next)
    {
        if (
            context.Request.Path.StartsWithSegments("/api")
            && context.User.Identity?.IsAuthenticated == true
            && !HttpMethods.IsGet(context.Request.Method)
            && !HttpMethods.IsHead(context.Request.Method)
            && !HttpMethods.IsOptions(context.Request.Method)
            && context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is null
        )
        {
            try
            {
                await context
                    .RequestServices.GetRequiredService<IAntiforgery>()
                    .ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(
                    new { error = "Invalid or missing CSRF token. Reload the page and try again." }
                );
                return;
            }
        }
        await next(context);
    }
}

// Only an opaque session reference reaches the browser. Logout removes the server ticket,
// so replaying a copied cookie after logout fails. Restarts invalidate active sessions.
public sealed class ManagementTicketStore : ITicketStore
{
    private readonly ConcurrentDictionary<string, AuthenticationTicket> tickets = new();

    public Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        foreach (var entry in tickets)
            if (entry.Value.Properties.ExpiresUtc <= DateTimeOffset.UtcNow)
                tickets.TryRemove(entry.Key, out _);
        var key = Convert.ToHexString(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)
        );
        tickets[key] = ticket;
        return Task.FromResult(key);
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        tickets[key] = ticket;
        return Task.CompletedTask;
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        if (
            tickets.TryGetValue(key, out var ticket)
            && ticket.Properties.ExpiresUtc > DateTimeOffset.UtcNow
        )
            return Task.FromResult<AuthenticationTicket?>(ticket);
        tickets.TryRemove(key, out _);
        return Task.FromResult<AuthenticationTicket?>(null);
    }

    public Task RemoveAsync(string key)
    {
        tickets.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
