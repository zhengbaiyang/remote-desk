using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace RemoteDesk;

internal static class Milestone3Checks
{
    public static IReadOnlyList<(string Name, Action Run)> CreateTests(string tempRoot) =>
    [
        ("DisplayResolutionPlanner picks exact, refresh, aspect-matched, and safe fallback modes", () =>
        {
            var current = new DisplayModeInfo(1920, 1080, 60);
            IReadOnlyList<DisplayModeInfo> modes =
            [
                new(1280, 720, 60),
                new(1920, 1080, 60),
                new(1920, 1080, 120),
                new(1920, 1080, 144),
                new(2560, 1440, 60),
                new(2560, 1440, 120),
            ];

            // Exact resolution picking closest >= refresh
            Equal(new DisplayModeInfo(1920, 1080, 120), DisplayResolutionPlanner.SelectBestMode(1920, 1080, 90, current, modes));
            // Exact resolution picking max refresh when target exceeds all
            Equal(new DisplayModeInfo(1920, 1080, 144), DisplayResolutionPlanner.SelectBestMode(1920, 1080, 240, current, modes));
            // 4K 16:9 unavailable -> closest 16:9 mode <= 3840 (2560x1440 @ 60)
            Equal(new DisplayModeInfo(2560, 1440, 60), DisplayResolutionPlanner.SelectBestMode(3840, 2160, 60, current, modes));
            // Unmatchable aspect ratio falls back safely to current
            Equal(current, DisplayResolutionPlanner.SelectBestMode(1000, 3000, 60, current, modes));
        }),

        ("FileTransferPathHelper blocks traversal/reserved names and resolves unique collision paths", () =>
        {
            Throws<ArgumentException>(() => FileTransferPathHelper.SanitizeFileName("../secret.txt"));
            Throws<ArgumentException>(() => FileTransferPathHelper.SanitizeFileName(@"folder\..\evil.txt"));
            Throws<ArgumentException>(() => FileTransferPathHelper.SanitizeFileName("CON.txt"));
            Throws<ArgumentException>(() => FileTransferPathHelper.SanitizeFileName("nul"));
            Throws<ArgumentException>(() => FileTransferPathHelper.SanitizeFileName("  "));

            Equal("report.pdf", FileTransferPathHelper.SanitizeFileName(@"C:\Users\alice\Desktop\report.pdf"));
            Equal("a_b_c.txt", FileTransferPathHelper.SanitizeFileName("dir/a:b?c.txt"));

            var dir = Path.Combine(tempRoot, "ft-unique-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var p0 = FileTransferPathHelper.ResolveUniqueTargetPath(dir, "doc.pdf");
                Equal(Path.Combine(dir, "doc.pdf"), p0);
                File.WriteAllText(p0, "0");

                var p1 = FileTransferPathHelper.ResolveUniqueTargetPath(dir, "doc.pdf");
                Equal(Path.Combine(dir, "doc (1).pdf"), p1);
                File.WriteAllText(p1, "1");

                var p2 = FileTransferPathHelper.ResolveUniqueTargetPath(dir, "doc.pdf");
                Equal(Path.Combine(dir, "doc (2).pdf"), p2);
            }
            finally
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
        }),

        ("AgentHmacAuth validates body-hash signatures and rejects tampering/replay", () =>
        {
            var verifier = new AgentHmacAuth();
            var authKey = "test-shared-secret-key";
            var sha = Convert.ToHexString(SHA256.HashData("payload-bytes"u8.ToArray())).ToLowerInvariant();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var nonce = "nonce-12345678";

            var sig = AgentHmacAuth.ComputeSignatureFromBodyHash(authKey, "POST", "/agent/v1/files/upload", now, nonce, sha);
            Equal(true, verifier.VerifySignatureFromBodyHash(authKey, "POST", "/agent/v1/files/upload", now, nonce, sha, sig, now));
            // Replay same nonce must fail
            Equal(false, verifier.VerifySignatureFromBodyHash(authKey, "POST", "/agent/v1/files/upload", now, nonce, sha, sig, now));
            // Tampered hash must fail
            var badSha = new string('0', 64);
            Equal(false, verifier.VerifySignatureFromBodyHash(authKey, "POST", "/agent/v1/files/upload", now, "nonce-22345678", badSha, sig, now));
        }),

        ("AgentPeerServer + AgentPeerClient execute clipboard, streaming upload, display, privacy, and power endpoints", () =>
        {
            const string authKey = "m3-peer-auth-key-64chars";
            var root = Path.Combine(tempRoot, "m3-peer-" + Guid.NewGuid().ToString("N"));
            var uploadDir = Path.Combine(root, "uploads");
            Directory.CreateDirectory(root);

            try
            {
                var currentClipboard = new AgentClipboardDto(0, "none", null, null, null);
                var currentMode = new DisplayModeInfo(1920, 1080, 60);
                DisplayModeInfo? originalMode = null;
                var overrideActive = false;
                var privacyOn = false;
                string? lastPowerAction = null;

                var handlers = new AgentPeerFeatureHandlers(
                    GetClipboardAsync: _ => Task.FromResult(currentClipboard),
                    SetClipboardAsync: (req, _) =>
                    {
                        var raw = req.Kind == "text"
                            ? Encoding.UTF8.GetBytes(req.Text ?? "")
                            : Convert.FromBase64String(req.ImageBase64 ?? "");
                        var prefix = Encoding.ASCII.GetBytes(req.Kind + ":");
                        var combined = new byte[prefix.Length + raw.Length];
                        Buffer.BlockCopy(prefix, 0, combined, 0, prefix.Length);
                        Buffer.BlockCopy(raw, 0, combined, prefix.Length, raw.Length);
                        var sha = Convert.ToHexString(SHA256.HashData(combined)).ToLowerInvariant();
                        currentClipboard = new AgentClipboardDto(currentClipboard.Sequence + 1, req.Kind, req.Text, req.ImageBase64, sha);
                        return Task.FromResult(new AgentClipboardSetResponseDto(true, sha, null, "ok"));
                    },
                    ResolveUploadDirectoryAsync: _ => Task.FromResult(uploadDir),
                    GetDisplayStateAsync: _ => Task.FromResult(new AgentDisplayStateDto(
                        currentMode,
                        [new DisplayModeInfo(1920, 1080, 60), new DisplayModeInfo(2560, 1440, 120)],
                        overrideActive,
                        originalMode)),
                    ApplyDisplayModeAsync: (req, _) =>
                    {
                        originalMode ??= currentMode;
                        currentMode = DisplayResolutionPlanner.SelectBestMode(
                            req.Width,
                            req.Height,
                            req.RefreshRate,
                            currentMode,
                            [new DisplayModeInfo(1920, 1080, 60), new DisplayModeInfo(2560, 1440, 120)]);
                        overrideActive = true;
                        return Task.FromResult(new AgentDisplayApplyResultDto(true, true, currentMode, originalMode, null, "matched"));
                    },
                    RestoreDisplayModeAsync: _ =>
                    {
                        if (originalMode is not null)
                            currentMode = originalMode;
                        overrideActive = false;
                        return Task.FromResult(new AgentDisplayApplyResultDto(true, true, currentMode, originalMode, null, "restored"));
                    },
                    SetPrivacyScreenAsync: (enabled, _) =>
                    {
                        privacyOn = enabled;
                        return Task.FromResult(new AgentPrivacyResultDto(true, enabled, null, enabled ? "on" : "off"));
                    },
                    ExecutePowerActionAsync: (action, _) =>
                    {
                        lastPowerAction = action;
                        return Task.FromResult(new AgentPowerResultDto(true, action, null, $"exec:{action}"));
                    });

                var state = new AgentPeerServerState("dev-m3", "TEST-HOST", true, 47989, authKey, false, null, null);
                using var server = new AgentPeerServer(() => state, (_, _, _) => Task.FromResult((true, "ok")), handlers);
                using var bridgeHandler = new PeerServerBridgeHandler(server);
                using var client = new AgentPeerClient(bridgeHandler);

                // 1. Clipboard text & image round-trip
                var clipTextResp = client.SetClipboardAsync("10.126.126.5", 47999, authKey, new AgentClipboardSetRequestDto("text", "你好 RemoteDesk 2.0", null)).GetAwaiter().GetResult();
                Equal(true, clipTextResp.Ok);
                var (clipOk, gotClip, _) = client.GetClipboardAsync("10.126.126.5", 47999, authKey).GetAwaiter().GetResult();
                Equal(true, clipOk);
                Equal("text", gotClip?.Kind);
                Equal("你好 RemoteDesk 2.0", gotClip?.Text);

                var fakePng = Convert.ToBase64String([137, 80, 78, 71, 13, 10, 26, 10]);
                Equal(true, client.SetClipboardAsync("10.126.126.5", 47999, authKey, new AgentClipboardSetRequestDto("image", null, fakePng)).GetAwaiter().GetResult().Ok);
                var (_, gotImg, _) = client.GetClipboardAsync("10.126.126.5", 47999, authKey).GetAwaiter().GetResult();
                Equal("image", gotImg?.Kind);
                Equal(fakePng, gotImg?.ImageBase64);

                // 2. Streaming file upload + collision rename
                var localFile = Path.Combine(root, "sample.bin");
                var payload = RandomNumberGenerator.GetBytes(256 * 1024);
                File.WriteAllBytes(localFile, payload);
                var up1 = client.UploadFileAsync("10.126.126.5", 47999, authKey, localFile).GetAwaiter().GetResult();
                Equal(true, up1.Ok);
                Equal("sample.bin", up1.FileName);
                Equal((long)payload.Length, up1.SizeBytes);
                Equal(true, File.Exists(up1.SavedPath!));

                var up2 = client.UploadFileAsync("10.126.126.5", 47999, authKey, localFile).GetAwaiter().GetResult();
                Equal(true, up2.Ok);
                Equal("sample (1).bin", up2.FileName);

                // Tampered upload body against declared hash must fail with 400 and leave zero .tmp files
                var badSha = new string('a', 64);
                var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var nonce = "bad-upload-nonce-1";
                var sig = AgentHmacAuth.ComputeSignatureFromBodyHash(authKey, "POST", "/agent/v1/files/upload", ts, nonce, badSha);
                using (var httpClient = new HttpClient(bridgeHandler, disposeHandler: false))
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, "http://10.126.126.5:47999/agent/v1/files/upload")
                    {
                        Content = new ByteArrayContent("tampered-bytes"u8.ToArray()),
                    };
                    req.Headers.TryAddWithoutValidation(AgentHmacAuth.HeaderTimestamp, ts.ToString(CultureInfo.InvariantCulture));
                    req.Headers.TryAddWithoutValidation(AgentHmacAuth.HeaderNonce, nonce);
                    req.Headers.TryAddWithoutValidation(AgentHmacAuth.HeaderSignature, sig);
                    req.Headers.TryAddWithoutValidation(AgentHmacAuth.HeaderFileName, Convert.ToBase64String("bad.bin"u8.ToArray()));
                    req.Headers.TryAddWithoutValidation(AgentHmacAuth.HeaderFileSha256, badSha);
                    using var badResp = httpClient.SendAsync(req).GetAwaiter().GetResult();
                    Equal(HttpStatusCode.BadRequest, badResp.StatusCode);
                }
                Equal(0, Directory.GetFiles(uploadDir, "*.tmp").Length);

                // 3. Display apply & restore
                var applyRes = client.ApplyDisplayModeAsync("10.126.126.5", 47999, authKey, 2560, 1440, 120).GetAwaiter().GetResult();
                Equal(true, applyRes.Ok);
                Equal(2560, applyRes.Applied?.Width);
                var (dispOk, dispState, _) = client.GetDisplayStateAsync("10.126.126.5", 47999, authKey).GetAwaiter().GetResult();
                Equal(true, dispOk);
                Equal(true, dispState?.OverrideActive);
                var restoreRes = client.RestoreDisplayModeAsync("10.126.126.5", 47999, authKey).GetAwaiter().GetResult();
                Equal(true, restoreRes.Ok);
                Equal(1920, restoreRes.Applied?.Width);

                // 4. Privacy curtain & Power actions
                Equal(true, client.SetPrivacyScreenAsync("10.126.126.5", 47999, authKey, true).GetAwaiter().GetResult().Enabled);
                Equal(true, privacyOn);
                Equal(false, client.SetPrivacyScreenAsync("10.126.126.5", 47999, authKey, false).GetAwaiter().GetResult().Enabled);

                Equal(true, client.ExecutePowerActionAsync("10.126.126.5", 47999, authKey, "lock").GetAwaiter().GetResult().Ok);
                Equal("lock", lastPowerAction);
                Equal(false, client.ExecutePowerActionAsync("10.126.126.5", 47999, authKey, "hibernate").GetAwaiter().GetResult().Ok);
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }),

