using System.Security.Claims;
using System.Text.Encodings.Web;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Krakarr.Host.Auth;

public static class AuthConstants
{
    public const string ApiKeyScheme = "ApiKey";
    public const string CookieScheme = "Cookies";
    public const string SmartScheme = "Smart";
    public const string ApiKeyHeader = "X-Api-Key";
    public const string AdminPolicy = "Admin";
}

/// <summary>Authenticates requests carrying the instance API key (header X-Api-Key or ?apikey=).</summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ISettingsService settings) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var provided = GetProvidedKey(Request);
        if (string.IsNullOrEmpty(provided))
        {
            return AuthenticateResult.NoResult();
        }

        var general = await settings.GetAsync<GeneralSettings>(Context.RequestAborted);
        if (string.IsNullOrEmpty(general.ApiKey)
            || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(provided), System.Text.Encoding.UTF8.GetBytes(general.ApiKey)))
        {
            return AuthenticateResult.Fail("Invalid API key");
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "api"), new Claim(ClaimTypes.Role, nameof(UserRole.Admin))], AuthConstants.ApiKeyScheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AuthConstants.ApiKeyScheme));
    }

    public static string? GetProvidedKey(HttpRequest request)
    {
        if (request.Headers.TryGetValue(AuthConstants.ApiKeyHeader, out var header) && !string.IsNullOrEmpty(header))
        {
            return header.ToString();
        }

        return request.Query.TryGetValue("apikey", out var query) ? query.ToString() : null;
    }
}
