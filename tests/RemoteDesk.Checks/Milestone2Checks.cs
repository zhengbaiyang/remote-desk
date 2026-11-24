using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace RemoteDesk;

internal static class Milestone2Checks
{
    public static IReadOnlyList<(string Name, Action Run)> CreateTests(string tempRoot) =>
    [
        ("GameStreamIdentity creates, persists, and extracts RSA-2048 cert signature", () =>
        {
            var dir = Path.Combine(tempRoot, "gs-id-" + Guid.NewGuid().ToString("N"));
            try
            {
                string thumbprint1;
                string uniqueId1;
                byte[] sig1;
                using (var id1 = GameStreamIdentity.LoadOrCreate(dir))
                {
                    Equal(16, id1.UniqueId.Length);
                    Equal(true, id1.UniqueId.All(c => char.IsAsciiHexDigitLower(c)));
                    Equal(256, id1.ClientCertSignature.Length);
                    Equal(true, id1.ClientCertPem.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal));
                    var decodedPem = Encoding.ASCII.GetString(Convert.FromHexString(id1.ClientCertPemHex));
                    Equal(id1.ClientCertPem, decodedPem);

                    thumbprint1 = id1.ClientCertificate.Thumbprint;
                    uniqueId1 = id1.UniqueId;
                    sig1 = id1.ClientCertSignature;
                }

                using var id2 = GameStreamIdentity.LoadOrCreate(dir);
                Equal(uniqueId1, id2.UniqueId);
                Equal(thumbprint1, id2.ClientCertificate.Thumbprint);
                Equal(Convert.ToHexString(sig1), Convert.ToHexString(id2.ClientCertSignature));
            }
            finally
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
        }),

        ("GameStreamClient parses serverinfo, applist, and launch/resume XML responses", () =>
        {
            var serverXml = """
                <root status_code="200">
                  <hostname>GAMING-PC</hostname>
                  <appversion>7.1.431.-1</appversion>
                  <GfeVersion>3.23.0.74</GfeVersion>
                  <PairStatus>1</PairStatus>
                  <ServerCodecModeSupport>65793</ServerCodecModeSupport>
                  <currentgame>1002</currentgame>
                </root>
                """;
            var info = GameStreamClient.ParseServerInfoXml(serverXml);
            Equal("GAMING-PC", info.HostName);
            Equal("7.1.431.-1", info.AppVersion);
            Equal(7, info.MajorVersion);
            Equal(1, info.PairStatus);
            Equal(65793, info.ServerCodecModeSupport);
            Equal(1002, info.CurrentGameId);

            var appsXml = """
                <root status_code="200">
                  <App><ID>1001</ID><AppTitle>Desktop</AppTitle></App>
                  <App><ID>1002</ID><AppTitle>Steam Big Picture</AppTitle></App>
                </root>
                """;
            var apps = GameStreamClient.ParseAppListXml(appsXml);
            Equal(2, apps.Count);
            Equal(1001, apps[0].Id);
            Equal("Desktop", apps[0].Title);
            Equal("Steam Big Picture", apps[1].Title);

            Equal("rtsp://10.126.126.2:48010", GameStreamClient.ParseLaunchXml("""<root status_code="200"><gamesession>1</gamesession><sessionUrl0>rtsp://10.126.126.2:48010</sessionUrl0></root>""", "launch"));
            Equal("rtsp://10.126.126.2:48010", GameStreamClient.ParseLaunchXml("""<root status_code="200"><resume>1</resume><sessionUrl0>rtsp://10.126.126.2:48010</sessionUrl0></root>""", "resume"));

            var threw = false;
            try
            {
                GameStreamClient.ParseLaunchXml("""<root status_code="200" status_message="Busy"><gamesession>0</gamesession></root>""", "launch");
            }
            catch (InvalidOperationException ex)
            {
                threw = ex.Message.Contains("Busy", StringComparison.Ordinal);
            }
            Equal(true, threw);
        }),

