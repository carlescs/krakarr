using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Krakarr.Host.Auth.Passkeys;

/// <summary>A WebAuthn ceremony was rejected; <see cref="Exception.Message"/> is safe to show to the user.</summary>
public sealed class WebAuthnException(string message) : Exception(message);

/// <summary>A credential's public key and id, read from a registration response.</summary>
public sealed record AttestedCredential(byte[] CredentialId, byte[] PublicKey, long SignCount);

/// <summary>
/// The parts of WebAuthn Level 3 that Krakarr needs to verify passkeys: registration with <c>none</c> attestation (we
/// trust the user, not the authenticator model) and assertions signed with ES256 or RS256, which every passkey provider
/// supports. Stateless and pure, so it can be unit tested without a browser.
/// </summary>
public static class WebAuthn
{
    /// <summary>COSE algorithm ids we accept, in order of preference.</summary>
    public const int Es256 = -7;
    public const int Rs256 = -257;
    public static readonly int[] SupportedAlgorithms = [Es256, Rs256];

    /// <summary>Spec limit for credential ids.</summary>
    public const int MaxCredentialIdLength = 1023;

    private const byte FlagUserPresent = 0x01;
    private const byte FlagUserVerified = 0x04;
    private const byte FlagAttestedCredentialData = 0x40;

    /// <summary>
    /// Relying party id for a host name: the host itself. Returns null for IP addresses, which browsers refuse as RP
    /// ids, so passkeys need Krakarr to be reached through a domain name (or <c>localhost</c>).
    /// </summary>
    public static string? RpIdFor(string host)
    {
        if (string.IsNullOrEmpty(host)) return null;
        var trimmed = host.Trim('[', ']');
        return IPAddress.TryParse(trimmed, out _) ? null : host.ToLowerInvariant();
    }

