using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace RemoteDesk;

internal static class EasyTierChecks
{
    public static IReadOnlyList<(string Name, Action Run)> CreateTests(string tempRoot) =>
    [
        ("EasyTierConfig.Validate accepts valid configs and rejects invalid schemes, CLI injection, and whitespace", () =>
        {
            var validAuto = EasyTierConfig.Validate(new EasyTierConfig
            {
                Enabled = true,
                NetworkName = "campus-mesh_01",
                NetworkSecret = "Secr3t.Key-2026",
                AutoAssignIpv4 = true,
                PeerUrl = "tcp://1.2.3.4:11010",
                EnableRelayFallback = true,
                AutoStartWithApp = true,
            });
            Equal("campus-mesh_01", validAuto.NetworkName);
            Equal("Secr3t.Key-2026", validAuto.NetworkSecret);
            Equal("tcp://1.2.3.4:11010", validAuto.PeerUrl);

            // Empty PeerUrl is valid (LAN-only auto discovery).
            var noPeer = EasyTierConfig.Validate(new EasyTierConfig
            {
                NetworkName = "dorm-lan",
                NetworkSecret = "pass1234",
                PeerUrl = "",
            });
            Equal("", noPeer.PeerUrl);

            // All supported PeerUrl schemes and IPv6 host syntax.
            Equal("tcp://1.2.3.4:11010", EasyTierConfig.ValidatePeerUrl("tcp://1.2.3.4:11010"));
            Equal("udp://relay.example.com:11010", EasyTierConfig.ValidatePeerUrl("udp://relay.example.com:11010"));
            Equal("wg://1.2.3.4:11011", EasyTierConfig.ValidatePeerUrl("wg://1.2.3.4:11011"));
            Equal("ws://relay.example.com:11011", EasyTierConfig.ValidatePeerUrl("ws://relay.example.com:11011"));
            Equal("wss://relay.example.com/easytier", EasyTierConfig.ValidatePeerUrl("wss://relay.example.com/easytier"));
            Equal("tcp://[2001:db8::1]:11010", EasyTierConfig.ValidatePeerUrl("tcp://[2001:db8::1]:11010"));
            Equal("", EasyTierConfig.ValidatePeerUrl(null));
            Equal("", EasyTierConfig.ValidatePeerUrl(""));

            // Static VirtualIpv4 with and without CIDR prefix 8..30.
            var staticBare = EasyTierConfig.Validate(new EasyTierConfig
            {
                NetworkName = "mesh",
                NetworkSecret = "secret",
                AutoAssignIpv4 = false,
                VirtualIpv4 = "10.126.126.1",
            });
            Equal("10.126.126.1", staticBare.VirtualIpv4);

            var staticCidr = EasyTierConfig.Validate(new EasyTierConfig
            {
                NetworkName = "mesh",
                NetworkSecret = "secret",
                AutoAssignIpv4 = false,
                VirtualIpv4 = "10.126.126.2/24",
            });
            Equal("10.126.126.2/24", staticCidr.VirtualIpv4);

            // Invalid network tokens: empty, leading '-', whitespace, control chars, shell metacharacters.
            string?[] badTokens =
            [
                null,
                "",
                "   ",
                "-d",
                "--foo",
                "my mesh",
                " leading",
                "trailing ",
                "mesh\tname",
                "mesh\n--evil",
                "mesh\0null",
                "mesh&calc",
                "mesh;whoami",
                "mesh|sh",
                "$(calc)",
                "`id`",
                "mesh\"arg",
                "mesh'arg",
                "mesh>out",
                "mesh<in",
                "mesh%PATH%",
                "mesh\\path",
            ];
            foreach (var bad in badTokens)
            {
                Throws<ArgumentException>(() => EasyTierConfig.ValidateNetworkToken(bad, "networkName"));
                Throws<ArgumentException>(() => EasyTierConfig.Validate(new EasyTierConfig
                {
                    NetworkName = bad ?? "",
                    NetworkSecret = "valid-secret",
                }));
                Throws<ArgumentException>(() => EasyTierConfig.Validate(new EasyTierConfig
                {
                    NetworkName = "valid-net",
                    NetworkSecret = bad ?? "",
                }));
            }

            // Invalid peer URLs: disallowed schemes, missing port, injection, whitespace, userinfo/query.
            string[] badPeerUrls =
            [
                "   ",
                "http://1.2.3.4:11010",
                "https://1.2.3.4:11010",
                "ftp://1.2.3.4:21",
                "file:///C:/Windows/calc.exe",
                "tcp://1.2.3.4",
                "tcp://1.2.3.4:0",
                "tcp://:11010",
                "tcp://user:pass@1.2.3.4:11010",
                "tcp://1.2.3.4:11010?k=v",
                "tcp://1.2.3.4:11010#frag",
                "tcp://1.2.3.4:11010/path",
                "--peer=tcp://1.2.3.4:11010",
                "tcp://1.2.3.4:11010 --foo",
                " tcp://1.2.3.4:11010",
                "tcp://1.2.3.4:11010&calc",
                "tcp://1.2.3.4:11010\ncalc",
            ];
            foreach (var badUrl in badPeerUrls)
            {
                Throws<ArgumentException>(() => EasyTierConfig.ValidatePeerUrl(badUrl));
            }

            // Invalid static IPv4 values when AutoAssignIpv4 == false.
            string[] badIps =
            [
                "-i",
                "10.126.126.1 --foo",
                "999.1.1.1",
                "010.126.126.1",
                "0.0.0.0",
                "127.0.0.1",
                "255.255.255.255",
                "10.126.126.1/7",
                "10.126.126.1/31",
                "10.126.126.1/08",
                "10.126.126.1/abc",
                "10.126.126.1/24/24",
            ];
            foreach (var badIp in badIps)
            {
                Throws<ArgumentException>(() => EasyTierConfig.Validate(new EasyTierConfig
                {
                    NetworkName = "mesh",
                    NetworkSecret = "secret",
                    AutoAssignIpv4 = false,
                    VirtualIpv4 = badIp,
                }));
            }
        }),

        ("EasyTierManager.CreateCoreStartInfo builds exact ArgumentList for auto-IP (-d) and static-IP (-i)", () =>
        {
            var root = Path.Combine(tempRoot, "easytier-args-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var coreExe = Path.Combine(root, "easytier-core.exe");
            File.WriteAllBytes(coreExe, []);

            var expectedHost = EasyTierManager.SanitizeHostname(Environment.MachineName);
            Equal(true, !string.IsNullOrWhiteSpace(expectedHost) && !expectedHost.StartsWith('-'));

            // Auto-IP (-d) with peer URL.
            var autoConfig = new EasyTierConfig
            {
                Enabled = true,
                NetworkName = "campus-net",
                NetworkSecret = "s3cr3t",
                AutoAssignIpv4 = true,
                VirtualIpv4 = "10.126.126.9", // Ignored because AutoAssignIpv4 is true.
                PeerUrl = "tcp://47.100.1.2:11010",
            };
            var autoStart = EasyTierManager.CreateCoreStartInfo(coreExe, autoConfig);
            Equal(coreExe, autoStart.FileName);
            Equal(false, autoStart.UseShellExecute);
            Equal(true, autoStart.CreateNoWindow);
            Equal(
                $"--network-name|campus-net|--network-secret|s3cr3t|-d|-p|tcp://47.100.1.2:11010|--hostname|{expectedHost}|--rpc-portal|{EasyTierManager.DefaultRpcPortal}",
                string.Join('|', autoStart.ArgumentList));
            Equal(false, autoStart.ArgumentList.Contains("--no-tun"));

            // Static-IP (-i) without peer URL.
            var staticConfig = new EasyTierConfig
            {
                Enabled = true,
                NetworkName = "lab-mesh",
                NetworkSecret = "lab-pass",
                AutoAssignIpv4 = false,
                VirtualIpv4 = "10.126.126.5/24",
                PeerUrl = "",
            };
            var staticStart = EasyTierManager.CreateCoreStartInfo(coreExe, staticConfig);
            Equal(
                $"--network-name|lab-mesh|--network-secret|lab-pass|-i|10.126.126.5/24|--hostname|{expectedHost}|--rpc-portal|{EasyTierManager.DefaultRpcPortal}",
                string.Join('|', staticStart.ArgumentList));

            // Static mode with empty VirtualIpv4 falls back to -d.
            var fallbackAuto = EasyTierManager.CreateCoreStartInfo(coreExe, new EasyTierConfig
            {
                NetworkName = "lab-mesh",
                NetworkSecret = "lab-pass",
                AutoAssignIpv4 = false,
                VirtualIpv4 = null,
            });
            Equal(
                $"--network-name|lab-mesh|--network-secret|lab-pass|-d|--hostname|{expectedHost}|--rpc-portal|{EasyTierManager.DefaultRpcPortal}",
                string.Join('|', fallbackAuto.ArgumentList));

            // Missing executable or invalid config must throw.
            Throws<FileNotFoundException>(() => EasyTierManager.CreateCoreStartInfo(Path.Combine(root, "missing.exe"), autoConfig));
            Throws<ArgumentException>(() => EasyTierManager.CreateCoreStartInfo(coreExe, autoConfig with { NetworkName = "--inject" }));
        }),

        ("EasyTierManager discovers executables with selected > managed precedence and finds sibling CLI", () =>
        {
            var root = Path.Combine(tempRoot, "easytier-find-" + Guid.NewGuid().ToString("N"));
            var selectedDir = Path.Combine(root, "selected");
            var managedDir = Path.Combine(root, "managed");
            Directory.CreateDirectory(selectedDir);
            Directory.CreateDirectory(managedDir);

            var selectedCore = Path.Combine(selectedDir, "easytier-core.exe");
            var selectedCli = Path.Combine(selectedDir, "easytier-cli.exe");
            var managedCore = Path.Combine(managedDir, "easytier-core.exe");
            File.WriteAllBytes(selectedCore, []);
            File.WriteAllBytes(selectedCli, []);
            File.WriteAllBytes(managedCore, []);

            Equal(selectedCore, EasyTierManager.FindCoreExecutable(selectedCore, managedCore));
            Equal(managedCore, EasyTierManager.FindCoreExecutable(null, managedCore));
            Equal(managedCore, EasyTierManager.FindCoreExecutable(Path.Combine(root, "missing.exe"), managedCore));

            Equal(selectedCli, EasyTierManager.FindCliExecutable(selectedCore));
            Equal<string?>(null, EasyTierManager.FindCliExecutable(managedCore));
            Equal<string?>(null, EasyTierManager.FindCliExecutable(null));
        }),

        ("EasyTierManager.ParsePeersJson parses local self, P2P direct, Relay peers, latency, and local IPv4", () =>
        {
            const string sampleJson = """
            [
              {
                "ipv4": "10.126.126.1/24",
                "hostname": "LAPTOP-CLIENT",
                "cost": "Local",
                "lat_ms": "-",
                "tunnel_proto": "-"
              },
              {
                "ipv4": "10.126.126.2/24",
                "hostname": "DORM-DESKTOP",
                "cost": "p2p",
                "lat_ms": "8.42",
                "tunnel_proto": "udp"
              },
              {
                "ipv4": "10.126.126.3",
                "hostname": "LAB-WORKSTATION",
                "cost": "relay(1)",
                "lat_ms": "43.75ms",
                "tunnel_proto": "tcp"
              },
              {
                "ipv4": "-",
                "hostname": "UNKNOWN-NODE",
                "cost": "-",
                "lat_ms": "-",
                "tunnel_proto": "-"
              }
            ]
            """;

            var peers = EasyTierManager.ParsePeersJson(sampleJson);
            Equal(4, peers.Count);

            Equal("LAPTOP-CLIENT", peers[0].Hostname);
            Equal("10.126.126.1", peers[0].VirtualIpv4);
            Equal("本机", peers[0].ConnectionType);
            Equal<double?>(null, peers[0].LatencyMs);
            Equal("", peers[0].Protocol);
            Equal(true, peers[0].IsSelf);

            Equal("DORM-DESKTOP", peers[1].Hostname);
            Equal("10.126.126.2", peers[1].VirtualIpv4);
            Equal("直连 (P2P)", peers[1].ConnectionType);
            Equal<double?>(8.42, peers[1].LatencyMs);
            Equal("udp", peers[1].Protocol);
            Equal(false, peers[1].IsSelf);

            Equal("LAB-WORKSTATION", peers[2].Hostname);
            Equal("10.126.126.3", peers[2].VirtualIpv4);
            Equal("中继 (Relay)", peers[2].ConnectionType);
            Equal<double?>(43.75, peers[2].LatencyMs);
            Equal("tcp", peers[2].Protocol);
            Equal(false, peers[2].IsSelf);

            Equal("UNKNOWN-NODE", peers[3].Hostname);
            Equal("", peers[3].VirtualIpv4);
            Equal("未知", peers[3].ConnectionType);
            Equal(false, peers[3].IsSelf);

            Equal("10.126.126.1", EasyTierManager.TryExtractLocalIpv4(peers));

            // Wrapped object with numeric cost and latency.
            const string wrappedJson = """
            {
              "peers": [
                { "ipv4": "10.10.0.1/24", "hostname": "SELF", "cost": 0, "latency_ms": 0, "protocol": "-" },
                { "ipv4": "10.10.0.2/24", "hostname": "PEER1", "cost": 1, "latency_ms": 5.5, "protocol": "wg" },
                { "ipv4": "10.10.0.3/24", "hostname": "PEER2", "cost": 2, "latency_ms": 31.2, "protocol": "tcp" }
              ]
            }
            """;
            var wrappedPeers = EasyTierManager.ParsePeersJson(wrappedJson);
            Equal(3, wrappedPeers.Count);
            Equal("本机", wrappedPeers[0].ConnectionType);
            Equal("直连 (P2P)", wrappedPeers[1].ConnectionType);
            Equal<double?>(5.5, wrappedPeers[1].LatencyMs);
            Equal("wg", wrappedPeers[1].Protocol);
            Equal("中继 (Relay)", wrappedPeers[2].ConnectionType);
            Equal("10.10.0.1", EasyTierManager.TryExtractLocalIpv4(wrappedPeers));

            // Malformed or empty JSON must return empty list without throwing.
            Equal(0, EasyTierManager.ParsePeersJson("").Count);
            Equal(0, EasyTierManager.ParsePeersJson("{broken").Count);
            Equal<string?>(null, EasyTierManager.TryExtractLocalIpv4([]));
        }),

        ("EngineCatalog promotes EasyTier out of Phase 2 placeholder and selects Windows x86_64 zip", () =>
        {
            var descriptor = EngineCatalog.Get(EngineKind.EasyTier);
            Equal(false, descriptor.IsPhase2Placeholder);
            Equal("跨网 P2P 组网", descriptor.RoleTag);
            Equal("easytier-core.exe", descriptor.ExecutableName);

            var primary = EngineCatalog.SelectBestAsset(
                descriptor,
                [
                    ("easytier-windows-arm64-v2.2.0.zip", "https://github.com/EasyTier/EasyTier/releases/download/v2.2.0/easytier-windows-arm64-v2.2.0.zip"),
                    ("easytier-gui-windows-x86_64-v2.2.0.zip", "https://github.com/EasyTier/EasyTier/releases/download/v2.2.0/easytier-gui-windows-x86_64-v2.2.0.zip"),
                    ("easytier-linux-x86_64-v2.2.0.zip", "https://github.com/EasyTier/EasyTier/releases/download/v2.2.0/easytier-linux-x86_64-v2.2.0.zip"),
                    ("easytier-windows-x86_64-v2.2.0.zip.sha256", "https://github.com/EasyTier/EasyTier/releases/download/v2.2.0/easytier-windows-x86_64-v2.2.0.zip.sha256"),
                    ("easytier-windows-x86_64-v2.2.0.zip", "https://github.com/EasyTier/EasyTier/releases/download/v2.2.0/easytier-windows-x86_64-v2.2.0.zip"),
                ]);
            Equal("easytier-windows-x86_64-v2.2.0.zip", primary?.Name);

            var fallback = EngineCatalog.SelectBestAsset(
                descriptor,
                [
                    ("easytier-android-v2.2.0.zip", "https://github.com/EasyTier/EasyTier/releases/download/v2.2.0/easytier-android-v2.2.0.zip"),
                    ("EasyTier-v2.2.0-windows-x86_64.zip", "https://github.com/EasyTier/EasyTier/releases/download/v2.2.0/EasyTier-v2.2.0-windows-x86_64.zip"),
                ]);
            Equal("EasyTier-v2.2.0-windows-x86_64.zip", fallback?.Name);
        }),

        ("SettingsStore round-trips EasyTier config, EasyTierPath, and UpdateRepository cleanly", () =>
        {
            var path = Path.Combine(tempRoot, "settings-easytier-" + Guid.NewGuid().ToString("N"), "settings.json");
            var store = new SettingsStore(path);

            var easyTierConfig = new EasyTierConfig
            {
                Enabled = true,
                NetworkName = "campus-p2p",
                NetworkSecret = "my-secret-key",
                VirtualIpv4 = "10.126.126.10/24",
                AutoAssignIpv4 = false,
                PeerUrl = "tcp://47.100.1.2:11010",
                EnableRelayFallback = true,
                AutoStartWithApp = true,
            };

            var saved = new AppSettings
            {
                EasyTierPath = @"C:\Tools\EasyTier\easytier-core.exe",
                EasyTier = easyTierConfig,
                UpdateRepository = "https://github.com/example/RemoteDesk",
            };
            store.Save(saved);

            var loaded = new SettingsStore(path).Load();
            Equal(@"C:\Tools\EasyTier\easytier-core.exe", loaded.EasyTierPath);
            Equal(easyTierConfig, loaded.EasyTier);
            Equal("https://github.com/example/RemoteDesk", loaded.UpdateRepository);

            // Disabled draft config also round-trips without error.
            var disabled = new AppSettings
            {
                EasyTier = new EasyTierConfig { Enabled = false },
            };
            store.Save(disabled);
            var loadedDisabled = new SettingsStore(path).Load();
            Equal(false, loadedDisabled.EasyTier?.Enabled);

            // Enabled invalid EasyTier config is rejected on save and load.
            Throws<ArgumentException>(() => store.Save(new AppSettings
            {
                EasyTier = new EasyTierConfig
                {
                    Enabled = true,
                    NetworkName = "--bad",
                    NetworkSecret = "secret",
                },
            }));

            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                easyTier = new
                {
                    enabled = true,
                    networkName = "ok",
                    networkSecret = "ok",
                    peerUrl = "http://invalid-scheme:11010",
                },
            }));
            Throws<ArgumentException>(() => new SettingsStore(path).Load());

            // Round-trip with FallbackPeerUrl preserved.
            var withFallback = easyTierConfig with { FallbackPeerUrl = "ws://47.100.1.2:11011" };
            store.Save(saved with { EasyTier = withFallback });
            var loadedWithFallback = new SettingsStore(path).Load();
            Equal(withFallback, loadedWithFallback.EasyTier);
        }),

        ("EasyTierConfig.Validate validates/deduplicates FallbackPeerUrl and CreateCoreStartInfo/CreateElevatedCoreStartInfo emit both -p entries", () =>
        {
            var withFallback = EasyTierConfig.Validate(new EasyTierConfig
            {
                Enabled = true,
                NetworkName = "campus-mesh_02",
                NetworkSecret = "Secr3t.Key-2026",
                AutoAssignIpv4 = true,
                PeerUrl = "tcp://8.137.208.131:11010",
                FallbackPeerUrl = "ws://8.137.208.131:11011",
            });
            Equal("ws://8.137.208.131:11011", withFallback.FallbackPeerUrl);
            Equal("tcp://8.137.208.131:11010|ws://8.137.208.131:11011", string.Join('|', withFallback.GetEffectivePeerUrls()));

            // Deduplicates when FallbackPeerUrl matches PeerUrl (case-insensitive).
            var deduped = EasyTierConfig.Validate(new EasyTierConfig
            {
                Enabled = true,
                NetworkName = "campus-mesh_02",
                NetworkSecret = "Secr3t.Key-2026",
                PeerUrl = "tcp://8.137.208.131:11010",
                FallbackPeerUrl = "TCP://8.137.208.131:11010",
            });
            Equal<string?>(null, deduped.FallbackPeerUrl);
            Equal("tcp://8.137.208.131:11010", string.Join('|', deduped.GetEffectivePeerUrls()));

            // Rejects invalid schemes, injection, and whitespace in FallbackPeerUrl.
            foreach (var badFallback in new[] { "http://1.2.3.4:11011", "ws://1.2.3.4:11011 --evil", " ws://1.2.3.4:11011", "ws://1.2.3.4:11011&calc" })
            {
                Throws<ArgumentException>(() => EasyTierConfig.Validate(new EasyTierConfig
                {
                    Enabled = true,
                    NetworkName = "campus-mesh_02",
                    NetworkSecret = "Secr3t.Key-2026",
                    PeerUrl = "tcp://8.137.208.131:11010",
                    FallbackPeerUrl = badFallback,
                }));
            }

            var root = Path.Combine(tempRoot, "easytier-fallback-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var coreExe = Path.Combine(root, "easytier-core.exe");
            File.WriteAllBytes(coreExe, []);
            var expectedHost = EasyTierManager.SanitizeHostname(Environment.MachineName);

            var startInfo = EasyTierManager.CreateCoreStartInfo(coreExe, withFallback);
            Equal(
                $"--network-name|campus-mesh_02|--network-secret|Secr3t.Key-2026|-d|-p|tcp://8.137.208.131:11010|-p|ws://8.137.208.131:11011|--hostname|{expectedHost}|--rpc-portal|{EasyTierManager.DefaultRpcPortal}",
                string.Join('|', startInfo.ArgumentList));

            var elevatedStart = EasyTierManager.CreateElevatedCoreStartInfo(coreExe, withFallback);
            Equal(true, elevatedStart.Arguments.Contains("-p tcp://8.137.208.131:11010 -p ws://8.137.208.131:11011", StringComparison.Ordinal));
        }),

        ("TargetHostResolver.BuildCandidates orders VirtualIpv4 vs LanIpv4 and filters loopback/APIPA", () =>
        {
            var device = new SyncedDeviceInfo(
                Id: "id-1",
                DeviceKey: "dev-dorm",
                Name: "DESKTOP-PB9FT5O",
                Hostname: "DESKTOP-PB9FT5O",
                VirtualIpv4: "10.126.126.3",
                LanIpv4: "10.244.155.185",
                LanIpv6: null,
                SunshineReady: true,
                SunshinePort: 47989,
                AgentPort: 47999,
                SecurityPinRequired: false,
                GpuEncoder: "NVENC",
                OsVersion: "Windows",
                LastSeenAtUtc: 0,
                IsOnline: true);

            // When peer is absent from EasyTier, LanIpv4 comes first, then VirtualIpv4.
            var absentCandidates = TargetHostResolver.BuildCandidates(device, [new EasyTierPeerInfo("LAPTOP", "10.126.126.2", "本机", null, "", IsSelf: true)]);
            Equal("10.244.155.185:True|10.126.126.3:False", string.Join('|', absentCandidates.Select(c => $"{c.Host}:{c.IsLanFallback}")));

            // When peer is present in EasyTier, VirtualIpv4 comes first, then LanIpv4.
            var presentCandidates = TargetHostResolver.BuildCandidates(device, [
                new EasyTierPeerInfo("LAPTOP", "10.126.126.2", "本机", null, "", IsSelf: true),
                new EasyTierPeerInfo("DESKTOP-PB9FT5O", "10.126.126.3", "直连 (P2P)", 3.2, "udp", IsSelf: false),
            ]);
            Equal("10.126.126.3:False|10.244.155.185:True", string.Join('|', presentCandidates.Select(c => $"{c.Host}:{c.IsLanFallback}")));

            // Filters loopback and APIPA addresses.
            var filtered = TargetHostResolver.BuildCandidates(device with { VirtualIpv4 = "127.0.0.1", LanIpv4 = "169.254.10.20" }, null);
            Equal(0, filtered.Count);
        }),

        ("TargetHostResolver.ResolveAsync handles LAN fallback, DeviceKey verification, VirtualIpv4 preference, and mesh diagnostics", () =>
        {
            var device = new SyncedDeviceInfo(
                Id: "id-1",
                DeviceKey: "dev-dorm-key",
                Name: "DESKTOP-PB9FT5O",
                Hostname: "DESKTOP-PB9FT5O",
                VirtualIpv4: "10.126.126.3",
                LanIpv4: "10.244.155.185",
                LanIpv6: null,
                SunshineReady: true,
                SunshinePort: 47989,
                AgentPort: 47999,
                SecurityPinRequired: false,
                GpuEncoder: "NVENC",
                OsVersion: "Windows",
                LastSeenAtUtc: 0,
                IsOnline: true);

            // 1. VirtualIpv4 fails (or peer absent), LanIpv4 succeeds with matching DeviceKey.
            using (var client1 = new AgentPeerClient(new StubPeerHandler(host =>
            {
                if (host == "10.244.155.185")
                    return (HttpStatusCode.OK, JsonSerializer.Serialize(new AgentPeerInfoDto("dev-dorm-key", "DESKTOP-PB9FT5O", true, 47989, false)));
                throw new HttpRequestException("unreachable");
            })))
            {
                var res = TargetHostResolver.ResolveAsync(device, [], 47999, "auth-key", client1).GetAwaiter().GetResult();
                Equal(true, res.Succeeded);
                Equal("10.244.155.185", res.ResolvedHost);
                Equal(true, res.UsedLanFallback);
                Equal("10.244.155.185", string.Join('|', res.TriedHosts));
            }

            // 2. VirtualIpv4 is in EasyTier peers, VirtualIpv4 times out/fails, falls back to LanIpv4.
            var peersWithDorm = new[] { new EasyTierPeerInfo("DESKTOP-PB9FT5O", "10.126.126.3", "中继 (Relay)", 25.0, "tcp", IsSelf: false) };
            using (var client2 = new AgentPeerClient(new StubPeerHandler(host =>
            {
                if (host == "10.126.126.3")
                    throw new HttpRequestException("virtual timeout");
                return (HttpStatusCode.OK, JsonSerializer.Serialize(new AgentPeerInfoDto("dev-dorm-key", "DESKTOP-PB9FT5O", true, 47989, false)));
            })))
            {
                var res = TargetHostResolver.ResolveAsync(device, peersWithDorm, 47999, "auth-key", client2).GetAwaiter().GetResult();
                Equal(true, res.Succeeded);
                Equal("10.244.155.185", res.ResolvedHost);
                Equal(true, res.UsedLanFallback);
                Equal("10.126.126.3|10.244.155.185", string.Join('|', res.TriedHosts));
            }

            // 3. LanIpv4 returns a mismatched DeviceKey -> must reject LanIpv4!
            using (var client3 = new AgentPeerClient(new StubPeerHandler(host =>
            {
                if (host == "10.244.155.185")
                    return (HttpStatusCode.OK, JsonSerializer.Serialize(new AgentPeerInfoDto("other-pc-key", "WRONG-PC", true, 47989, false)));
                throw new HttpRequestException("virtual unreachable");
            })))
            {
                var res = TargetHostResolver.ResolveAsync(device, [], 47999, "auth-key", client3).GetAwaiter().GetResult();
                Equal(false, res.Succeeded);
                Equal<string?>(null, res.ResolvedHost);
                Equal(true, res.FailureMessage!.Contains("11010", StringComparison.Ordinal));
            }

            // 4. VirtualIpv4 is reachable in EasyTier peers and succeeds -> prefers VirtualIpv4 without using LAN fallback.
            using (var client4 = new AgentPeerClient(new StubPeerHandler(host =>
            {
                if (host == "10.126.126.3")
                    return (HttpStatusCode.OK, JsonSerializer.Serialize(new AgentPeerInfoDto("dev-dorm-key", "DESKTOP-PB9FT5O", true, 47989, false)));
                throw new InvalidOperationException("Should not probe LAN when VirtualIpv4 succeeded");
            })))
            {
                var res = TargetHostResolver.ResolveAsync(device, peersWithDorm, 47999, "auth-key", client4).GetAwaiter().GetResult();
                Equal(true, res.Succeeded);
                Equal("10.126.126.3", res.ResolvedHost);
                Equal(false, res.UsedLanFallback);
            }
        }),
    ];

    private sealed class StubPeerHandler(Func<string, (HttpStatusCode Status, string Json)> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri?.Host ?? "";
            var (status, json) = responder(host);
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