        ("GameStreamClient completes 6-step SHA-256 AES-128-ECB pairing and rejects wrong PIN", () =>
        {
            var dir = Path.Combine(tempRoot, "gs-pair-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var clientIdentity = GameStreamIdentity.LoadOrCreate(dir);
                var mockHost = new SimulatedSunshineHandler("4829");
                var client = new GameStreamClient(clientIdentity, mockHost);

                var pinSubmitted = false;
                var (ok, msg) = client.PairWithPinAsync(
                    "10.126.126.9",
                    "4829",
                    onPinReadyAsync: _ =>
                    {
                        pinSubmitted = true;
                        mockHost.UnlockPinWait();
                        return Task.CompletedTask;
                    }).GetAwaiter().GetResult();

                Equal(true, ok);
                Equal(true, pinSubmitted);
                Equal(true, mockHost.PairCompleted);

                // Wrong PIN must be rejected during cryptographic challenge verification.
                var wrongHost = new SimulatedSunshineHandler("9999", autoUnlockPin: true);
                var wrongClient = new GameStreamClient(clientIdentity, wrongHost);
                var (wrongOk, _) = wrongClient.PairWithPinAsync("10.126.126.9", "1234").GetAwaiter().GetResult();
                Equal(false, wrongOk);

                // Launch builds network-order AES IV matching rikeyid.
                var launched = client.LaunchOrResumeAsync("10.126.126.9", 1001, 2560, 1440, 120, false).GetAwaiter().GetResult();
                Equal("rtsp://10.126.126.9:48010", launched.SessionUrl);
                Equal(16, launched.RemoteInputAesKey.Length);
                Equal(16, launched.RemoteInputAesIv.Length);
            }
            finally
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
        }),

