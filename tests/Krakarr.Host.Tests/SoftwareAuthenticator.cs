using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Krakarr.Host.Auth.Passkeys;

namespace Krakarr.Host.Tests;

/// <summary>A passkey in memory: produces the WebAuthn responses a browser would send, for ES256 or RS256.</summary>
public sealed class SoftwareAuthenticator : IDisposable
{
    private readonly ECDsa? _ec;
    private readonly RSA? _rsa;
    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(16);
    public byte[]? UserHandle { get; set; }
    public uint SignCount { get; set; }
    public byte Flags { get; set; } = 0x01 | 0x04; // user present + verified

    public SoftwareAuthenticator(bool rsa = false)
    {
        if (rsa) _rsa = RSA.Create(2048);
        else _ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    }

    public byte[] CoseKey()
    {
        var w = new CborWriter();
        if (_ec is not null)
        {
            var p = _ec.ExportParameters(false);
            w.WriteStartMap(5);
            w.WriteInt32(1); w.WriteInt32(2);
            w.WriteInt32(3); w.WriteInt32(WebAuthn.Es256);
            w.WriteInt32(-1); w.WriteInt32(1);
            w.WriteInt32(-2); w.WriteByteString(p.Q.X!);
            w.WriteInt32(-3); w.WriteByteString(p.Q.Y!);
        }
        else
        {
            var p = _rsa!.ExportParameters(false);
            w.WriteStartMap(4);
            w.WriteInt32(1); w.WriteInt32(3);
            w.WriteInt32(3); w.WriteInt32(WebAuthn.Rs256);
            w.WriteInt32(-1); w.WriteByteString(p.Modulus!);
            w.WriteInt32(-2); w.WriteByteString(p.Exponent!);
        }

        w.WriteEndMap();
        return w.Encode();
    }

    public static byte[] ClientData(string type, string challenge, string origin) =>
        JsonSerializer.SerializeToUtf8Bytes(new { type, challenge, origin, crossOrigin = false });

    private byte[] AuthData(string rpId, bool attested)
    {
        using var ms = new MemoryStream();
        ms.Write(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)));
        ms.WriteByte((byte)(Flags | (attested ? 0x40 : 0)));
        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(count, SignCount);
        ms.Write(count);
        if (attested)
        {
            ms.Write(new byte[16]); // AAGUID
            Span<byte> len = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(len, (ushort)CredentialId.Length);
            ms.Write(len);
            ms.Write(CredentialId);
            ms.Write(CoseKey());
        }

        return ms.ToArray();
    }

    public (byte[] ClientData, byte[] AttestationObject) Create(string challenge, string rpId = "localhost", string origin = "http://localhost")
    {
        var w = new CborWriter();
        w.WriteStartMap(3);
        w.WriteTextString("fmt"); w.WriteTextString("none");
        w.WriteTextString("attStmt"); w.WriteStartMap(0); w.WriteEndMap();
        w.WriteTextString("authData"); w.WriteByteString(AuthData(rpId, attested: true));
        w.WriteEndMap();
        return (ClientData("webauthn.create", challenge, origin), w.Encode());
    }

    public (byte[] ClientData, byte[] AuthenticatorData, byte[] Signature) Get(string challenge, string rpId = "localhost", string origin = "http://localhost")
    {
        var clientData = ClientData("webauthn.get", challenge, origin);
        var authData = AuthData(rpId, attested: false);
        var signed = authData.Concat(SHA256.HashData(clientData)).ToArray();
        var signature = _ec is not null
            ? _ec.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)
            : _rsa!.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return (clientData, authData, signature);
    }

    /// <summary>Registration body for <c>POST /api/v1/auth/passkeys</c>.</summary>
    public object RegistrationRequest(string name, string challenge)
    {
        var (clientData, attestation) = Create(challenge);
        var id = WebAuthn.ToBase64Url(CredentialId);
        return new
        {
            name,
            credential = new
            {
                id, rawId = id, type = "public-key",
                response = new { clientDataJSON = WebAuthn.ToBase64Url(clientData), attestationObject = WebAuthn.ToBase64Url(attestation), transports = new[] { "internal", "bogus" } },
            },
        };
    }

    /// <summary>Sign-in body for <c>POST /api/v1/auth/passkeys/login</c>.</summary>
    public object SignInRequest(string challenge, string rpId = "localhost")
    {
        var (clientData, authData, signature) = Get(challenge, rpId);
        var id = WebAuthn.ToBase64Url(CredentialId);
        return new
        {
            credential = new
            {
                id, rawId = id, type = "public-key",
                response = new
                {
                    clientDataJSON = WebAuthn.ToBase64Url(clientData),
                    authenticatorData = WebAuthn.ToBase64Url(authData),
                    signature = WebAuthn.ToBase64Url(signature),
                    userHandle = UserHandle is null ? null : WebAuthn.ToBase64Url(UserHandle),
                },
            },
        };
    }

    public void Dispose()
    {
        _ec?.Dispose();
        _rsa?.Dispose();
    }
}