    /// <summary>Base64url without padding, as WebAuthn JSON uses.</summary>
    public static string ToBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string? value)
    {
        if (string.IsNullOrEmpty(value)) throw new WebAuthnException("The passkey response is incomplete.");
        var s = value.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        try
        {
            return Convert.FromBase64String(s);
        }
        catch (FormatException)
        {
            throw new WebAuthnException("The passkey response is malformed.");
        }
    }

    /// <summary>Verifies a registration (<c>navigator.credentials.create</c>) response and returns the new credential.</summary>
    /// <param name="isExpectedChallenge">Checks (and consumes) the challenge found in the client data.</param>
    public static AttestedCredential VerifyRegistration(byte[] clientDataJson, byte[] attestationObject, string rpId,
        Func<string, bool> isExpectedChallenge)
    {
        VerifyClientData(clientDataJson, "webauthn.create", rpId, isExpectedChallenge);

        byte[] authData;
        try
        {
            // attestationObject = { fmt, attStmt, authData }. The attestation statement isn't verified: we ask for
            // "none" and don't restrict which authenticators users may pick.
            var reader = new CborReader(attestationObject, CborConformanceMode.Lax);
            authData = null!;
            var count = reader.ReadStartMap();
            for (var i = 0; count is null || i < count; i++)
            {
                if (count is null && reader.PeekState() == CborReaderState.EndMap) break;
                var key = reader.ReadTextString();
                if (key == "authData") authData = reader.ReadByteString();
                else reader.SkipValue();
            }

            if (authData is null) throw new WebAuthnException("The passkey response has no authenticator data.");
        }
        catch (CborContentException)
        {
            throw new WebAuthnException("The passkey response is malformed.");
        }
        catch (InvalidOperationException)
        {
            throw new WebAuthnException("The passkey response is malformed.");
        }

        var flags = VerifyAuthenticatorData(authData, rpId, out var signCount);
        if ((flags & FlagAttestedCredentialData) == 0 || authData.Length < 37 + 16 + 2)
        {
            throw new WebAuthnException("The passkey response has no credential.");
        }

        var idLength = BinaryPrimitives.ReadUInt16BigEndian(authData.AsSpan(37 + 16, 2));
        var idStart = 37 + 16 + 2;
        if (idLength is 0 or > MaxCredentialIdLength || authData.Length < idStart + idLength + 1)
        {
            throw new WebAuthnException("The passkey response has an invalid credential id.");
        }

        var credentialId = authData.AsSpan(idStart, idLength).ToArray();
        var keyBytes = authData.AsMemory(idStart + idLength);
        byte[] publicKey;
        try
        {
            // The COSE key is followed by optional extensions; read exactly one CBOR item.
            var reader = new CborReader(keyBytes, CborConformanceMode.Lax, allowMultipleRootLevelValues: true);
            reader.SkipValue();
            publicKey = keyBytes[..(keyBytes.Length - reader.BytesRemaining)].ToArray();
        }
        catch (CborContentException)
        {
            throw new WebAuthnException("The passkey public key is malformed.");
        }

        // Fails for unsupported algorithms or malformed keys.
        using (CreateVerifier(publicKey))
        {
        }

        return new AttestedCredential(credentialId, publicKey, signCount);
    }

    /// <summary>
    /// Verifies an authentication (<c>navigator.credentials.get</c>) response against a stored public key and returns
    /// the authenticator's new signature counter.
    /// </summary>
    public static long VerifyAssertion(byte[] clientDataJson, byte[] authenticatorData, byte[] signature, byte[] publicKey,
        long storedSignCount, string rpId, Func<string, bool> isExpectedChallenge)
    {
        VerifyClientData(clientDataJson, "webauthn.get", rpId, isExpectedChallenge);
        VerifyAuthenticatorData(authenticatorData, rpId, out var signCount);

        var signed = new byte[authenticatorData.Length + SHA256.HashSizeInBytes];
        authenticatorData.CopyTo(signed, 0);
        SHA256.HashData(clientDataJson, signed.AsSpan(authenticatorData.Length));

        using var verifier = CreateVerifier(publicKey);
        if (!verifier.Verify(signed, signature)) throw new WebAuthnException("The passkey signature is invalid.");

        // A counter that doesn't move forward suggests a cloned authenticator. Synced passkeys always report 0.
        if ((signCount != 0 || storedSignCount != 0) && signCount <= storedSignCount)
        {
            throw new WebAuthnException("This passkey's signature counter went backwards, so it may have been cloned. Remove it and add it again.");
        }

        return signCount;
    }

    private static void VerifyClientData(byte[] clientDataJson, string expectedType, string rpId, Func<string, bool> isExpectedChallenge)
    {
        string? type, challenge, origin;
        try
        {
            using var doc = JsonDocument.Parse(clientDataJson);
            var root = doc.RootElement;
            type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            challenge = root.TryGetProperty("challenge", out var c) ? c.GetString() : null;
            origin = root.TryGetProperty("origin", out var o) ? o.GetString() : null;
            if (root.TryGetProperty("crossOrigin", out var cross) && cross.ValueKind == JsonValueKind.True)
            {
                throw new WebAuthnException("Passkeys can't be used from an embedded page.");
            }
        }
        catch (JsonException)
        {
            throw new WebAuthnException("The passkey response is malformed.");
        }
        catch (InvalidOperationException)
        {
            throw new WebAuthnException("The passkey response is malformed.");
        }

        if (type != expectedType) throw new WebAuthnException("The passkey response is of the wrong type.");
        if (string.IsNullOrEmpty(challenge) || !isExpectedChallenge(challenge))
        {
            throw new WebAuthnException("The passkey request expired. Please try again.");
        }

        if (!IsValidOrigin(origin, rpId)) throw new WebAuthnException("The passkey was created for a different site.");
    }

    /// <summary>
    /// The origin must be on the RP id's host (or a subdomain), over HTTPS, or plain HTTP for localhost, which browsers
    /// treat as secure. Its port and scheme can differ from the request's (TLS often terminates at a reverse proxy).
    /// </summary>
    public static bool IsValidOrigin(string? origin, string rpId)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        if (host != rpId && !host.EndsWith("." + rpId, StringComparison.Ordinal)) return false;
        return uri.Scheme == Uri.UriSchemeHttps
            || (uri.Scheme == Uri.UriSchemeHttp && (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal)));
    }

    /// <summary>Checks the RP id hash and that the user was present and verified (PIN, biometrics); returns the flags.</summary>
    private static byte VerifyAuthenticatorData(byte[] authData, string rpId, out long signCount)
    {
        if (authData.Length < 37) throw new WebAuthnException("The passkey response is malformed.");
        var rpIdHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rpId));
        if (!CryptographicOperations.FixedTimeEquals(authData.AsSpan(0, 32), rpIdHash))
        {
            throw new WebAuthnException("The passkey was created for a different site.");
        }

        var flags = authData[32];
        if ((flags & FlagUserPresent) == 0) throw new WebAuthnException("The passkey didn't confirm you were present.");
        if ((flags & FlagUserVerified) == 0) throw new WebAuthnException("The passkey didn't verify you (PIN or biometrics).");
        signCount = BinaryPrimitives.ReadUInt32BigEndian(authData.AsSpan(33, 4));
        return flags;
    }

    private abstract class Verifier : IDisposable
    {
        public abstract bool Verify(byte[] data, byte[] signature);
        public abstract void Dispose();
    }

    private sealed class EcVerifier(ECDsa key) : Verifier
    {
        public override bool Verify(byte[] data, byte[] signature)
        {
            try
            {
                return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            }
            catch (CryptographicException)
            {
                return false;
            }
        }

        public override void Dispose() => key.Dispose();
    }

    private sealed class RsaVerifier(RSA key) : Verifier
    {
        public override bool Verify(byte[] data, byte[] signature)
        {
            try
            {
                return key.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            catch (CryptographicException)
            {
                return false;
            }
        }

        public override void Dispose() => key.Dispose();
    }

    /// <summary>Builds a verifier from a COSE_Key: EC2 P-256 with ES256, or RSA with RS256.</summary>
    private static Verifier CreateVerifier(byte[] coseKey)
    {
        long? kty = null, alg = null, crv = null;
        byte[]? x = null, y = null, n = null, e = null;
        try
        {
            var reader = new CborReader(coseKey, CborConformanceMode.Lax);
            var count = reader.ReadStartMap() ?? throw new WebAuthnException("The passkey public key is malformed.");
            for (var i = 0; i < count; i++)
            {
                if (reader.PeekState() is not (CborReaderState.UnsignedInteger or CborReaderState.NegativeInteger))
                {
                    reader.SkipValue();
                    reader.SkipValue();
                    continue;
                }

                var label = reader.ReadInt64();
                switch (label)
                {
                    case 1: kty = reader.ReadInt64(); break;
                    case 3: alg = reader.ReadInt64(); break;
                    // -1 is "crv" for EC2 keys and "n" for RSA keys; -2 is "x" or "e".
                    case -1 when reader.PeekState() == CborReaderState.ByteString: n = reader.ReadByteString(); break;
                    case -1: crv = reader.ReadInt64(); break;
                    case -2: x = e = reader.ReadByteString(); break;
                    case -3: y = reader.ReadByteString(); break;
                    default: reader.SkipValue(); break;
                }
            }
        }
        catch (Exception ex) when (ex is CborContentException or InvalidOperationException or OverflowException)
        {
            throw new WebAuthnException("The passkey public key is malformed.");
        }

        try
        {
            if (kty == 2 && alg == Es256 && crv == 1 && x is { Length: 32 } && y is { Length: 32 })
            {
                return new EcVerifier(ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } }));
            }

            if (kty == 3 && alg == Rs256 && n is { Length: >= 256 } && e is { Length: > 0 })
            {
                var rsa = RSA.Create();
                rsa.ImportParameters(new RSAParameters { Modulus = n, Exponent = e });
                return new RsaVerifier(rsa);
            }
        }
        catch (CryptographicException)
        {
            throw new WebAuthnException("The passkey public key is invalid.");
        }

        throw new WebAuthnException("This passkey uses an algorithm Krakarr doesn't support.");
    }
}
