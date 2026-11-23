using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace RemoteDesk;

internal static class EngineChecks
{
    public static IReadOnlyList<(string Name, Action Run)> CreateTests(string tempRoot) =>
    [
        ("EngineCatalog.ApplyMirror handles valid, invalid, and non-GitHub URLs safely", () =>
        {
            const string ghUrl = "https://github.com/moonlight-stream/moonlight-qt/releases/download/v6.1.0/MoonlightPortable-x64-6.1.0.zip";
            Equal("https://ghfast.top/" + ghUrl, EngineCatalog.ApplyMirror(ghUrl, "https://ghfast.top"));
            Equal("https://ghfast.top/" + ghUrl, EngineCatalog.ApplyMirror(ghUrl, "https://ghfast.top/"));
            Equal("https://mirror.example.com/proxy/" + ghUrl, EngineCatalog.ApplyMirror(ghUrl, "  https://mirror.example.com/proxy  "));

            // Blank or invalid prefixes must leave the URL untouched.
            Equal(ghUrl, EngineCatalog.ApplyMirror(ghUrl, null));
            Equal(ghUrl, EngineCatalog.ApplyMirror(ghUrl, "   "));
            Equal(ghUrl, EngineCatalog.ApplyMirror(ghUrl, "http://insecure.example.com"));
            Equal(ghUrl, EngineCatalog.ApplyMirror(ghUrl, "not-a-url"));
            Equal(ghUrl, EngineCatalog.ApplyMirror(ghUrl, "https://user:pass@mirror.example.com"));
            Equal(ghUrl, EngineCatalog.ApplyMirror(ghUrl, "https://mirror.example.com/?x=1"));
            Equal(ghUrl, EngineCatalog.ApplyMirror(ghUrl, "https://github.com/"));

            // Non-GitHub or non-HTTPS URLs must remain untouched.
            const string nonGhUrl = "https://example.com/files/engine.zip";
            const string spoofGhUrl = "https://github.com.evil.example/a/b.zip";
            const string apiUrl = "https://api.github.com/repos/rustdesk/rustdesk/releases/latest";
            const string httpGhUrl = "http://github.com/rustdesk/rustdesk/releases/download/1.0/rustdesk.zip";
            Equal(nonGhUrl, EngineCatalog.ApplyMirror(nonGhUrl, "https://ghfast.top"));
            Equal(spoofGhUrl, EngineCatalog.ApplyMirror(spoofGhUrl, "https://ghfast.top"));
            Equal(apiUrl, EngineCatalog.ApplyMirror(apiUrl, "https://ghfast.top"));
            Equal(httpGhUrl, EngineCatalog.ApplyMirror(httpGhUrl, "https://ghfast.top"));
        }),

        ("EngineCatalog.SelectBestAsset picks portable x64 assets for Moonlight, Sunshine, and RustDesk", () =>
        {
            var moonlight = EngineCatalog.SelectBestAsset(
                EngineCatalog.Get(EngineKind.Moonlight),
                [
                    ("MoonlightSetup-6.1.0.exe", "https://github.com/moonlight-stream/moonlight-qt/releases/download/v6.1.0/MoonlightSetup-6.1.0.exe"),
                    ("MoonlightPortable-ARM64-6.1.0.zip", "https://github.com/moonlight-stream/moonlight-qt/releases/download/v6.1.0/MoonlightPortable-ARM64-6.1.0.zip"),
                    ("MoonlightPortable-x64-6.1.0-symbols.zip", "https://github.com/moonlight-stream/moonlight-qt/releases/download/v6.1.0/MoonlightPortable-x64-6.1.0-symbols.zip"),
                    ("MoonlightPortable-x64-6.1.0.zip", "https://github.com/moonlight-stream/moonlight-qt/releases/download/v6.1.0/MoonlightPortable-x64-6.1.0.zip"),
                ]);
            Equal("MoonlightPortable-x64-6.1.0.zip", moonlight?.Name);

            var sunshinePrimary = EngineCatalog.SelectBestAsset(
                EngineCatalog.Get(EngineKind.Sunshine),
                [
                    ("sunshine-windows-installer.exe", "https://github.com/LizardByte/Sunshine/releases/download/v0.23.1/sunshine-windows-installer.exe"),
                    ("sunshine-windows-portable-arm64.zip", "https://github.com/LizardByte/Sunshine/releases/download/v0.23.1/sunshine-windows-portable-arm64.zip"),
                    ("sunshine-windows-debug.zip", "https://github.com/LizardByte/Sunshine/releases/download/v0.23.1/sunshine-windows-debug.zip"),
                    ("sunshine-windows-portable.zip", "https://github.com/LizardByte/Sunshine/releases/download/v0.23.1/sunshine-windows-portable.zip"),
                ]);
            Equal("sunshine-windows-portable.zip", sunshinePrimary?.Name);

            var sunshineFallback = EngineCatalog.SelectBestAsset(
                EngineCatalog.Get(EngineKind.Sunshine),
                [
                    ("sunshine-windows-installer.exe", "https://github.com/LizardByte/Sunshine/releases/download/v0.23.1/sunshine-windows-installer.exe"),
                    ("Sunshine-Windows-2025-Portable.zip", "https://github.com/LizardByte/Sunshine/releases/download/v0.23.1/Sunshine-Windows-2025-Portable.zip"),
                ]);
            Equal("Sunshine-Windows-2025-Portable.zip", sunshineFallback?.Name);

            var rustDeskZip = EngineCatalog.SelectBestAsset(
                EngineCatalog.Get(EngineKind.RustDesk),
                [
                    ("rustdesk-1.3.2-aarch64.exe", "https://github.com/rustdesk/rustdesk/releases/download/1.3.2/rustdesk-1.3.2-aarch64.exe"),
                    ("rustdesk-1.3.2-x86_64-sciter.exe", "https://github.com/rustdesk/rustdesk/releases/download/1.3.2/rustdesk-1.3.2-x86_64-sciter.exe"),
                    ("rustdesk-1.3.2-x86_64.msi", "https://github.com/rustdesk/rustdesk/releases/download/1.3.2/rustdesk-1.3.2-x86_64.msi"),
                    ("rustdesk-1.3.2-x86_64.exe.sha256", "https://github.com/rustdesk/rustdesk/releases/download/1.3.2/rustdesk-1.3.2-x86_64.exe.sha256"),
                    ("rustdesk-1.3.2-x86_64.exe", "https://github.com/rustdesk/rustdesk/releases/download/1.3.2/rustdesk-1.3.2-x86_64.exe"),
                    ("rustdesk-1.3.2-x86_64.zip", "https://github.com/rustdesk/rustdesk/releases/download/1.3.2/rustdesk-1.3.2-x86_64.zip"),
                ]);
            Equal("rustdesk-1.3.2-x86_64.zip", rustDeskZip?.Name);

            var rustDeskExeFallback = EngineCatalog.SelectBestAsset(
                EngineCatalog.Get(EngineKind.RustDesk),
                [
                    ("rustdesk-1.3.2-x86_64-sciter.exe", "https://github.com/rustdesk/rustdesk/releases/download/1.3.2/rustdesk-1.3.2-x86_64-sciter.exe"),
                    ("rustdesk-1.3.2-x86_64.exe", "https://github.com/rustdesk/rustdesk/releases/download/1.3.2/rustdesk-1.3.2-x86_64.exe"),
                ]);
            Equal("rustdesk-1.3.2-x86_64.exe", rustDeskExeFallback?.Name);

            var none = EngineCatalog.SelectBestAsset(
                EngineCatalog.Get(EngineKind.Moonlight),
                [("MoonlightPortable-x64-6.1.0.zip", "http://github.com/insecure.zip")]);
            Equal(true, none is null);
        }),

        ("EngineManager.InstallFromLocalFileAsync imports nested zip and direct exe", () =>
        {
            var root = Path.Combine(tempRoot, "engine-local-" + Guid.NewGuid().ToString("N"));
            var manager = new EngineManager(Path.Combine(root, "engines"));

            // Nested zip (depth 2) for Moonlight
            var zipPath = Path.Combine(root, "MoonlightPortable-x64.zip");
            CreateZip(zipPath,
                ("MoonlightPortable-x64/bin/Moonlight.exe", "MZ-MOONLIGHT"u8.ToArray()),
                ("MoonlightPortable-x64/README.txt", "ok"u8.ToArray()));

            var reported = new List<EngineInstallProgress>();
            var progress = new SyncProgress<EngineInstallProgress>(reported.Add);

            var status = manager.InstallFromLocalFileAsync(EngineKind.Moonlight, zipPath, progress, CancellationToken.None)
                .GetAwaiter().GetResult();

            Equal(EngineInstallState.ManagedInstalled, status.State);
            Equal(true, status.ExecutablePath is not null && File.Exists(status.ExecutablePath));
            Equal("Moonlight.exe", Path.GetFileName(status.ExecutablePath));
            Equal("local", status.InstalledVersion);
            Equal(true, reported.Count > 0 && reported[^1].Percentage == 100);
            Equal(true, File.Exists(Path.Combine(manager.GetEngineDirectory(EngineKind.Moonlight), "engine-meta.json")));

            // Direct exe (renamed portable release filename) for RustDesk
            var exePath = Path.Combine(root, "rustdesk-1.3.2-x86_64.exe");
            File.WriteAllBytes(exePath, "MZ-RUSTDESK"u8.ToArray());

            var rustDeskStatus = manager.InstallFromLocalFileAsync(EngineKind.RustDesk, exePath, null, CancellationToken.None)
                .GetAwaiter().GetResult();

            Equal(EngineInstallState.ManagedInstalled, rustDeskStatus.State);
            Equal(true, rustDeskStatus.ExecutablePath is not null && File.Exists(rustDeskStatus.ExecutablePath));
            Equal("rustdesk.exe", Path.GetFileName(rustDeskStatus.ExecutablePath));

            // Zip missing expected executable must fail without corrupting state
            var badZip = Path.Combine(root, "no-exe.zip");
            CreateZip(badZip, ("docs/readme.txt", "hello"u8.ToArray()));
            Throws<InvalidDataException>(() =>
                manager.InstallFromLocalFileAsync(EngineKind.Sunshine, badZip, null, CancellationToken.None)
                    .GetAwaiter().GetResult());
            Equal(EngineInstallState.NotInstalled, manager.Inspect(EngineKind.Sunshine).State);

            // Failed reinstall over existing managed Moonlight must preserve the existing installation
            Throws<InvalidDataException>(() =>
                manager.InstallFromLocalFileAsync(EngineKind.Moonlight, badZip, null, CancellationToken.None)
                    .GetAwaiter().GetResult());
            Equal(EngineInstallState.ManagedInstalled, manager.Inspect(EngineKind.Moonlight).State);

            // Atomic replacement updates existing managed Moonlight cleanly
            var upgradedZip = Path.Combine(root, "MoonlightPortable-v2.zip");
            CreateZip(upgradedZip, ("Moonlight.exe", "MZ-MOONLIGHT-V2"u8.ToArray()));
            var upgraded = manager.InstallFromLocalFileAsync(EngineKind.Moonlight, upgradedZip, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            Equal(EngineInstallState.ManagedInstalled, upgraded.State);
            Equal("MZ-MOONLIGHT-V2", File.ReadAllText(upgraded.ExecutablePath!));
        }),

        ("EngineManager rejects Zip Slip path traversal archives", () =>
        {
            var root = Path.Combine(tempRoot, "engine-zipslip-" + Guid.NewGuid().ToString("N"));
            var enginesRoot = Path.Combine(root, "engines");
            var manager = new EngineManager(enginesRoot);

            var traversalZip = Path.Combine(root, "zipslip.zip");
            CreateZip(traversalZip,
                ("../../evil.txt", "pwned"u8.ToArray()),
                ("Moonlight.exe", "MZ"u8.ToArray()));

            Throws<InvalidDataException>(() =>
                manager.InstallFromLocalFileAsync(EngineKind.Moonlight, traversalZip, null, CancellationToken.None)
                    .GetAwaiter().GetResult());

            Equal(false, File.Exists(Path.Combine(root, "evil.txt")));
            Equal(false, File.Exists(Path.Combine(enginesRoot, "evil.txt")));
            Equal(EngineInstallState.NotInstalled, manager.Inspect(EngineKind.Moonlight).State);

            var nestedTraversalZip = Path.Combine(root, "zipslip-nested.zip");
            CreateZip(nestedTraversalZip,
                (@"sub\..\..\..\evil2.txt", "pwned"u8.ToArray()),
                ("sunshine.exe", "MZ"u8.ToArray()));

            Throws<InvalidDataException>(() =>
                manager.InstallFromLocalFileAsync(EngineKind.Sunshine, nestedTraversalZip, null, CancellationToken.None)
                    .GetAwaiter().GetResult());
            Equal(EngineInstallState.NotInstalled, manager.Inspect(EngineKind.Sunshine).State);
        }),

        ("EngineManager.Inspect tracks NotInstalled -> SystemInstalled -> ManagedInstalled -> Uninstall -> NotInstalled", () =>
        {
            var root = Path.Combine(tempRoot, "engine-states-" + Guid.NewGuid().ToString("N"));
            var manager = new EngineManager(Path.Combine(root, "engines"));

            // 1. NotInstalled
            var s0 = manager.Inspect(EngineKind.Sunshine);
            Equal(EngineInstallState.NotInstalled, s0.State);
            Equal<string?>(null, s0.ExecutablePath);

            // 2. SystemInstalled when fallback path exists
            var systemExe = Path.Combine(root, "ProgramFiles", "Sunshine", "sunshine.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(systemExe)!);
            File.WriteAllBytes(systemExe, "MZ-SYS"u8.ToArray());

            var s1 = manager.Inspect(EngineKind.Sunshine, systemExe);
            Equal(EngineInstallState.SystemInstalled, s1.State);
            Equal(systemExe, s1.ExecutablePath);

            // 3. ManagedInstalled takes precedence over fallback system path
            var zipPath = Path.Combine(root, "sunshine-portable.zip");
            CreateZip(zipPath, ("sunshine/sunshine.exe", "MZ-MGD"u8.ToArray()));
            manager.InstallFromLocalFileAsync(EngineKind.Sunshine, zipPath, null, CancellationToken.None)
                .GetAwaiter().GetResult();

            var s2 = manager.Inspect(EngineKind.Sunshine, systemExe);
            Equal(EngineInstallState.ManagedInstalled, s2.State);
            Equal(false, string.Equals(systemExe, s2.ExecutablePath, StringComparison.OrdinalIgnoreCase));
            Equal("local", s2.InstalledVersion);

            // 4. UninstallManaged returns to NotInstalled (and SystemInstalled when fallback is provided)
            manager.UninstallManaged(EngineKind.Sunshine);
            Equal(false, Directory.Exists(manager.GetEngineDirectory(EngineKind.Sunshine)));
            Equal(EngineInstallState.NotInstalled, manager.Inspect(EngineKind.Sunshine).State);
            Equal(EngineInstallState.SystemInstalled, manager.Inspect(EngineKind.Sunshine, systemExe).State);
        }),

        ("EngineManager manages EasyTier as a live Phase 2 engine", () =>
        {
            var root = Path.Combine(tempRoot, "engine-phase2-" + Guid.NewGuid().ToString("N"));
            var manager = new EngineManager(Path.Combine(root, "engines"));

            var dummyExe = Path.Combine(root, "easytier-core.exe");
            Directory.CreateDirectory(root);
            File.WriteAllBytes(dummyExe, "MZ"u8.ToArray());

            var systemStatus = manager.Inspect(EngineKind.EasyTier, dummyExe);
            Equal(EngineInstallState.SystemInstalled, systemStatus.State);
            Equal(false, systemStatus.Descriptor.IsPhase2Placeholder);
            Equal(dummyExe, systemStatus.ExecutablePath);

            var managedStatus = manager.InstallFromLocalFileAsync(EngineKind.EasyTier, dummyExe, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            Equal(EngineInstallState.ManagedInstalled, managedStatus.State);
            Equal("easytier-core.exe", Path.GetFileName(managedStatus.ExecutablePath));
        }),

        ("EngineManager.InstallFromGitHubAsync uses User-Agent, mirror fallback, and writes engine-meta.json", () =>
        {
            var root = Path.Combine(tempRoot, "engine-gh-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            var payloadZipPath = Path.Combine(root, "payload.zip");
            CreateZip(payloadZipPath, ("MoonlightPortable/Moonlight.exe", "MZ-GH"u8.ToArray()));
            var payloadBytes = File.ReadAllBytes(payloadZipPath);

            const string directAssetUrl = "https://github.com/moonlight-stream/moonlight-qt/releases/download/v6.1.0/MoonlightPortable-x64-6.1.0.zip";
            const string mirrorPrefix = "https://mirror.example.test";
            const string mirroredAssetUrl = mirrorPrefix + "/" + directAssetUrl;

            var requestedUrls = new List<string>();
            string? capturedUserAgent = null;

            using var handler = new StubHttpMessageHandler(request =>
            {
                var uri = request.RequestUri!.AbsoluteUri;
                requestedUrls.Add(uri);
                capturedUserAgent ??= request.Headers.UserAgent.ToString();

                if (uri == EngineCatalog.Get(EngineKind.Moonlight).ReleasesApiUrl)
                {
                    var releaseJson = JsonSerializer.Serialize(new
                    {
                        tag_name = "v6.1.0",
                        assets = new[]
                        {
                            new { name = "MoonlightSetup-6.1.0.exe", browser_download_url = "https://github.com/moonlight-stream/moonlight-qt/releases/download/v6.1.0/MoonlightSetup-6.1.0.exe" },
                            new { name = "MoonlightPortable-x64-6.1.0.zip", browser_download_url = directAssetUrl },
                        },
                    });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(releaseJson, Encoding.UTF8, "application/json"),
                    };
                }

                if (uri == mirroredAssetUrl)
                    return new HttpResponseMessage(HttpStatusCode.BadGateway);

                if (uri == directAssetUrl)
                {
                    var content = new ByteArrayContent(payloadBytes);
                    content.Headers.ContentLength = payloadBytes.Length;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            using var httpClient = new HttpClient(handler);
            var manager = new EngineManager(Path.Combine(root, "engines"), httpClient);
            var updates = new List<EngineInstallProgress>();

            var installed = manager.InstallFromGitHubAsync(
                EngineKind.Moonlight,
                mirrorPrefix,
                new SyncProgress<EngineInstallProgress>(updates.Add),
                CancellationToken.None).GetAwaiter().GetResult();

            Equal(EngineInstallState.ManagedInstalled, installed.State);
            Equal("v6.1.0", installed.InstalledVersion);
            Equal(true, capturedUserAgent is not null && capturedUserAgent.Contains("RemoteDesk", StringComparison.Ordinal));
            Equal(3, requestedUrls.Count);
            Equal(mirroredAssetUrl, requestedUrls[1]);
            Equal(directAssetUrl, requestedUrls[2]);
            Equal(true, updates.Any(u => u.Percentage is > 0 and <= 80));
            Equal(true, updates[^1].Percentage == 100);

            var metaPath = Path.Combine(manager.GetEngineDirectory(EngineKind.Moonlight), "engine-meta.json");
            using var metaDoc = JsonDocument.Parse(File.ReadAllText(metaPath));
            Equal("v6.1.0", metaDoc.RootElement.GetProperty("version").GetString());
            Equal(directAssetUrl, metaDoc.RootElement.GetProperty("sourceUrl").GetString());
            Equal(true, metaDoc.RootElement.TryGetProperty("installedAtUtc", out _));
        }),
    ];

    private static void CreateZip(string zipPath, params (string EntryName, byte[] Content)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (entryName, content) in entries)
        {
            var entry = archive.CreateEntry(entryName);
            using var stream = entry.Open();
            stream.Write(content);
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

    private sealed class SyncProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