        ("SettingsStore migrates mixed v0.5/v0.6 devices cleanly without failing on RustDesk-only entries", () =>
        {
            var path = Path.Combine(tempRoot, "m3-migrate-" + Guid.NewGuid().ToString("N"), "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """
                {
                  "devices": [
                    { "name": "OfficeOnly", "id": "123456789", "preferredMode": "Office" },
                    { "name": "DormPC", "id": "987654321", "hostAddress": "10.126.126.2", "preferredMode": "Stream", "streamAppName": "Desktop" }
                  ],
                  "clientPath": "C:\\Tools\\rustdesk.exe",
                  "moonlightPath": "C:\\Tools\\Moonlight.exe"
                }
                """);

            var loaded = new SettingsStore(path).Load();
            Equal(1, loaded.Devices.Count);
            Equal("DormPC", loaded.Devices[0].Name);
            Equal("10.126.126.2", loaded.Devices[0].HostAddress);
            Equal(true, loaded.AutoMatchResolution);
            Equal(true, loaded.AutoSyncClipboard);
        }),

        ("Repository uses GPL-3.0 license and EngineCatalog converges to Sunshine + EasyTier", () =>
        {
            Equal(2, EngineCatalog.All.Count);
            Equal(true, EngineCatalog.All.Any(e => e.Kind == EngineKind.Sunshine));
            Equal(true, EngineCatalog.All.Any(e => e.Kind == EngineKind.EasyTier));

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string? licensePath = null;
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "LICENSE");
                if (File.Exists(candidate))
                {
                    licensePath = candidate;
                    break;
                }
                dir = dir.Parent;
            }

            Equal(true, licensePath is not null);
            var text = File.ReadAllText(licensePath!);
            Equal(true, text.Contains("GNU GENERAL PUBLIC LICENSE", StringComparison.Ordinal));
            Equal(true, text.Contains("Version 3", StringComparison.Ordinal));
        }),

        ("Agent IPC JSON uses single-line serialization and reads legacy indented responses safely", () =>
        {
            var compactOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var indentedOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

            var status = new AgentRuntimeStatus(
                ServiceRunning: true,
                IsSystemService: true,
                EasyTierRunning: true,
                EasyTierVirtualIpv4: "10.126.126.2",
                EasyTierPeers: [],
                EasyTierDetail: "ok",
                SunshineRunning: false,
                SunshineManagedReady: false,
                AgentPort: 47999,
                HostModeEnabled: true,
                SecurityPinRequired: false,
                LastCheckinUtc: 1_750_000_000L,
                LastError: null);

            var payloadJson = JsonSerializer.Serialize(status, compactOpts);
            var compactLine = JsonSerializer.Serialize(new AgentIpcResponse(true, payloadJson), compactOpts);
            Equal(false, compactLine.Contains('\n'));
            Equal(false, compactLine.Contains('\r'));

            // Legacy multi-line indented envelope read fully via StreamReader.ReadToEnd parses cleanly.
            var legacyIndented = JsonSerializer.Serialize(new AgentIpcResponse(true, JsonSerializer.Serialize(status, indentedOpts)), indentedOpts);
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(legacyIndented));
            using var reader = new StreamReader(ms, Encoding.UTF8);
            var fullRead = reader.ReadToEnd();
            var parsedResp = JsonSerializer.Deserialize<AgentIpcResponse>(fullRead, compactOpts);
            Equal(true, parsedResp is { Ok: true, PayloadJson.Length: > 0 });
            var parsedStatus = JsonSerializer.Deserialize<AgentRuntimeStatus>(parsedResp!.PayloadJson!, compactOpts);
            Equal("10.126.126.2", parsedStatus?.EasyTierVirtualIpv4);

            var stopLine = JsonSerializer.Serialize(new AgentIpcRequest("stop_self"), compactOpts);
            Equal(false, stopLine.Contains('\n'));
            Equal("stop_self", JsonSerializer.Deserialize<AgentIpcRequest>(stopLine, compactOpts)?.Command);

            var credsJson = JsonSerializer.Serialize(new AgentSunshineCredsDto("remotedesk", "SecPass123"), compactOpts);
            var credsDto = JsonSerializer.Deserialize<AgentSunshineCredsDto>(credsJson, compactOpts);
            Equal("remotedesk", credsDto?.Username);
            Equal("SecPass123", credsDto?.Password);
        }),

        ("SunshineManager.SubmitPairingPinDetailedAsync classifies 200, status=false, and 401 unauthorized", () =>
        {
            var statusToReturn = HttpStatusCode.OK;
            var bodyToReturn = """{"status":true}""";
            using var client = new HttpClient(new DelegateHttpHandler((req, _) =>
            {
                return Task.FromResult(new HttpResponseMessage(statusToReturn)
                {
                    Content = new StringContent(bodyToReturn, Encoding.UTF8, "application/json"),
                });
            }));

            var baseUri = new Uri("https://127.0.0.1:47990");
            var okRes = SunshineManager.SubmitPairingPinDetailedAsync("1234", "PC-A", "remotedesk", "pass", baseUri, client).GetAwaiter().GetResult();
            Equal(true, okRes.Success);
            Equal(false, okRes.IsUnauthorized);

            bodyToReturn = """{"status":false}""";
            var falseRes = SunshineManager.SubmitPairingPinDetailedAsync("1234", "PC-A", "remotedesk", "pass", baseUri, client).GetAwaiter().GetResult();
            Equal(false, falseRes.Success);
            Equal(false, falseRes.IsUnauthorized);

            statusToReturn = HttpStatusCode.Unauthorized;
            bodyToReturn = "";
            var unauthRes = SunshineManager.SubmitPairingPinDetailedAsync("1234", "PC-A", "remotedesk", "bad", baseUri, client).GetAwaiter().GetResult();
            Equal(false, unauthRes.Success);
            Equal(true, unauthRes.IsUnauthorized);
        }),

        ("SunshineManager.SubmitPinWithSelfHealAsync auto-recovers from HTTP 401 and retries /api/pin", () =>
        {
            var activePass = "stale-pass";
            var reprovisionCount = 0;

            using var client = new HttpClient(new DelegateHttpHandler((req, _) =>
            {
                var authHeader = req.Headers.Authorization?.Parameter ?? "";
                var expectedParam = Convert.ToBase64String(Encoding.UTF8.GetBytes($"remotedesk:{activePass}"));
                if (!string.Equals(authHeader, expectedParam, StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

                if (req.RequestUri!.AbsolutePath == "/api/config")
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"status":true}""", Encoding.UTF8, "application/json"),
                });
            }));

            var localStoredPass = "wrong-old-pass";
            var baseUri = new Uri("https://127.0.0.1:47990");
            var (success, message) = SunshineManager.SubmitPinWithSelfHealAsync(
                "4321",
                "CLIENT-1",
                getCredentials: () => ("remotedesk", localStoredPass),
                reprovisionAndStartAsync: _ =>
                {
                    reprovisionCount++;
                    activePass = "fresh-healed-pass";
                    localStoredPass = activePass;
                    return Task.FromResult<(bool, string, string, string?)>((true, "remotedesk", activePass, null));
                },
                wakeHostAsync: () => Task.FromResult(true),
                baseUri: baseUri,
                httpClient: client).GetAwaiter().GetResult();

            Equal(true, success);
            Equal(1, reprovisionCount);
            Equal(true, message.Contains("静默完成配对", StringComparison.Ordinal));
        }),

        ("SunshineManager submits Sunshine 2026 pairing_id + CSRF token and matches ::ffff: client IP", () =>
        {
            const string targetId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string otherId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            string? postedPairingId = null;
            string? postedPin = null;
            string? postedCsrf = null;

            using var client = new HttpClient(new DelegateHttpHandler(async (req, ct) =>
            {
                if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/api/pin")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($$"""
                            {
                              "pairings": [
                                { "id": "{{otherId}}", "name": "OtherPC", "address": "10.126.126.99" },
                                { "id": "{{targetId}}", "name": "Laptop", "address": "::ffff:10.126.126.5" }
                              ]
                            }
                            """, Encoding.UTF8, "application/json"),
                    };
                }

                if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/api/csrf-token")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"csrf_token":"csrf-tok-999"}""", Encoding.UTF8, "application/json"),
                    };
                }

                if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/api/pin")
                {
                    postedCsrf = req.Headers.TryGetValues("X-CSRF-Token", out var vals) ? vals.FirstOrDefault() : null;
                    var raw = await req.Content!.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(raw);
                    postedPairingId = doc.RootElement.TryGetProperty("pairing_id", out var pid) ? pid.GetString() : null;
                    postedPin = doc.RootElement.TryGetProperty("pin", out var pinProp) ? pinProp.GetString() : null;

                    var ok = string.Equals(postedPairingId, targetId, StringComparison.Ordinal) &&
                             string.Equals(postedCsrf, "csrf-tok-999", StringComparison.Ordinal);
                    return new HttpResponseMessage(ok ? HttpStatusCode.OK : HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent(ok ? """{"status":true}""" : """{"status":false}""", Encoding.UTF8, "application/json"),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));

            var res = SunshineManager.SubmitPairingPinDetailedAsync(
                "4829",
                "Laptop-1",
                "remotedesk",
                "pass",
                new Uri("https://127.0.0.1:47990"),
                client,
                CancellationToken.None,
                remoteClientIp: "10.126.126.5").GetAwaiter().GetResult();

            Equal(true, res.Success);
            Equal(targetId, postedPairingId);
            Equal("4829", postedPin);
            Equal("csrf-tok-999", postedCsrf);
        }),

        ("SunshineManager polls GET /api/pin until pairing appears and falls back to legacy on 404", () =>
        {
            var getPinCount = 0;
            const string polledId = "cccccccccccccccccccccccccccccccc";
            using var pollingClient = new HttpClient(new DelegateHttpHandler((req, _) =>
            {
                if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/api/pin")
                {
                    getPinCount++;
                    var json = getPinCount < 3
                        ? """{"pairings":[]}"""
                        : $$"""{"pairings":[{"id":"{{polledId}}","name":"Client","address":"10.126.126.8"}]}""";
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    });
                }
                if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/api/csrf-token")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"csrf_token":"tok"}""", Encoding.UTF8, "application/json"),
                    });
                }
                if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/api/pin")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"status":true}""", Encoding.UTF8, "application/json"),
                    });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }));

            var polledRes = SunshineManager.SubmitPairingPinDetailedAsync(
                "1357", "Client", "remotedesk", "pass", new Uri("https://127.0.0.1:47990"), pollingClient).GetAwaiter().GetResult();
            Equal(true, polledRes.Success);
            Equal(3, getPinCount);

            var legacyPostHadPairingId = true;
            using var legacyClient = new HttpClient(new DelegateHttpHandler(async (req, ct) =>
            {
                if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/api/pin")
                    return new HttpResponseMessage(HttpStatusCode.NotFound);

                if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/api/pin")
                {
                    var raw = await req.Content!.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(raw);
                    legacyPostHadPairingId = doc.RootElement.TryGetProperty("pairing_id", out _);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"status":true}""", Encoding.UTF8, "application/json"),
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));

            var legacyRes = SunshineManager.SubmitPairingPinDetailedAsync(
                "2468", "LegacyClient", "remotedesk", "pass", new Uri("https://127.0.0.1:47990"), legacyClient).GetAwaiter().GetResult();
            Equal(true, legacyRes.Success);
            Equal(false, legacyPostHadPairingId);
        }),

        ("GameStreamClient.PairWithPinAsync cancels blocked getservercert immediately when callback fails", () =>
        {
            var idDir = Path.Combine(tempRoot, "gs-cancel-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var id = GameStreamIdentity.LoadOrCreate(idDir);
                using var handler = new DelegateHttpHandler(async (req, ct) =>
                {
                    if (req.RequestUri!.AbsolutePath == "/serverinfo")
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(
                                "<root status_code=\"200\"><hostname>H</hostname><appversion>7.1.431.-1</appversion><PairStatus>0</PairStatus></root>",
                                Encoding.UTF8,
                                "application/xml"),
                        };
                    }

                    if (req.RequestUri!.AbsolutePath == "/pair")
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                    }

                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                });

                var client = new GameStreamClient(id, handler);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var threwFast = false;
                try
                {
                    client.PairWithPinAsync(
                        "10.126.126.2",
                        "5678",
                        onPinReadyAsync: _ => throw new InvalidOperationException("fast-fail-reason")).GetAwaiter().GetResult();
                }
                catch (InvalidOperationException ex)
                {
                    sw.Stop();
                    threwFast = ex.Message == "fast-fail-reason" && sw.Elapsed < TimeSpan.FromSeconds(3);
                }

                Equal(true, threwFast);
            }
            finally
            {
                if (Directory.Exists(idDir))
                    Directory.Delete(idDir, recursive: true);
            }
        }),

        ("AutoPairOrchestrator retries handshake on SUNSHINE_REPROVISIONED_RETRY and preserves host error messages", () =>
        {
            var idDir = Path.Combine(tempRoot, "autopair-retry-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var id = GameStreamIdentity.LoadOrCreate(idDir);
                var getServerCertCount = 0;
                var gsHandler = new DelegateHttpHandler(async (req, ct) =>
                {
                    if (req.RequestUri!.AbsolutePath == "/serverinfo")
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(
                                "<root status_code=\"200\"><hostname>H</hostname><appversion>7.1.431.-1</appversion><PairStatus>0</PairStatus></root>",
                                Encoding.UTF8,
                                "application/xml"),
                        };
                    }

                    if (req.RequestUri!.AbsolutePath == "/pair" && req.RequestUri.Query.Contains("phrase=getservercert", StringComparison.Ordinal))
                    {
                        getServerCertCount++;
                        await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                    }

                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                });

                const string authKey = "retry-test-auth-key-0123456789ab";
                var pairAttempts = 0;
                using var server = new AgentPeerServer(
                    () => new AgentPeerServerState("dev-r", "HOST-R", true, 47989, authKey, false, null, null),
                    (_, _, _, _) =>
                    {
                        pairAttempts++;
                        if (pairAttempts == 1)
                            return Task.FromResult<(bool, string, string?)>((false, "Sunshine 已自动修复托管凭据并重启，正在重新发起配对握手。", AgentIpcConstants.ErrorSunshineReprovisionedRetry));
                        return Task.FromResult<(bool, string, string?)>((false, "EXACT_HOST_FAILURE_REASON", "SUNSHINE_PAIR_FAILED"));
                    });

                using var bridge = new PeerServerBridgeHandler(server);
                using var peerClient = new AgentPeerClient(bridge);
                var orchestrator = new AutoPairOrchestrator(peerClient);
                var gsClient = new GameStreamClient(id, gsHandler);

                string? observedErr = null;
                try
                {
                    _ = orchestrator.NativeAutoPairAsync(gsClient, "10.126.126.2", 47999, authKey, "Client-X", null).GetAwaiter().GetResult();
                }
                catch (InvalidOperationException ex)
                {
                    observedErr = ex.Message;
                }

                Equal(2, getServerCertCount);
                Equal("EXACT_HOST_FAILURE_REASON", observedErr);
            }
            finally
            {
                if (Directory.Exists(idDir))
                    Directory.Delete(idDir, recursive: true);
            }
        }),

        ("SunshineManager.EnsureSafeLocalConfig writes address_family=both and disables tray only for managed paths", () =>
        {
            var dir = Path.Combine(tempRoot, "sunshine-cfg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var unmanagedConf = Path.Combine(dir, "unmanaged.conf");
                SunshineManager.EnsureSafeLocalConfig(unmanagedConf, isManagedInstall: false);
                var unmanagedText = File.ReadAllText(unmanagedConf);
                Equal(true, unmanagedText.Contains("origin_web_ui_allowed = pc", StringComparison.Ordinal));
                Equal(true, unmanagedText.Contains("upnp = off", StringComparison.Ordinal));
                Equal(true, unmanagedText.Contains("address_family = both", StringComparison.Ordinal));
                Equal(false, unmanagedText.Contains("system_tray = disabled", StringComparison.Ordinal));

                var managedConf = Path.Combine(dir, "managed.conf");
                SunshineManager.EnsureSafeLocalConfig(managedConf, isManagedInstall: true);
                var managedText = File.ReadAllText(managedConf);
                Equal(true, managedText.Contains("address_family = both", StringComparison.Ordinal));
                Equal(true, managedText.Contains("system_tray = disabled", StringComparison.Ordinal));
                Equal(true, managedText.Contains("notify_pre_releases = disabled", StringComparison.Ordinal));
                Equal(true, SunshineManager.IsManagedSunshinePath(@"C:\Users\Alice\AppData\Local\RemoteDesk\engines\sunshine\sunshine.exe"));
                Equal(false, SunshineManager.IsManagedSunshinePath(@"C:\Program Files\Sunshine\sunshine.exe"));
            }
            finally
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
        }),

        ("GameStreamClient.PairWithPinAsync does not deadlock when POST /api/pin blocks until pairchallenge", () =>
        {
            var idDir = Path.Combine(tempRoot, "gs-sunshine2026-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var id = GameStreamIdentity.LoadOrCreate(idDir);
                using var serverRsa = RSA.Create(2048);
                var certReq = new CertificateRequest("CN=Sunshine-2026-Mock", serverRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var serverCert = certReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
                var serverCertPemHex = Convert.ToHexString(Encoding.ASCII.GetBytes(
                    serverCert.ExportCertificatePem().Replace("\r\n", "\n", StringComparison.Ordinal))).ToLowerInvariant();
                var serverCertSig = GameStreamIdentity.ExtractCertificateSignatureBytes(serverCert);
                var serverChallenge = RandomNumberGenerator.GetBytes(16);
                var serverSecret = RandomNumberGenerator.GetBytes(16);

                var pinGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var pairDoneGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                byte[] aesKey = [];
                byte[] clientCertPem = [];

                static Dictionary<string, string> ParseQueryParams(string q)
                {
                    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var part in q.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var eq = part.IndexOf('=');
                        if (eq > 0)
                            d[Uri.UnescapeDataString(part[..eq])] = Uri.UnescapeDataString(part[(eq + 1)..]);
                    }
                    return d;
                }

                static byte[] Concat(params byte[][] arrays)
                {
                    var res = new byte[arrays.Sum(a => a.Length)];
                    var off = 0;
                    foreach (var a in arrays)
                    {
                        Buffer.BlockCopy(a, 0, res, off, a.Length);
                        off += a.Length;
                    }
                    return res;
                }

                using var handler = new DelegateHttpHandler(async (req, ct) =>
                {
                    var uri = req.RequestUri!;
                    var query = ParseQueryParams(uri.Query);

                    if (uri.AbsolutePath == "/serverinfo")
                    {
                        if (string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                        {
                            return new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StringContent(
                                    "<root status_code=\"401\" status_message=\"Certificate verification failed.\"/>",
                                    Encoding.UTF8,
                                    "application/xml"),
                            };
                        }
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(
                                "<root status_code=\"200\"><hostname>SUN2026</hostname><appversion>7.1.431.-1</appversion><PairStatus>0</PairStatus><ServerCodecModeSupport>1835777</ServerCodecModeSupport></root>",
                                Encoding.UTF8,
                                "application/xml"),
                        };
                    }

                    if (uri.AbsolutePath == "/pair")
                    {
                        if (query.TryGetValue("phrase", out var phrase) && phrase == "getservercert")
                        {
                            await pinGate.Task.WaitAsync(ct).ConfigureAwait(false);
                            var salt = Convert.FromHexString(query["salt"]);
                            aesKey = SHA256.HashData(Concat(salt, "4829"u8.ToArray()))[..16];
                            clientCertPem = Convert.FromHexString(query["clientcert"]);
                            return new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StringContent($"<root status_code=\"200\"><paired>1</paired><plaincert>{serverCertPemHex}</plaincert></root>", Encoding.UTF8, "application/xml"),
                            };
                        }

                        if (query.TryGetValue("clientchallenge", out var encChallengeHex))
                        {
                            var clientChallenge = GameStreamClient.Aes128EcbDecrypt(aesKey, Convert.FromHexString(encChallengeHex));
                            var serverHash = SHA256.HashData(Concat(clientChallenge, serverCertSig, serverSecret));
                            var encResp = GameStreamClient.Aes128EcbEncrypt(aesKey, Concat(serverHash, serverChallenge));
                            return new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StringContent($"<root status_code=\"200\"><paired>1</paired><challengeresponse>{Convert.ToHexString(encResp).ToLowerInvariant()}</challengeresponse></root>", Encoding.UTF8, "application/xml"),
                            };
                        }

                        if (query.ContainsKey("serverchallengeresp"))
                        {
                            var serverSig = serverRsa.SignData(serverSecret, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                            var pairingSecret = Concat(serverSecret, serverSig);
                            return new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StringContent($"<root status_code=\"200\"><paired>1</paired><pairingsecret>{Convert.ToHexString(pairingSecret).ToLowerInvariant()}</pairingsecret></root>", Encoding.UTF8, "application/xml"),
                            };
                        }

                        if (query.TryGetValue("clientpairingsecret", out var clientSecretHex))
                        {
                            var bytes = Convert.FromHexString(clientSecretHex);
                            using var clientCert = X509Certificate2.CreateFromPem(Encoding.ASCII.GetString(clientCertPem));
                            using var clientRsa = clientCert.GetRSAPublicKey()!;
                            var okSig = clientRsa.VerifyData(bytes[..16], bytes[16..], HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                            return new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StringContent($"<root status_code=\"200\"><paired>{(okSig ? 1 : 0)}</paired></root>", Encoding.UTF8, "application/xml"),
                            };
                        }

                        if (query.TryGetValue("phrase", out var finalPhrase) && finalPhrase == "pairchallenge")
                        {
                            pairDoneGate.TrySetResult();
                            return new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StringContent("<root status_code=\"200\"><paired>1</paired></root>", Encoding.UTF8, "application/xml"),
                            };
                        }
                    }

                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                });

                var client = new GameStreamClient(id, handler);
                var pinCallbackFinished = false;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (ok, _) = client.PairWithPinAsync(
                    "10.126.126.9",
                    "4829",
                    onPinReadyAsync: async token =>
                    {
                        pinGate.TrySetResult();
                        // Simulate Sunshine 2026 keeping POST /api/pin blocked until pairchallenge completes!
                        await pairDoneGate.Task.WaitAsync(token).ConfigureAwait(false);
                        pinCallbackFinished = true;
                    }).GetAwaiter().GetResult();
                sw.Stop();

                Equal(true, ok);
                Equal(true, pairDoneGate.Task.IsCompleted);
                Equal(true, pinCallbackFinished);
                Equal(true, sw.Elapsed < TimeSpan.FromSeconds(5));
            }
            finally
            {
                if (Directory.Exists(idDir))
                    Directory.Delete(idDir, recursive: true);
            }
        }),

        ("GameStreamClient.GetServerInfoAsync falls back to HTTP when HTTPS returns status_code=401", () =>
        {
            var idDir = Path.Combine(tempRoot, "gs-serverinfo401-" + Guid.NewGuid().ToString("N"));
            try
            {
                Throws<InvalidDataException>(() =>
                    GameStreamClient.ParseServerInfoXml("<root status_code=\"401\" status_message=\"Certificate verification failed.\"/>"));

                using var id = GameStreamIdentity.LoadOrCreate(idDir);
                using var handler = new DelegateHttpHandler((req, _) =>
                {
                    if (string.Equals(req.RequestUri!.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(
                                "<root status_code=\"401\" query=\"\" status_message=\"The client is not authorized. Certificate verification failed.\"/>",
                                Encoding.UTF8,
                                "application/xml"),
                        });
                    }

                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            "<root status_code=\"200\"><hostname>REAL-HOST</hostname><appversion>7.1.431.-1</appversion><PairStatus>0</PairStatus><ServerCodecModeSupport>1835777</ServerCodecModeSupport></root>",
                            Encoding.UTF8,
                            "application/xml"),
                    });
                });

                var client = new GameStreamClient(id, handler);
                var info = client.GetServerInfoAsync("10.126.126.4").GetAwaiter().GetResult();
                Equal("REAL-HOST", info.HostName);
                Equal(1835777, info.ServerCodecModeSupport);
                Equal(0, info.PairStatus);
            }
            finally
            {
                if (Directory.Exists(idDir))
                    Directory.Delete(idDir, recursive: true);
            }
        }),

        ("SunshineManager does not misclassify post-reachability TaskCanceledException as IsUnreachable", () =>
        {
            const string targetId = "dddddddddddddddddddddddddddddddd";
            using var client = new HttpClient(new DelegateHttpHandler((req, _) =>
            {
                if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/api/pin")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($$"""{"pairings":[{"id":"{{targetId}}","name":"roth","address":"10.126.126.2"}]}""", Encoding.UTF8, "application/json"),
                    });
                }
                if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/api/csrf-token")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"csrf_token":"tok-1"}""", Encoding.UTF8, "application/json"),
                    });
                }
                if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/api/pin")
                {
                    throw new TaskCanceledException("Simulated handshake timeout while /api/pin was blocked");
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }));

            var res = SunshineManager.SubmitPairingPinDetailedAsync(
                "1234", "Client-A", "remotedesk", "pass", new Uri("https://127.0.0.1:47990"), client).GetAwaiter().GetResult();
            Equal(false, res.Success);
            Equal(false, res.IsUnreachable);
            Equal(false, res.IsUnauthorized);
        }),

        ("AutoPairOrchestrator fails fast with upgrade guidance when legacy peer without AgentVersion returns HTTP 400", () =>
        {
            var idDir = Path.Combine(tempRoot, "autopair-legacy400-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var id = GameStreamIdentity.LoadOrCreate(idDir);
                using var gsHandler = new DelegateHttpHandler(async (req, ct) =>
                {
                    if (req.RequestUri!.AbsolutePath == "/serverinfo")
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(
                                "<root status_code=\"200\"><hostname>LEGACY-HOST</hostname><appversion>7.1.431.-1</appversion><PairStatus>0</PairStatus></root>",
                                Encoding.UTF8,
                                "application/xml"),
                        };
                    }
                    if (req.RequestUri!.AbsolutePath == "/pair")
                        await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                });

                var pairCallCount = 0;
                using var peerHandler = new DelegateHttpHandler((req, _) =>
                {
                    if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/agent/v1/info")
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(
                                """{"deviceKey":"dev-legacy","hostname":"LEGACY-HOST","sunshineReady":true,"sunshinePort":47989,"securityPinRequired":false}""",
                                Encoding.UTF8,
                                "application/json"),
                        });
                    }
                    if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/agent/v1/pair")
                    {
                        pairCallCount++;
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
                        {
                            Content = new StringContent(
                                """{"ok":false,"error":"SUNSHINE_PAIR_FAILED","message":"Sunshine 配对接口拒绝请求（HTTP 400）。"}""",
                                Encoding.UTF8,
                                "application/json"),
                        });
                    }
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                });

                using var peerClient = new AgentPeerClient(peerHandler);
                var orchestrator = new AutoPairOrchestrator(peerClient);
                var gsClient = new GameStreamClient(id, gsHandler);

                string? observedErr = null;
                try
                {
                    _ = orchestrator.NativeAutoPairAsync(gsClient, "10.126.126.4", 47999, "legacy-auth-key-0123456789abcdef", "Client-Y", null).GetAwaiter().GetResult();
                }
                catch (InvalidOperationException ex)
                {
                    observedErr = ex.Message;
                }

                Equal(1, pairCallCount);
                Equal(true, observedErr?.Contains("版本过旧", StringComparison.Ordinal) == true);
            }
            finally
            {
                if (Directory.Exists(idDir))
                    Directory.Delete(idDir, recursive: true);
            }
        }),

        ("GameStreamClient.CreateDefaultHttpsHandler disables TLS resumption and attaches client certificate", () =>
        {
            var idDir = Path.Combine(tempRoot, "gs-tls-handler-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var id = GameStreamIdentity.LoadOrCreate(idDir);
                using var handler = GameStreamClient.CreateDefaultHttpsHandler(id.ClientCertificate);
                Equal(false, handler.UseProxy);
                Equal(false, handler.SslOptions.AllowTlsResume);
                Equal(1, handler.SslOptions.ClientCertificates?.Count ?? 0);
                Equal(true, ReferenceEquals(id.ClientCertificate, handler.SslOptions.ClientCertificates![0]));
            }
            finally
            {
                if (Directory.Exists(idDir))
                    Directory.Delete(idDir, recursive: true);
            }
        }),
    ];

    private sealed class DelegateHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }

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

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
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
