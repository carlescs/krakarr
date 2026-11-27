using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Krakarr.Host.Push;

/// <summary>A VAPID key pair (RFC 8292): identifies this Krakarr instance to push services.</summary>
/// <param name="PublicKey">Uncompressed P-256 point (65 bytes, base64url): the browser's <c>applicationServerKey</c>.</param>
/// <param name="PrivateKey">The private scalar (32 bytes, base64url).</param>
public sealed record VapidKeys(string PublicKey, string PrivateKey)
{
    public static VapidKeys Generate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(includePrivateParameters: true);
        return new VapidKeys(WebEncoders.Base64UrlEncode(WebPushCrypto.Uncompressed(p.Q)), WebEncoders.Base64UrlEncode(p.D!));
    }
}

/// <summary>
/// The two pieces of Web Push that need cryptography, done with the framework's primitives: encrypting the payload for
/// one device (RFC 8291, <c>aes128gcm</c> from RFC 8188) and the VAPID <c>Authorization</c> header (RFC 8292).
/// </summary>
public static class WebPushCrypto
{
    /// <summary>Record size written in the header; a single record always fits since payloads are capped well below it.</summary>
    private const int RecordSize = 4096;

    /// <summary>
    /// Largest plaintext we send. Push services accept 4096 bytes of encrypted body; the header (86 bytes), the padding
    /// delimiter and the GCM tag take 103 of them.
    /// </summary>
    public const int MaxPayload = 3993;

    /// <summary>Encrypts <paramref name="plaintext"/> for the device that gave <paramref name="p256dh"/> and <paramref name="auth"/> (base64url).</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, string p256dh, string auth)
    {
        if (plaintext.Length > MaxPayload) throw new ArgumentException($"Push payloads are limited to {MaxPayload} bytes", nameof(plaintext));
        var uaPublic = WebEncoders.Base64UrlDecode(p256dh);
        var authSecret = WebEncoders.Base64UrlDecode(auth);
        if (uaPublic.Length != 65 || uaPublic[0] != 0x04) throw new ArgumentException("Invalid p256dh key", nameof(p256dh));
        if (authSecret.Length < 16) throw new ArgumentException("Invalid auth secret", nameof(auth));

        using var ua = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = Point(uaPublic) });
        using var local = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var asPublic = Uncompressed(local.ExportParameters(false).Q);
        var ecdhSecret = local.DeriveRawSecretAgreement(ua.PublicKey);

        // IKM = HKDF(auth_secret, ecdh_secret, "WebPush: info" || 0x00 || ua_public || as_public, 32)
        var keyInfo = Concat("WebPush: info\0"u8, uaPublic, asPublic);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, 32, authSecret, keyInfo);

        var salt = RandomNumberGenerator.GetBytes(16);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, "Content-Encoding: nonce\0"u8.ToArray());

        // Header: salt (16) || record size (4) || key id length (1) || key id (the sender's public key, 65).
        var headerLength = 16 + 4 + 1 + asPublic.Length;
        var body = new byte[headerLength + plaintext.Length + 1 + 16];
        salt.CopyTo(body, 0);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(16), RecordSize);
        body[20] = (byte)asPublic.Length;
        asPublic.CopyTo(body, 21);

        // The only (so last) record: plaintext followed by the 0x02 delimiter and no padding.
        var record = new byte[plaintext.Length + 1];
        plaintext.CopyTo(record);
        record[^1] = 0x02;
        using var gcm = new AesGcm(cek, 16);
        gcm.Encrypt(nonce, record, body.AsSpan(headerLength, record.Length), body.AsSpan(headerLength + record.Length, 16));
        return body;
    }

    /// <summary>
    /// <c>Authorization</c> header value for a request to <paramref name="endpoint"/>: a JWT signed with the VAPID
    /// private key whose audience is the push service's origin, plus the public key.
    /// </summary>
    public static string VapidAuthorization(Uri endpoint, VapidKeys keys, string subject, DateTimeOffset now)
    {
        var header = Base64Url("""{"typ":"JWT","alg":"ES256"}"""u8);
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["aud"] = endpoint.GetLeftPart(UriPartial.Authority),
            ["exp"] = now.AddHours(12).ToUnixTimeSeconds(),
            ["sub"] = subject,
        }));
        var unsigned = $"{header}.{claims}";

        var publicKey = WebEncoders.Base64UrlDecode(keys.PublicKey);
        using var ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = Point(publicKey),
            D = WebEncoders.Base64UrlDecode(keys.PrivateKey),
        });
        // ES256 wants r || s (IEEE P1363), which is .NET's default signature format.
        var signature = ecdsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256);
        return $"vapid t={unsigned}.{Base64Url(signature)}, k={keys.PublicKey}";
    }

    internal static byte[] Uncompressed(ECPoint q) => [0x04, .. q.X!, .. q.Y!];

    internal static ECPoint Point(byte[] uncompressed)
    {
        if (uncompressed.Length != 65 || uncompressed[0] != 0x04) throw new ArgumentException("Expected an uncompressed P-256 point");
        return new ECPoint { X = uncompressed[1..33], Y = uncompressed[33..] };
    }

    private static string Base64Url(ReadOnlySpan<byte> data) => WebEncoders.Base64UrlEncode(data.ToArray());

    private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c) => [.. a, .. b, .. c];
}
