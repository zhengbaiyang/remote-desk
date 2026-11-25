using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;

namespace RemoteDesk;

internal sealed record GameStreamServerInfo(
    string HostName,
    string AppVersion,
    string GfeVersion,
    int PairStatus,
    int ServerCodecModeSupport,
    int CurrentGameId,
    int MajorVersion);

internal sealed record GameStreamAppInfo(
    int Id,
    string Title);

internal sealed record GameStreamLaunchResult(
    string SessionUrl,
    byte[] RemoteInputAesKey,
    byte[] RemoteInputAesIv);

internal sealed class GameStreamClient
{
    public const int HttpPort = 47989;
    public const int HttpsPort = 47984;

    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PairTimeout = TimeSpan.FromSeconds(25);

    private readonly GameStreamIdentity _identity;
    private readonly HttpMessageHandler? _customHandler;

    public GameStreamClient(GameStreamIdentity identity, HttpMessageHandler? customHandler = null)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _customHandler = customHandler;
    }

    public async Task<GameStreamServerInfo> GetServerInfoAsync(string host, CancellationToken ct = default)
    {
        var validatedHost = FormatHostForUri(host);
        try
        {
            var httpsXml = await SendHttpsGetAsync(
                $"https://{validatedHost}:{HttpsPort}/serverinfo?uniqueid={_identity.UniqueId}&uuid={Guid.NewGuid():N}",
                HttpTimeout,
                ct).ConfigureAwait(false);
            return ParseServerInfoXml(httpsXml);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            var httpXml = await SendHttpGetAsync(
                $"http://{validatedHost}:{HttpPort}/serverinfo?uniqueid={_identity.UniqueId}&uuid={Guid.NewGuid():N}",
                HttpTimeout,
                ct).ConfigureAwait(false);
            return ParseServerInfoXml(httpXml);
        }
    }

    public async Task<(bool Success, string Message)> PairWithPinAsync(
        string host,
        string pin,
        Func<CancellationToken, Task>? onPinReadyAsync = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pin) || pin.Trim().Length != 4 || !pin.Trim().All(char.IsAsciiDigit))
            return (false, "PIN 码必须为 4 位数字。");

        var normalizedPin = pin.Trim();
        var uriHost = FormatHostForUri(host);
        var serverInfo = await GetServerInfoAsync(host, ct).ConfigureAwait(false);
        if (serverInfo.PairStatus == 1)
            return (true, "目标主机已完成配对。");

        var useSha256 = serverInfo.MajorVersion >= 7;
        var hashLen = useSha256 ? 32 : 20;

        // 1. Salt + AES key derived from Hash(salt || ASCII(pin))
        var salt = RandomNumberGenerator.GetBytes(16);
        var saltPlusPin = new byte[salt.Length + 4];
        Buffer.BlockCopy(salt, 0, saltPlusPin, 0, salt.Length);
        Encoding.ASCII.GetBytes(normalizedPin, saltPlusPin.AsSpan(salt.Length, 4));
        var aesKey = ComputeHash(saltPlusPin, useSha256)[..16];

        var certUrl = $"http://{uriHost}:{HttpPort}/pair?uniqueid={_identity.UniqueId}&devicename=roth&updateState=1&phrase=getservercert&salt={ToLowerHex(salt)}&clientcert={_identity.ClientCertPemHex}";

        // Sunshine blocks /pair?phrase=getservercert until /api/pin is posted, and Sunshine 2026
        // keeps POST /api/pin open until the full pairing handshake completes. Run onPinReadyAsync
        // concurrently so Steps 2-6 execute without deadlocking on POST /api/pin.
        using var pairCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var pinCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var getCertTask = SendHttpGetAsync(certUrl, PairTimeout, pairCts.Token);
        Task? pinTask = null;

        string certXml;
        try
        {
            if (onPinReadyAsync is not null)
            {
                await Task.WhenAny(getCertTask, Task.Delay(300, ct)).ConfigureAwait(false);
                if (!getCertTask.IsCompleted)
                {
                    pinTask = Task.Run(() => onPinReadyAsync(pinCts.Token), ct);
                }
            }

            if (pinTask is not null)
            {
                var first = await Task.WhenAny(getCertTask, pinTask).ConfigureAwait(false);
                if (first == pinTask)
                {
                    if (pinTask.IsFaulted || pinTask.IsCanceled)
                    {
                        try { pairCts.Cancel(); } catch { }
                        await pinTask.ConfigureAwait(false);
                    }
                    certXml = await getCertTask.ConfigureAwait(false);
                }
                else
                {
                    if (getCertTask.IsFaulted || getCertTask.IsCanceled)
                    {
                        await Task.WhenAny(pinTask, Task.Delay(TimeSpan.FromSeconds(2.5), ct)).ConfigureAwait(false);
                        if (pinTask.IsFaulted || pinTask.IsCanceled)
                            await pinTask.ConfigureAwait(false);
                    }
                    certXml = await getCertTask.ConfigureAwait(false);
                }
            }
            else
            {
                certXml = await getCertTask.ConfigureAwait(false);
                if (onPinReadyAsync is not null)
                {
                    await onPinReadyAsync(ct).ConfigureAwait(false);
                }
            }

            var certDoc = ParseXml(certXml);
            var paired = GetElementInt(certDoc, "paired", 0);
            if (paired != 1)
            {
                if (pinTask is not null)
                {
                    await Task.WhenAny(pinTask, Task.Delay(TimeSpan.FromSeconds(1), ct)).ConfigureAwait(false);
                    if (pinTask.IsFaulted || pinTask.IsCanceled)
                        await pinTask.ConfigureAwait(false);
                }
                return (false, "Sunshine 拒绝了证书交换请求（可能配对已被取消）。");
            }

            var plainCertHex = GetElementText(certDoc, "plaincert");
            if (string.IsNullOrWhiteSpace(plainCertHex))
                return (false, "目标主机未返回服务端证书。");

            var serverCertPem = Encoding.ASCII.GetString(Convert.FromHexString(plainCertHex.Trim()));
            using var serverCert = X509Certificate2.CreateFromPem(serverCertPem);
            var serverCertSig = GameStreamIdentity.ExtractCertificateSignatureBytes(serverCert);

            // 2. Client challenge
            var clientChallenge = RandomNumberGenerator.GetBytes(16);
            var encClientChallenge = Aes128EcbEncrypt(aesKey, clientChallenge);
            var challengeUrl = $"http://{uriHost}:{HttpPort}/pair?uniqueid={_identity.UniqueId}&devicename=roth&updateState=1&clientchallenge={ToLowerHex(encClientChallenge)}";
            var challengeDoc = ParseXml(await SendHttpGetAsync(challengeUrl, PairTimeout, ct).ConfigureAwait(false));
            if (GetElementInt(challengeDoc, "paired", 0) != 1)
                return (false, "配对质询失败（PIN 码可能不匹配）。");

            var challengeRespHex = GetElementText(challengeDoc, "challengeresponse");
            if (string.IsNullOrWhiteSpace(challengeRespHex))
                return (false, "目标主机未返回 challengeresponse。");

            var decChallengeResp = Aes128EcbDecrypt(aesKey, Convert.FromHexString(challengeRespHex.Trim()));
            if (decChallengeResp.Length < hashLen + 16)
                return (false, "challengeresponse 长度无效。");

            var serverResponseHash = decChallengeResp.AsSpan(0, hashLen).ToArray();
            var serverChallenge = decChallengeResp.AsSpan(hashLen, 16).ToArray();

            // 3. Client secret + response hash
            var clientSecret = RandomNumberGenerator.GetBytes(16);
            var respInput = ConcatBytes(serverChallenge, _identity.ClientCertSignature, clientSecret);
            var challengeRespHash = ComputeHash(respInput, useSha256);
            var encRespHash = Aes128EcbEncrypt(aesKey, PadToBlock16(challengeRespHash));
            var serverRespUrl = $"http://{uriHost}:{HttpPort}/pair?uniqueid={_identity.UniqueId}&devicename=roth&updateState=1&serverchallengeresp={ToLowerHex(encRespHash)}";
            var pairingSecretDoc = ParseXml(await SendHttpGetAsync(serverRespUrl, PairTimeout, ct).ConfigureAwait(false));
            if (GetElementInt(pairingSecretDoc, "paired", 0) != 1)
                return (false, "服务端响应校验未通过。");

            var pairingSecretHex = GetElementText(pairingSecretDoc, "pairingsecret");
            if (string.IsNullOrWhiteSpace(pairingSecretHex))
                return (false, "目标主机未返回 pairingsecret。");

            var pairingSecretBytes = Convert.FromHexString(pairingSecretHex.Trim());
            if (pairingSecretBytes.Length <= 16)
                return (false, "pairingsecret 长度异常。");

            var serverSecret = pairingSecretBytes.AsSpan(0, 16).ToArray();
            var serverSig = pairingSecretBytes.AsSpan(16).ToArray();

            // 4. Verify server RSA signature over serverSecret & verify serverResponseHash
            using var serverRsa = serverCert.GetRSAPublicKey()
                ?? throw new InvalidDataException("目标主机证书不包含 RSA 公钥。");
            var hashAlgName = useSha256 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1;
            if (!serverRsa.VerifyData(serverSecret, serverSig, hashAlgName, RSASignaturePadding.Pkcs1))
                return (false, "目标主机 RSA 签名校验失败，请检查网络安全。");

            var expectedServerHash = ComputeHash(ConcatBytes(clientChallenge, serverCertSig, serverSecret), useSha256);
            if (!CryptographicOperations.FixedTimeEquals(expectedServerHash, serverResponseHash))
                return (false, "PIN 码校验失败（主机密钥不一致）。");

            // 5. Sign clientSecret and send clientpairingsecret
            var clientSig = _identity.PrivateKey.SignData(clientSecret, hashAlgName, RSASignaturePadding.Pkcs1);
            var clientPairingSecret = ConcatBytes(clientSecret, clientSig);
            var clientSecretUrl = $"http://{uriHost}:{HttpPort}/pair?uniqueid={_identity.UniqueId}&devicename=roth&updateState=1&clientpairingsecret={ToLowerHex(clientPairingSecret)}";
            var clientSecretDoc = ParseXml(await SendHttpGetAsync(clientSecretUrl, PairTimeout, ct).ConfigureAwait(false));
            if (GetElementInt(clientSecretDoc, "paired", 0) != 1)
                return (false, "客户端配对凭据提交失败。");

            // 6. Final mTLS HTTPS pairchallenge
            var challengeVerifyUrl = $"https://{uriHost}:{HttpsPort}/pair?uniqueid={_identity.UniqueId}&devicename=roth&updateState=1&phrase=pairchallenge";
            var finalDoc = ParseXml(await SendHttpsGetAsync(challengeVerifyUrl, PairTimeout, ct).ConfigureAwait(false));
            if (GetElementInt(finalDoc, "paired", 0) != 1)
                return (false, "HTTPS mTLS 最终配对校验未通过。");

            if (pinTask is not null)
            {
                await Task.WhenAny(pinTask, Task.Delay(TimeSpan.FromSeconds(3), ct)).ConfigureAwait(false);
            }

            return (true, "原生加密配对已完成。");
        }
        finally
        {
            try { pairCts.Cancel(); } catch { }
            try { pinCts.Cancel(); } catch { }
            ObserveTaskFault(getCertTask);
            if (pinTask is not null)
                ObserveTaskFault(pinTask);
        }
    }

    public async Task<IReadOnlyList<GameStreamAppInfo>> GetAppListAsync(string host, CancellationToken ct = default)
    {
        var uriHost = FormatHostForUri(host);
        var xml = await SendHttpsGetAsync(
            $"https://{uriHost}:{HttpsPort}/applist?uniqueid={_identity.UniqueId}&uuid={Guid.NewGuid():N}",
            HttpTimeout,
            ct).ConfigureAwait(false);
        return ParseAppListXml(xml);
    }

    public async Task<GameStreamLaunchResult> LaunchOrResumeAsync(
        string host,
        int appId,
        int width,
        int height,
        int fps,
        bool resumeIfRunning,
        CancellationToken ct = default)
    {
        var uriHost = FormatHostForUri(host);
        var rikey = RandomNumberGenerator.GetBytes(16);
        var rikeyIdInt = BitConverter.ToInt32(RandomNumberGenerator.GetBytes(4), 0);
        var rikeyIdBytes = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(rikeyIdInt));
        var riIv = new byte[16];
        Buffer.BlockCopy(rikeyIdBytes, 0, riIv, 0, 4);

        var verb = resumeIfRunning ? "resume" : "launch";
        var url =
            $"https://{uriHost}:{HttpsPort}/{verb}" +
            $"?uniqueid={_identity.UniqueId}" +
            $"&uuid={Guid.NewGuid():N}" +
            $"&appid={appId.ToString(CultureInfo.InvariantCulture)}" +
            $"&mode={width.ToString(CultureInfo.InvariantCulture)}x{height.ToString(CultureInfo.InvariantCulture)}x{fps.ToString(CultureInfo.InvariantCulture)}" +
            $"&additionalStates=1" +
            $"&sops=0" +
            $"&rikey={ToLowerHex(rikey)}" +
            $"&rikeyid={rikeyIdInt.ToString(CultureInfo.InvariantCulture)}" +
            $"&localAudioPlayMode=0" +
            $"&surroundAudioInfo=196610" +
            $"&remoteControllersBitmap=0" +
            $"&gcmap=0" +
            $"&gcpersist=0" +
            $"&corever=1";

        var xml = await SendHttpsGetAsync(url, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        var sessionUrl = ParseLaunchXml(xml, verb);
        return new GameStreamLaunchResult(sessionUrl, rikey, riIv);
    }

    public async Task CancelSessionAsync(string host, CancellationToken ct = default)
    {
        var uriHost = FormatHostForUri(host);
        try
        {
            _ = await SendHttpsGetAsync(
                $"https://{uriHost}:{HttpsPort}/cancel?uniqueid={_identity.UniqueId}&uuid={Guid.NewGuid():N}",
                TimeSpan.FromSeconds(4),
                ct).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort session teardown.
        }
    }

    internal static GameStreamServerInfo ParseServerInfoXml(string xml)
    {
        var doc = ParseXml(xml);
        var statusCodeAttr = doc.Root?.Attribute("status_code")?.Value;
        if (!string.IsNullOrWhiteSpace(statusCodeAttr) &&
            int.TryParse(statusCodeAttr.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var statusCode) &&
            statusCode != 200)
        {
            var statusMessage = doc.Root?.Attribute("status_message")?.Value;
            throw new InvalidDataException(
                string.IsNullOrWhiteSpace(statusMessage)
                    ? $"Sunshine /serverinfo 返回状态码 {statusCode}。"
                    : $"Sunshine /serverinfo 返回状态码 {statusCode}：{statusMessage}");
        }

        var appVersion = GetElementText(doc, "appversion") ?? "7.1.431.-1";
        var major = ParseMajorVersion(appVersion);
        return new GameStreamServerInfo(
            HostName: GetElementText(doc, "hostname") ?? "Sunshine-Host",
            AppVersion: appVersion,
            GfeVersion: GetElementText(doc, "GfeVersion") ?? "3.23.0.74",
            PairStatus: GetElementInt(doc, "PairStatus", 0),
            ServerCodecModeSupport: GetElementInt(doc, "ServerCodecModeSupport", 1),
            CurrentGameId: GetElementInt(doc, "currentgame", 0),
            MajorVersion: major);
    }

    internal static IReadOnlyList<GameStreamAppInfo> ParseAppListXml(string xml)
    {
        var doc = ParseXml(xml);
        var apps = new List<GameStreamAppInfo>();
        foreach (var el in doc.Descendants("App"))
        {
            var idText = el.Element("ID")?.Value;
            var title = el.Element("AppTitle")?.Value?.Trim();
            if (int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) &&
                !string.IsNullOrWhiteSpace(title))
            {
                apps.Add(new GameStreamAppInfo(id, title));
            }
        }
        return apps;
    }

    internal static string ParseLaunchXml(string xml, string verb)
    {
        var doc = ParseXml(xml);
        var tagName = string.Equals(verb, "resume", StringComparison.OrdinalIgnoreCase) ? "resume" : "gamesession";
        var status = GetElementInt(doc, tagName, 0);
        if (status == 0)
        {
            var statusMsg = doc.Root?.Attribute("status_message")?.Value ?? "主机未能启动串流会话。";
            throw new InvalidOperationException(statusMsg);
        }

        return GetElementText(doc, "sessionUrl0") ?? "";
    }

    private async Task<string> SendHttpGetAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        using var handler = _customHandler is null ? new HttpClientHandler { UseProxy = false } : null;
        using var client = new HttpClient(_customHandler ?? handler!, disposeHandler: false) { Timeout = timeout };
        using var resp = await client.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    internal static SocketsHttpHandler CreateDefaultHttpsHandler(X509Certificate2 clientCertificate) =>
        new()
        {
            UseProxy = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                AllowTlsResume = false,
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
                ClientCertificates = new X509CertificateCollection { clientCertificate },
            },
        };

    private async Task<string> SendHttpsGetAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        SocketsHttpHandler? handler = _customHandler is null
            ? CreateDefaultHttpsHandler(_identity.ClientCertificate)
            : null;

        try
        {
            using var client = new HttpClient(_customHandler ?? handler!, disposeHandler: false) { Timeout = timeout };
            using var resp = await client.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            handler?.Dispose();
        }
    }

    private static XDocument ParseXml(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            throw new InvalidDataException("Sunshine 返回了空响应。");

        try
        {
            return XDocument.Parse(xml);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException)
        {
            throw new InvalidDataException("无法解析 Sunshine XML 响应。", ex);
        }
    }

    private static string? GetElementText(XDocument doc, string name) =>
        doc.Root?.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static int GetElementInt(XDocument doc, string name, int fallback)
    {
        var text = GetElementText(doc, name);
        return int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private static int ParseMajorVersion(string appVersion)
    {
        var dot = appVersion.IndexOf('.');
        var head = dot > 0 ? appVersion[..dot] : appVersion;
        return int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) ? major : 7;
    }

    private static string FormatHostForUri(string host)
    {
        var validated = DeviceProfile.ValidateHostAddress(host, nameof(host));
        return validated.Contains(':') ? $"[{validated}]" : validated;
    }

    internal static byte[] ComputeHash(byte[] data, bool useSha256) =>
        useSha256 ? SHA256.HashData(data) : SHA1.HashData(data);

    internal static byte[] Aes128EcbEncrypt(byte[] key16, byte[] input)
    {
        using var aes = Aes.Create();
        aes.Key = key16;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        return aes.EncryptEcb(input, PaddingMode.None);
    }

    internal static byte[] Aes128EcbDecrypt(byte[] key16, byte[] input)
    {
        using var aes = Aes.Create();
        aes.Key = key16;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        return aes.DecryptEcb(input, PaddingMode.None);
    }

    private static byte[] PadToBlock16(byte[] data)
    {
        var rem = data.Length % 16;
        if (rem == 0)
            return data;
        var padded = new byte[data.Length + (16 - rem)];
        Buffer.BlockCopy(data, 0, padded, 0, data.Length);
        return padded;
    }

    private static byte[] ConcatBytes(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }
        return result;
    }

    private static string ToLowerHex(byte[] bytes) =>
        Convert.ToHexString(bytes).ToLowerInvariant();

    private static void ObserveTaskFault(Task task) =>
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
