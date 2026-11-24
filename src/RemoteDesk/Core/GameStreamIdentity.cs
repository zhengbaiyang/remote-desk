using System.Formats.Asn1;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace RemoteDesk;

internal sealed class GameStreamIdentity : IDisposable
{
    public GameStreamIdentity(string uniqueId, X509Certificate2 clientCertificate, RSA privateKey, string clientCertPem)
    {
        UniqueId = uniqueId;
        ClientCertificate = clientCertificate;
        PrivateKey = privateKey;
        ClientCertPem = clientCertPem;
        ClientCertSignature = ExtractCertificateSignatureBytes(clientCertificate);
    }

    public string UniqueId { get; }
    public X509Certificate2 ClientCertificate { get; }
    public RSA PrivateKey { get; }
    public string ClientCertPem { get; }
    public byte[] ClientCertSignature { get; }

    public string ClientCertPemHex => Convert.ToHexString(Encoding.ASCII.GetBytes(ClientCertPem)).ToLowerInvariant();

    public void Dispose()
    {
        ClientCertificate.Dispose();
        PrivateKey.Dispose();
    }

    public static string GetDefaultDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RemoteDesk",
            "stream-identity");

    public static GameStreamIdentity LoadOrCreate(string? directory = null)
    {
        var dir = string.IsNullOrWhiteSpace(directory) ? GetDefaultDirectory() : Path.GetFullPath(directory);
        Directory.CreateDirectory(dir);

        var certPath = Path.Combine(dir, "client.crt");
        var keyPath = Path.Combine(dir, "client.key");
        var uidPath = Path.Combine(dir, "uniqueid.txt");

        if (File.Exists(certPath) && File.Exists(keyPath) && File.Exists(uidPath))
        {
            try
            {
                var uid = File.ReadAllText(uidPath, Encoding.ASCII).Trim();
                var certPem = File.ReadAllText(certPath, Encoding.ASCII);
                var keyPem = File.ReadAllText(keyPath, Encoding.ASCII);
                if (IsValidHexUid(uid) && !string.IsNullOrWhiteSpace(certPem) && !string.IsNullOrWhiteSpace(keyPem))
                {
                    return BuildIdentityFromPem(uid, certPem, keyPem);
                }
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or FormatException)
            {
                // Regenerate corrupted identity cleanly.
            }
        }

        var newUid = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=NVIDIA GameStream Client",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var now = DateTimeOffset.UtcNow;
        using var generatedCert = req.CreateSelfSigned(now.AddDays(-1), now.AddYears(20));

        var newCertPem = ExportCertPem(generatedCert);
        var newKeyPem = ExportPkcs8PrivateKeyPem(rsa);

        File.WriteAllText(uidPath, newUid, new UTF8Encoding(false));
        File.WriteAllText(certPath, newCertPem, new UTF8Encoding(false));
        File.WriteAllText(keyPath, newKeyPem, new UTF8Encoding(false));

        return BuildIdentityFromPem(newUid, newCertPem, newKeyPem);
    }

    public static byte[] ExtractCertificateSignatureBytes(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var reader = new AsnReader(certificate.RawData, AsnEncodingRules.DER);
        var outerSeq = reader.ReadSequence();
        _ = outerSeq.ReadEncodedValue(); // tbsCertificate
        _ = outerSeq.ReadEncodedValue(); // signatureAlgorithm
        return outerSeq.ReadBitString(out _);
    }

    private static GameStreamIdentity BuildIdentityFromPem(string uniqueId, string certPem, string keyPem)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(keyPem);

        using var ephemeral = X509Certificate2.CreateFromPem(certPem, keyPem);
        // Round-trip through PKCS#12 so Windows SChannel can perform TLS client authentication reliably.
        var pfxBytes = ephemeral.Export(X509ContentType.Pkcs12, string.Empty);
        var flags = OperatingSystem.IsWindows()
            ? X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet
            : X509KeyStorageFlags.Exportable;
        var cert = new X509Certificate2(pfxBytes, string.Empty, flags);

        return new GameStreamIdentity(uniqueId, cert, rsa, NormalizePemLineEndings(certPem));
    }

    private static string ExportCertPem(X509Certificate2 cert)
    {
        var base64 = Convert.ToBase64String(cert.RawData, Base64FormattingOptions.InsertLineBreaks);
        return $"-----BEGIN CERTIFICATE-----\n{base64.Replace("\r\n", "\n")}\n-----END CERTIFICATE-----\n";
    }

    private static string ExportPkcs8PrivateKeyPem(RSA rsa)
    {
        var pkcs8 = rsa.ExportPkcs8PrivateKey();
        var base64 = Convert.ToBase64String(pkcs8, Base64FormattingOptions.InsertLineBreaks);
        return $"-----BEGIN PRIVATE KEY-----\n{base64.Replace("\r\n", "\n")}\n-----END PRIVATE KEY-----\n";
    }

    private static string NormalizePemLineEndings(string pem) =>
        pem.Replace("\r\n", "\n").TrimEnd() + "\n";

    private static bool IsValidHexUid(string uid) =>
        uid.Length == 16 && uid.All(Uri.IsHexDigit);
}
