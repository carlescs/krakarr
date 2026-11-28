using System.Globalization;
using System.Text;
using Krakarr.Core.Domain;
using Krakarr.Core.Telemetry;
using Krakarr.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Host.Auth.Passkeys;

/// <summary>A <c>PublicKeyCredential</c> serialized with base64url fields (as <c>PublicKeyCredential.toJSON()</c>).</summary>
public sealed record PasskeyCredentialJson(string Id, string RawId, string Type, PasskeyResponseJson Response);

/// <summary>Attestation fields are set on registration, assertion fields on sign-in.</summary>
public sealed record PasskeyResponseJson(
    string ClientDataJson,
    string? AttestationObject,
    string? AuthenticatorData,
    string? Signature,
    string? UserHandle,
    string[]? Transports);

public sealed record PasskeyRegistrationOptionsRequest(string CurrentPassword);

public sealed record PasskeyRegistrationRequest(string Name, PasskeyCredentialJson Credential);

public sealed record PasskeySignInRequest(PasskeyCredentialJson Credential);

public sealed record PasskeyRenameRequest(string Name);

public sealed record PasskeyDto(int Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <summary>
/// Passkey (WebAuthn) sign-in. Users add passkeys from their account page after confirming their password, and can
/// then sign in with one instead of a username and password. Passkeys are discoverable, so the browser offers the
/// user's accounts itself, and always verify the user (PIN or biometrics), which makes them two factors in one.
/// </summary>
public static class PasskeyEndpoints
{
    private const string LogCategory = "Krakarr.Auth";
    public const int MaxNameLength = 100;

    /// <summary>A user can't pile up passkeys without bound.</summary>
    public const int MaxPasskeysPerUser = 20;

    private static readonly int TimeoutMs = (int)PasskeyChallenges.Lifetime.TotalMilliseconds;

    public static IEndpointRouteBuilder MapPasskeyEndpoints(this IEndpointRouteBuilder app)
    {
        var anonymous = app.MapGroup("/api/v1/auth/passkeys").WithTags("Auth").AllowAnonymous();
        var signedIn = app.MapGroup("/api/v1/auth/passkeys").WithTags("Auth");

        anonymous.MapPost("/login/options", (HttpContext http, PasskeyChallenges challenges) =>
        {
            if (RpId(http) is not { } rpId) return NoRpId();
            var challenge = challenges.CreateForSignIn(http.Connection.RemoteIpAddress, rpId);

            // No allowCredentials: the passkeys are discoverable, so the browser lists the accounts it holds for us.
            return Results.Ok(new { challenge, rpId, timeout = TimeoutMs, userVerification = "required" });
        }).RequireRateLimiting(AuthEndpoints.RateLimitPolicy);

        anonymous.MapPost("/login", async (PasskeySignInRequest req, HttpContext http, KrakarrDbContext db, PasskeyChallenges challenges,
            ILoggerFactory loggers, TimeProvider time, CancellationToken ct) =>
        {
            var logger = loggers.CreateLogger(LogCategory);
            if (RpId(http) is not { } rpId) return NoRpId();

            UserPasskey? passkey = null;
            try
            {
                var response = req.Credential?.Response ?? throw new WebAuthnException("The passkey response is incomplete.");
                var credentialId = WebAuthn.FromBase64Url(req.Credential.RawId);
                passkey = await db.UserPasskeys.Include(p => p.User).FirstOrDefaultAsync(p => p.CredentialId == credentialId, ct);

                if (passkey?.User is null)
                {
                    throw new WebAuthnException("This passkey isn't registered with Krakarr. Sign in with your password and add it again.");
                }

                if (!string.IsNullOrEmpty(response.UserHandle)
                    && !WebAuthn.FromBase64Url(response.UserHandle).AsSpan().SequenceEqual(UserHandle(passkey.UserId)))
                {
                    throw new WebAuthnException("This passkey belongs to a different account.");
                }

                passkey.SignCount = WebAuthn.VerifyAssertion(WebAuthn.FromBase64Url(response.ClientDataJson), WebAuthn.FromBase64Url(response.AuthenticatorData),
                    WebAuthn.FromBase64Url(response.Signature), passkey.PublicKey, passkey.SignCount, rpId,
                    c => challenges.Take(c, null, rpId));
            }
            catch (WebAuthnException ex)
            {
                logger.LogWarning("Failed passkey sign-in{User} from {Ip}: {Reason}",
                    passkey?.User is { } u ? $" for {u.Username}" : string.Empty, ClientIp(http), ex.Message);
                KrakarrTelemetry.Logins.Add(1, new KeyValuePair<string, object?>(KrakarrTelemetry.OutcomeTag, KrakarrTelemetry.Failure));
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status401Unauthorized);
            }

            passkey.LastUsedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await AuthEndpoints.SignInAsync(http, passkey.User);
            logger.LogInformation("{Username} signed in with passkey '{Passkey}' from {Ip}", passkey.User.Username, passkey.Name, ClientIp(http));
            KrakarrTelemetry.Logins.Add(1, new KeyValuePair<string, object?>(KrakarrTelemetry.OutcomeTag, KrakarrTelemetry.Success));
            return Results.NoContent();
        }).RequireRateLimiting(AuthEndpoints.RateLimitPolicy);

        signedIn.MapGet("/", async (HttpContext http, KrakarrDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.GetUserId();
            if (userId is null) return Results.NotFound();
            var list = await db.UserPasskeys.AsNoTracking()
                .Where(p => p.UserId == userId)
                .OrderBy(p => p.CreatedAt)
                .Select(p => new PasskeyDto(p.Id, p.Name, p.CreatedAt, p.LastUsedAt))
                .ToListAsync(ct);
            return Results.Ok(list);
        });

        // Adding a passkey creates a new way into the account, so it needs the password, not just a session cookie.
        signedIn.MapPost("/register/options", async (PasskeyRegistrationOptionsRequest req, HttpContext http, KrakarrDbContext db,
            IPasswordHasher<User> hasher, PasskeyChallenges challenges, CancellationToken ct) =>
        {
            var userId = http.User.GetUserId();
            var user = userId is null ? null : await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null) return Results.NotFound();
            if (RpId(http) is not { } rpId) return NoRpId();

            if (hasher.VerifyHashedPassword(user, user.PasswordHash, req.CurrentPassword ?? string.Empty) == PasswordVerificationResult.Failed)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["currentPassword"] = ["Current password is incorrect"] });
            }

            var existing = await db.UserPasskeys.AsNoTracking()
                .Where(p => p.UserId == user.Id)
                .Select(p => new { p.CredentialId, p.Transports })
                .ToListAsync(ct);
            if (existing.Count >= MaxPasskeysPerUser) return TooMany();
            var challenge = challenges.CreateForRegistration(user.Id, rpId);

            return Results.Ok(new
            {
                challenge,
                rp = new { id = rpId, name = "Krakarr" },
                user = new { id = WebAuthn.ToBase64Url(UserHandle(user.Id)), name = user.Username, displayName = user.Username },
                pubKeyCredParams = WebAuthn.SupportedAlgorithms.Select(alg => new { type = "public-key", alg }),
                timeout = TimeoutMs,
                attestation = "none",
                authenticatorSelection = new { residentKey = "required", requireResidentKey = true, userVerification = "required" },
                // Stops the same authenticator from being registered twice.
                excludeCredentials = existing.Select(p => new
                {
                    type = "public-key",
                    id = WebAuthn.ToBase64Url(p.CredentialId),
                    transports = SplitTransports(p.Transports),
                }),
            });
        }).RequireRateLimiting(AuthEndpoints.RateLimitPolicy);

        signedIn.MapPost("/", async (PasskeyRegistrationRequest req, HttpContext http, KrakarrDbContext db, PasskeyChallenges challenges,
            ILoggerFactory loggers, CancellationToken ct) =>
        {
            var userId = http.User.GetUserId();
            if (userId is null) return Results.NotFound();
            if (RpId(http) is not { } rpId) return NoRpId();
            if (ValidateName(req.Name) is { } problem) return problem;
            if (await db.UserPasskeys.CountAsync(p => p.UserId == userId, ct) >= MaxPasskeysPerUser) return TooMany();

            AttestedCredential credential;
            try
            {
                var response = req.Credential?.Response ?? throw new WebAuthnException("The passkey response is incomplete.");
                credential = WebAuthn.VerifyRegistration(WebAuthn.FromBase64Url(response.ClientDataJson),
                    WebAuthn.FromBase64Url(response.AttestationObject), rpId, c => challenges.Take(c, userId, rpId));
                if (!credential.CredentialId.AsSpan().SequenceEqual(WebAuthn.FromBase64Url(req.Credential.RawId)))
                {
                    throw new WebAuthnException("The passkey response doesn't match its credential id.");
                }
            }
            catch (WebAuthnException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }

            if (await db.UserPasskeys.AnyAsync(p => p.CredentialId == credential.CredentialId, ct))
            {
                return Results.Problem("This passkey is already registered.", statusCode: StatusCodes.Status409Conflict);
            }

            var transports = req.Credential.Response.Transports is { Length: > 0 } t
                ? string.Join(',', t.Where(IsTransport).Distinct().Take(8))
                : null;
            var passkey = new UserPasskey
            {
                UserId = userId.Value,
                CredentialId = credential.CredentialId,
                PublicKey = credential.PublicKey,
                SignCount = credential.SignCount,
                Transports = string.IsNullOrEmpty(transports) ? null : transports,
                Name = req.Name.Trim(),
            };
            db.UserPasskeys.Add(passkey);
            await db.SaveChangesAsync(ct);
            loggers.CreateLogger(LogCategory).LogInformation("{Username} added passkey '{Passkey}' from {Ip}",
                http.User.Identity?.Name, passkey.Name, ClientIp(http));
            return Results.Created($"/api/v1/auth/passkeys/{passkey.Id}", new PasskeyDto(passkey.Id, passkey.Name, passkey.CreatedAt, null));
        });

        signedIn.MapPut("/{id:int}", async (int id, PasskeyRenameRequest req, HttpContext http, KrakarrDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.GetUserId();
            var passkey = await db.UserPasskeys.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId, ct);
            if (passkey is null) return Results.NotFound();
            if (ValidateName(req.Name) is { } problem) return problem;

            passkey.Name = req.Name.Trim();
            await db.SaveChangesAsync(ct);
            return Results.Ok(new PasskeyDto(passkey.Id, passkey.Name, passkey.CreatedAt, passkey.LastUsedAt));
        });

        signedIn.MapDelete("/{id:int}", async (int id, HttpContext http, KrakarrDbContext db, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var userId = http.User.GetUserId();
            var deleted = await db.UserPasskeys.Where(p => p.Id == id && p.UserId == userId).ExecuteDeleteAsync(ct);
            if (deleted == 0) return Results.NotFound();
            loggers.CreateLogger(LogCategory).LogInformation("{Username} removed a passkey from {Ip}", http.User.Identity?.Name, ClientIp(http));
            return Results.NoContent();
        });

        return app;
    }

    /// <summary>WebAuthn user handle: the user id. Not personal data, and stable across username changes.</summary>
    internal static byte[] UserHandle(int userId) => Encoding.UTF8.GetBytes(userId.ToString(CultureInfo.InvariantCulture));

    private static string? RpId(HttpContext http) => WebAuthn.RpIdFor(http.Request.Host.Host);

    private static string ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static readonly HashSet<string> KnownTransports = new(StringComparer.Ordinal) { "usb", "nfc", "ble", "smart-card", "hybrid", "internal" };

    private static bool IsTransport(string? value) => value is not null && KnownTransports.Contains(value);

    private static string[] SplitTransports(string? value) =>
        string.IsNullOrEmpty(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries);

    private static IResult? ValidateName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Name is required"] })
        : name.Trim().Length > MaxNameLength ? Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = [$"Name must be at most {MaxNameLength} characters"] })
        : null;

    private static IResult NoRpId() => Results.Problem(
        "Passkeys need Krakarr to be opened through a domain name (not an IP address) over HTTPS, or on localhost.",
        statusCode: StatusCodes.Status400BadRequest);

    private static IResult TooMany() => Results.Problem($"You can have at most {MaxPasskeysPerUser} passkeys. Remove one first.",
        statusCode: StatusCodes.Status409Conflict);
}