        ("StreamViewportMapper computes letterbox rects, mouse coordinates, and codec fallback", () =>
        {
            // 1920x1080 host displaying 4:3 (1600x1200) -> centered pillarbox 1440x1080 at x=240
            var rectPillar = StreamViewportMapper.ComputeLetterboxRect(1920, 1080, 1600, 1200);
            Equal(new ViewportRect(240, 0, 1440, 1080), rectPillar);

            // Clamped inside left black bar -> x=0
            var (leftBarX, leftBarY) = StreamViewportMapper.MapClientToStream(50, 540, 1920, 1080, 1600, 1200);
            Equal((short)0, leftBarX);
            Equal((short)600, leftBarY);

            // Center of video -> (800, 600)
            var (centerX, centerY) = StreamViewportMapper.MapClientToStream(960, 540, 1920, 1080, 1600, 1200);
            Equal((short)800, centerX);
            Equal((short)600, centerY);

            // 1200x1200 host displaying 16:9 (1920x1080) -> letterbox 1200x675
            var rectLetter = StreamViewportMapper.ComputeLetterboxRect(1200, 1200, 1920, 1080);
            Equal(1200, rectLetter.DrawWidth);
            Equal(675, rectLetter.DrawHeight);

            // Codec negotiation & H.264 fallback
            var (av1ToHevcId, av1ToHevcPref, fellBack1) = StreamViewportMapper.ResolveEffectiveCodec(
                VideoCodecPreference.Av1,
                StreamViewportMapper.CodecMaskH264 | StreamViewportMapper.CodecMaskHevc);
            Equal(StreamViewportMapper.NativePrefCodecHevc, av1ToHevcId);
            Equal(VideoCodecPreference.H265, av1ToHevcPref);
            Equal(false, fellBack1);

            var (hevcFallbackId, hevcFallbackPref, fellBack2) = StreamViewportMapper.ResolveEffectiveCodec(
                VideoCodecPreference.H265,
                StreamViewportMapper.CodecMaskH264);
            Equal(StreamViewportMapper.NativePrefCodecH264, hevcFallbackId);
            Equal(VideoCodecPreference.H264, hevcFallbackPref);
            Equal(true, fellBack2);
        }),
    ];

    private sealed class SimulatedSunshineHandler : HttpMessageHandler
    {
        private readonly RSA _serverRsa;
        private readonly X509Certificate2 _serverCert;
        private readonly string _serverCertPemHex;
        private readonly byte[] _serverCertSig;
        private readonly TaskCompletionSource _pinGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _expectedPin;

        private byte[] _aesKey = [];
        private byte[] _clientChallenge = [];
        private readonly byte[] _serverChallenge = RandomNumberGenerator.GetBytes(16);
        private readonly byte[] _serverSecret = RandomNumberGenerator.GetBytes(16);
        private byte[] _clientCertPem = [];

        public bool PairCompleted { get; private set; }

        public SimulatedSunshineHandler(string expectedPin, bool autoUnlockPin = false)
        {
            _expectedPin = expectedPin;
            _serverRsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=Sunshine-Mock", _serverRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            _serverCert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            var pem = _serverCert.ExportCertificatePem().Replace("\r\n", "\n", StringComparison.Ordinal);
            _serverCertPemHex = Convert.ToHexString(Encoding.ASCII.GetBytes(pem)).ToLowerInvariant();
            _serverCertSig = GameStreamIdentity.ExtractCertificateSignatureBytes(_serverCert);

            if (autoUnlockPin)
                _pinGate.TrySetResult();
        }

        public void UnlockPinWait() => _pinGate.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var query = ParseQuery(uri.Query);

            if (uri.AbsolutePath == "/serverinfo")
            {
                var pairedVal = PairCompleted ? 1 : 0;
                return XmlOk($"<root status_code=\"200\"><hostname>SIM-HOST</hostname><appversion>7.1.431.-1</appversion><GfeVersion>3.23.0.74</GfeVersion><PairStatus>{pairedVal}</PairStatus><ServerCodecModeSupport>65793</ServerCodecModeSupport><currentgame>0</currentgame></root>");
            }

            if (uri.AbsolutePath == "/launch")
            {
                return XmlOk($"<root status_code=\"200\"><gamesession>1</gamesession><sessionUrl0>rtsp://{uri.Host}:48010</sessionUrl0></root>");
            }

            if (uri.AbsolutePath == "/pair")
            {
                if (query.TryGetValue("phrase", out var phrase) && phrase == "getservercert")
                {
                    await _pinGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    var salt = Convert.FromHexString(query["salt"]);
                    var pinBytes = Encoding.ASCII.GetBytes(_expectedPin);
                    var combined = new byte[salt.Length + pinBytes.Length];
                    Buffer.BlockCopy(salt, 0, combined, 0, salt.Length);
                    Buffer.BlockCopy(pinBytes, 0, combined, salt.Length, pinBytes.Length);
                    _aesKey = SHA256.HashData(combined)[..16];
                    _clientCertPem = Convert.FromHexString(query["clientcert"]);
                    return XmlOk($"<root status_code=\"200\"><paired>1</paired><plaincert>{_serverCertPemHex}</plaincert></root>");
                }

                if (query.TryGetValue("clientchallenge", out var encChallengeHex))
                {
                    _clientChallenge = GameStreamClient.Aes128EcbDecrypt(_aesKey, Convert.FromHexString(encChallengeHex));
                    var hashInput = Combine(_clientChallenge, _serverCertSig, _serverSecret);
                    var serverHash = SHA256.HashData(hashInput);
                    var plain = Combine(serverHash, _serverChallenge);
                    var encResp = GameStreamClient.Aes128EcbEncrypt(_aesKey, plain);
                    return XmlOk($"<root status_code=\"200\"><paired>1</paired><challengeresponse>{Convert.ToHexString(encResp).ToLowerInvariant()}</challengeresponse></root>");
                }

                if (query.ContainsKey("serverchallengeresp"))
                {
                    var serverSig = _serverRsa.SignData(_serverSecret, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    var pairingSecret = Combine(_serverSecret, serverSig);
                    return XmlOk($"<root status_code=\"200\"><paired>1</paired><pairingsecret>{Convert.ToHexString(pairingSecret).ToLowerInvariant()}</pairingsecret></root>");
                }

                if (query.TryGetValue("clientpairingsecret", out var clientSecretHex))
                {
                    var bytes = Convert.FromHexString(clientSecretHex);
                    var clientSecret = bytes[..16];
                    var clientSig = bytes[16..];
                    using var clientCert = X509Certificate2.CreateFromPem(Encoding.ASCII.GetString(_clientCertPem));
                    using var clientRsa = clientCert.GetRSAPublicKey()!;
                    var sigOk = clientRsa.VerifyData(clientSecret, clientSig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    return XmlOk($"<root status_code=\"200\"><paired>{(sigOk ? 1 : 0)}</paired></root>");
                }

                if (query.TryGetValue("phrase", out var finalPhrase) && finalPhrase == "pairchallenge")
                {
                    PairCompleted = true;
                    return XmlOk("<root status_code=\"200\"><paired>1</paired></root>");
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _serverCert.Dispose();
                _serverRsa.Dispose();
            }
            base.Dispose(disposing);
        }

        private static HttpResponseMessage XmlOk(string xml) =>
            new(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq > 0)
                    result[Uri.UnescapeDataString(part[..eq])] = Uri.UnescapeDataString(part[(eq + 1)..]);
            }
            return result;
        }

        private static byte[] Combine(params byte[][] parts)
        {
            var total = parts.Sum(p => p.Length);
            var buf = new byte[total];
            var off = 0;
            foreach (var p in parts)
            {
                Buffer.BlockCopy(p, 0, buf, off, p.Length);
                off += p.Length;
            }
            return buf;
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}
