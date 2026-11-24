using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace RemoteDesk;

internal static class Milestone1Checks
{
    public static IReadOnlyList<(string Name, Action Run)> CreateTests(string tempRoot) =>
    [
        ("HostServiceManager filters 198.18.0.0/15 Fake-IP and classifies virtual/proxy adapters", () =>
        {
            var (ipv4, ipv6) = HostServiceManager.FilterIpAddresses(
            [
                IPAddress.Parse("198.18.0.1"),
                IPAddress.Parse("198.19.255.254"),
                IPAddress.Parse("169.254.10.20"),
                IPAddress.Parse("127.0.0.1"),
                IPAddress.Parse("192.168.31.100"),
                IPAddress.Parse("10.126.126.2"),
                IPAddress.Parse("2001:da8:201::88"),
            ]);
            Equal("192.168.31.100|10.126.126.2", string.Join('|', ipv4));
            Equal("2001:da8:201::88", string.Join('|', ipv6));

            Equal(true, HostServiceManager.IsVirtualOrProxyAdapter("Clash", "Clash Tunnel"));
            Equal(true, HostServiceManager.IsVirtualOrProxyAdapter("Mihomo", "Meta TUN"));
            Equal(true, HostServiceManager.IsVirtualOrProxyAdapter("easytier_tun", "Wintun Userspace Tunnel"));
            Equal(true, HostServiceManager.IsVirtualOrProxyAdapter("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter"));
            Equal(true, HostServiceManager.IsVirtualOrProxyAdapter("VMware Network Adapter VMnet8", "VMware Virtual Ethernet"));
            Equal(false, HostServiceManager.IsVirtualOrProxyAdapter("WLAN", "Intel(R) Wi-Fi 6E AX211 160MHz"));
            Equal(false, HostServiceManager.IsVirtualOrProxyAdapter("以太网", "Realtek Gaming 2.5GbE Family Controller"));
        }),

        ("EasyTierManager classifies relay-only nodes and builds elevated StartInfo safely", () =>
        {
            var relayPeer = new EasyTierPeerInfo("aliyun-relay", "", EasyTierManager.ConnectionTypeP2P, 32.8, "tcp", false);
            var pendingClient = new EasyTierPeerInfo("DESKTOP-10LPLFE", "", EasyTierManager.ConnectionTypeP2P, 21.4, "tcp", false);
            var selfNoIp = new EasyTierPeerInfo("LAPTOP-SELF", "", EasyTierManager.ConnectionTypeSelf, null, "", true);
            var allocatedRelay = new EasyTierPeerInfo("relay-1", "10.126.126.1", EasyTierManager.ConnectionTypeRelay, 15.0, "tcp", false);

            Equal(true, EasyTierManager.IsRelayOnlyPeer(relayPeer));
            Equal(false, EasyTierManager.IsRelayOnlyPeer(pendingClient));
            Equal(false, EasyTierManager.IsRelayOnlyPeer(selfNoIp));
            Equal(false, EasyTierManager.IsRelayOnlyPeer(allocatedRelay));

            var root = Path.Combine(tempRoot, "et-elev-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var coreExe = Path.Combine(root, "easytier-core.exe");
            File.WriteAllBytes(coreExe, []);

            var cfg = new EasyTierConfig
            {
                Enabled = true,
                NetworkName = "rd-test-net",
                NetworkSecret = "secret1234",
                AutoAssignIpv4 = false,
                VirtualIpv4 = "10.126.126.2/24",
                PeerUrl = "tcp://203.0.113.10:11010",
            };
            var elevated = EasyTierManager.CreateElevatedCoreStartInfo(coreExe, cfg);
            Equal(true, elevated.UseShellExecute);
            Equal("runas", elevated.Verb);
            Equal(System.Diagnostics.ProcessWindowStyle.Hidden, elevated.WindowStyle);
            Equal(true, elevated.Arguments.Contains("--network-name rd-test-net", StringComparison.Ordinal));
            Equal(true, elevated.Arguments.Contains("-i 10.126.126.2/24", StringComparison.Ordinal));
            Equal(true, elevated.Arguments.Contains("-p tcp://203.0.113.10:11010", StringComparison.Ordinal));
        }),

        ("SecretProtector round-trips secrets and rejects corrupted DPAPI blobs", () =>
        {
            const string secret = "tok_super_secret_value_2026!";
            var encrypted = SecretProtector.ProtectCurrentUser(secret);
            Equal(true, encrypted.Length > secret.Length);
            Equal(true, !encrypted.Contains(secret, StringComparison.Ordinal));
            Equal(secret, SecretProtector.Unprotect(encrypted));

            Equal(false, SecretProtector.TryUnprotect("dpapi-u:!!!not-base64!!!", out _));
            Equal(false, SecretProtector.TryUnprotect("", out _));
        }),

        ("SecurityPinHasher hashes, verifies, and rejects weak or control PINs", () =>
        {
            var (salt, hash) = SecurityPinHasher.HashPin("258099");
            Equal(32, salt.Length);
            Equal(64, hash.Length);
            Equal(true, SecurityPinHasher.VerifyPin("258099", salt, hash));
            Equal(false, SecurityPinHasher.VerifyPin("258098", salt, hash));
            Equal(false, SecurityPinHasher.VerifyPin("", salt, hash));

            Throws<ArgumentException>(() => SecurityPinHasher.HashPin("123"));
            Throws<ArgumentException>(() => SecurityPinHasher.HashPin("12\n34"));
        }),

        ("AgentHmacAuth verifies signatures, rejects tampering, clock skew, and replay", () =>
        {
            var auth = new AgentHmacAuth();
            const string key = "0123456789abcdef0123456789abcdef";
            var now = 1_750_000_000L;
            var body = Encoding.UTF8.GetBytes("""{"pin":"4829","clientName":"Laptop"}""");
            var sig = AgentHmacAuth.ComputeSignature(key, "POST", "/agent/v1/pair", now, "nonce-abc-123456", body);

            Equal(true, auth.VerifySignature(key, "POST", "/agent/v1/pair", now, "nonce-abc-123456", body, sig, now));
            // Replay with same nonce must fail.
            Equal(false, auth.VerifySignature(key, "POST", "/agent/v1/pair", now, "nonce-abc-123456", body, sig, now));
            // Tampered body must fail.
            var tamperedBody = Encoding.UTF8.GetBytes("""{"pin":"0000","clientName":"Laptop"}""");
            Equal(false, auth.VerifySignature(key, "POST", "/agent/v1/pair", now, "nonce-tamper-123", tamperedBody, sig, now));
            // Stale timestamp (>60s) must fail.
            Equal(false, auth.VerifySignature(key, "POST", "/agent/v1/pair", now - 65, "nonce-stale-1234", body, sig, now));
        }),

        ("SunshineManager hardens sunshine.conf cleanly and builds safe --creds arguments", () =>
        {
            var dir = Path.Combine(tempRoot, "sunshine-" + Guid.NewGuid().ToString("N"));
            var configDir = Path.Combine(dir, "config");
            Directory.CreateDirectory(configDir);
            var exe = Path.Combine(dir, "sunshine.exe");
            File.WriteAllBytes(exe, []);

            var resolved = SunshineManager.ResolveConfigPath(exe);
            Equal(Path.Combine(configDir, "sunshine.conf"), resolved);

            File.WriteAllText(resolved, "fps = [60, 120]\norigin_web_ui_allowed = wan\n");
            SunshineManager.EnsureSafeLocalConfig(resolved);
            var updated = File.ReadAllText(resolved);
            Equal(true, updated.Contains("fps = [60, 120]", StringComparison.Ordinal));
            Equal(true, updated.Contains("origin_web_ui_allowed = pc", StringComparison.Ordinal));
            Equal(true, updated.Contains("upnp = off", StringComparison.Ordinal));
            Equal(false, updated.Contains("origin_web_ui_allowed = wan", StringComparison.Ordinal));

            var creds = SunshineManager.CreateSetCredentialsStartInfo(exe, "remotedesk", "Pass1234ABCD");
            Equal("--creds|remotedesk|Pass1234ABCD", string.Join('|', creds.ArgumentList));
            Equal(true, creds.CreateNoWindow);
            Throws<ArgumentException>(() => SunshineManager.CreateSetCredentialsStartInfo(exe, "bad user", "Pass1234"));
        }),

        ("AgentPeerServer + AgentPeerClient enforce HMAC auth, zero-PIN pair callback, and optional security PIN", () =>
        {
            const string authKey = "fedcba9876543210fedcba9876543210";
            var (pinSalt, pinHash) = SecurityPinHasher.HashPin("889900");
            var requirePin = false;
            string? capturedPin = null;
            string? capturedClient = null;

            using var server = new AgentPeerServer(
                () => new AgentPeerServerState(
                    DeviceKey: "dev-host-01",
                    Hostname: "DORM-PC",
                    SunshineReady: true,
                    SunshinePort: 47989,
                    AgentAuthKey: authKey,
                    SecurityPinRequired: requirePin,
                    SecurityPinSalt: pinSalt,
                    SecurityPinHash: pinHash),
                (pin, clientName, _) =>
                {
                    capturedPin = pin;
                    capturedClient = clientName;
                    return Task.FromResult((true, "paired"));
                });

            var bridgeHandler = new PeerServerBridgeHandler(server);
            using var rawHttp = new HttpClient(bridgeHandler);
            using var peerClient = new AgentPeerClient(bridgeHandler);

            // 1. Unsigned request must return 401.
            var unsignedResp = rawHttp.GetAsync("http://10.126.126.2:47999/agent/v1/info").GetAwaiter().GetResult();
            Equal(HttpStatusCode.Unauthorized, unsignedResp.StatusCode);

            // 2. Signed GET /agent/v1/info succeeds.
            var (infoOk, info, _) = peerClient.GetInfoAsync("10.126.126.2", 47999, authKey).GetAwaiter().GetResult();
            Equal(true, infoOk);
            Equal("dev-host-01", info?.DeviceKey);
            Equal("DORM-PC", info?.Hostname);
            Equal(true, info?.SunshineReady);
            Equal(false, info?.SecurityPinRequired);

            // 3. Signed POST /agent/v1/pair without security PIN succeeds when disabled.
            var pairOk = peerClient.SubmitPairPinAsync("10.126.126.2", 47999, authKey, "4829", "Laptop-A").GetAwaiter().GetResult();
            Equal(true, pairOk.Ok);
            Equal("4829", capturedPin);
            Equal("Laptop-A", capturedClient);

            // 4. Enable security PIN: missing PIN -> SECURITY_PIN_REQUIRED, wrong -> INVALID_SECURITY_PIN, valid -> Ok.
            requirePin = true;
            var missingResp = peerClient.SubmitPairPinAsync("10.126.126.2", 47999, authKey, "1122", "Laptop-B").GetAwaiter().GetResult();
            Equal(false, missingResp.Ok);
            Equal("SECURITY_PIN_REQUIRED", missingResp.Error);

            var wrongResp = peerClient.SubmitPairPinAsync("10.126.126.2", 47999, authKey, "1122", "Laptop-B", "000000").GetAwaiter().GetResult();
            Equal(false, wrongResp.Ok);
            Equal("INVALID_SECURITY_PIN", wrongResp.Error);

            var validResp = peerClient.SubmitPairPinAsync("10.126.126.2", 47999, authKey, "1122", "Laptop-B", "889900").GetAwaiter().GetResult();
            Equal(true, validResp.Ok);
            Equal("1122", capturedPin);
        }),

        ("SyncApiClient normalizes URLs, parses auth/checkin payloads, and SettingsStore round-trips AccountSettings", () =>
        {
            Equal("https://203.0.113.10:11020", SyncApiClient.NormalizeServerUrl("203.0.113.10:11020/"));
            Equal("http://127.0.0.1:11020", SyncApiClient.NormalizeServerUrl(" http://127.0.0.1:11020/ "));
            Throws<ArgumentException>(() => SyncApiClient.NormalizeServerUrl("ftp://203.0.113.10"));
            Throws<ArgumentException>(() => SyncApiClient.NormalizeServerUrl("   "));

            var mockHandler = new MockJsonHttpHandler(req =>
            {
                if (req.RequestUri!.AbsolutePath == "/api/v1/auth/login")
                {
                    return (HttpStatusCode.OK, """
                    {
                      "token": "tok_abc",
                      "user": { "id": "u1", "username": "alice", "createdAtUtc": 1750000000 },
                      "meshConfig": {
                        "peerUrl": "tcp://203.0.113.10:11010",
                        "fallbackWsUrl": "ws://203.0.113.10:11011",
                        "networkName": "rd-112233",
                        "networkSecret": "sec_xyz",
                        "agentAuthKey": "auth_key_64",
                        "subnetCidr": "10.126.126.0/24"
                      }
                    }
                    """);
                }
                if (req.RequestUri.AbsolutePath == "/api/v1/devices/checkin")
                {
                    return (HttpStatusCode.OK, """
                    {
                      "device": {
                        "id": "d1", "deviceKey": "dev-1", "name": "Dorm", "hostname": "DORM",
                        "virtualIpv4": "10.126.126.2", "lanIpv4": "192.168.1.10", "lanIpv6": null,
                        "sunshineReady": true, "sunshinePort": 47989, "agentPort": 47999,
                        "securityPinRequired": false, "gpuEncoder": "NVENC", "osVersion": "Win11",
                        "lastSeenAtUtc": 1750000010, "isOnline": true
                      },
                      "devices": [],
                      "serverTimeUtc": 1750000010
                    }
                    """);
                }
                return (HttpStatusCode.NotFound, """{"error":"NOT_FOUND","message":"not found"}""");
            });

            var api = new SyncApiClient(mockHandler);
            var auth = api.LoginAsync("https://203.0.113.10:11020", "alice", "pass1234", null).GetAwaiter().GetResult();
            Equal("tok_abc", auth.Token);
            Equal("rd-112233", auth.MeshConfig.NetworkName);

            var checkin = api.CheckinDeviceAsync(
                "https://203.0.113.10:11020",
                auth.Token,
                new SyncCheckinRequest("dev-1", "Dorm", "DORM", "192.168.1.10", null, true, 47989, 47999, false, "NVENC", "Win11"),
                null).GetAwaiter().GetResult();
            Equal("10.126.126.2", checkin.Device.VirtualIpv4);

            var settingsPath = Path.Combine(tempRoot, "sync-settings-" + Guid.NewGuid().ToString("N"), "settings.json");
            var store = new SettingsStore(settingsPath);
            var saved = new AppSettings
            {
                LocalDeviceKey = "dev-1",
                PairedDeviceIds = ["d1", "d2"],
                SecurityPinRequired = true,
                Account = new AccountSettings
                {
                    ServerUrl = "https://203.0.113.10:11020",
                    ServerCertThumbprint = "AABBCC",
                    UserId = "u1",
                    Username = "alice",
                    ProtectedSessionToken = SecretProtector.ProtectCurrentUser("tok_abc"),
                    MeshConfig = auth.MeshConfig,
                    AssignedVirtualIpv4 = "10.126.126.2",
                },
            };
            store.Save(saved);
            var reloaded = store.Load();
            Equal("dev-1", reloaded.LocalDeviceKey);
            Equal("alice", reloaded.Account?.Username);
            Equal("10.126.126.2", reloaded.Account?.AssignedVirtualIpv4);
            Equal(2, reloaded.PairedDeviceIds?.Count);
            Equal(true, reloaded.SecurityPinRequired);
        }),
    ];

    private sealed class PeerServerBridgeHandler(AgentPeerServer server) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bodyBytes = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            var sb = new StringBuilder();
            sb.Append(request.Method.Method).Append(' ').Append(request.RequestUri!.PathAndQuery).Append(" HTTP/1.1\r\n");
            sb.Append("Host: ").Append(request.RequestUri.Authority).Append("\r\n");
            foreach (var header in request.Headers)
                sb.Append(header.Key).Append(": ").Append(string.Join(", ", header.Value)).Append("\r\n");
            if (request.Content is not null)
            {
                foreach (var header in request.Content.Headers)
                    sb.Append(header.Key).Append(": ").Append(string.Join(", ", header.Value)).Append("\r\n");
                sb.Append("Content-Length: ").Append(bodyBytes.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            }
            sb.Append("\r\n");

            var reqHeaderBytes = Encoding.ASCII.GetBytes(sb.ToString());
            using var reqStream = new MemoryStream(reqHeaderBytes.Length + bodyBytes.Length);
            reqStream.Write(reqHeaderBytes, 0, reqHeaderBytes.Length);
            reqStream.Write(bodyBytes, 0, bodyBytes.Length);
            reqStream.Position = 0;

            using var respStream = new MemoryStream();
            using var split = new SplitStream(reqStream, respStream);
            await server.HandleConnectionStreamAsync(split, cancellationToken).ConfigureAwait(false);

            var rawResp = respStream.ToArray();
            var sep = FindHeaderBodySeparator(rawResp);
            var headText = Encoding.ASCII.GetString(rawResp, 0, sep);
            var headLines = headText.Split("\r\n", StringSplitOptions.None);
            var statusCode = (HttpStatusCode)int.Parse(headLines[0].Split(' ')[1], CultureInfo.InvariantCulture);

            var bodyOffset = sep + 4;
            var respBody = new byte[rawResp.Length - bodyOffset];
            Buffer.BlockCopy(rawResp, bodyOffset, respBody, 0, respBody.Length);

            return new HttpResponseMessage(statusCode)
            {
                Content = new ByteArrayContent(respBody),
            };
        }

        private static int FindHeaderBodySeparator(byte[] data)
        {
            for (var i = 0; i <= data.Length - 4; i++)
            {
                if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                    return i;
            }
            throw new InvalidDataException("Missing HTTP header separator.");
        }
    }

    private sealed class SplitStream(Stream input, Stream output) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => output.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => output.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            input.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            input.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            output.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            output.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class MockJsonHttpHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Json)> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, json) = responder(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
