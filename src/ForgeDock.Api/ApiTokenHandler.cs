using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ForgeDock.Api;

public sealed class ApiTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return Task.FromResult(AuthenticateResult.NoResult());
        var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(header[7..]));
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(configuration["ForgeDock:ApiToken"]!));
        if (!CryptographicOperations.FixedTimeEquals(supplied, expected))
            return Task.FromResult(AuthenticateResult.Fail("Invalid management token."));
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "operator")], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
