using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace RemoteDesk;

internal static class UpdateChecks
{
    public static IReadOnlyList<(string Name, Action Run)> CreateTests(string tempRoot) =>
    [
        ("AppUpdateService.NormalizeRepository normalizes blank, owner/repo, and GitHub URLs and rejects invalid inputs", () =>
        {
            Equal(AppUpdateService.DefaultRepository, AppUpdateService.NormalizeRepository(null));
            Equal(AppUpdateService.DefaultRepository, AppUpdateService.NormalizeRepository(""));
            Equal(AppUpdateService.DefaultRepository, AppUpdateService.NormalizeRepository("   "));

            Equal("zhengbaiyang/remote-desk", AppUpdateService.NormalizeRepository("zhengbaiyang/remote-desk"));
            Equal("My_Org.1/Remote-Desk_Fork", AppUpdateService.NormalizeRepository("  My_Org.1/Remote-Desk_Fork  "));
            Equal("zhengbaiyang/remote-desk", AppUpdateService.NormalizeRepository("https://github.com/zhengbaiyang/remote-desk"));
            Equal("zhengbaiyang/remote-desk", AppUpdateService.NormalizeRepository("https://github.com/zhengbaiyang/remote-desk/"));
            Equal("zhengbaiyang/remote-desk", AppUpdateService.NormalizeRepository("https://github.com/zhengbaiyang/remote-desk.git"));
            Equal("zhengbaiyang/remote-desk", AppUpdateService.NormalizeRepository("https://github.com/zhengbaiyang/remote-desk.git/"));
            Equal("MyOrg/MyRepo", AppUpdateService.NormalizeRepository("HTTPS://GITHUB.COM/MyOrg/MyRepo.GIT"));

            string[] badRepos =
            [
                "only-owner",
                "owner/",
                "/repo",
                "owner/.git",
                "owner/repo/extra",
                "../evil/repo",
                "owner/..",
                "owner/.",
                "owner/repo..bad",
                "https://github.com/owner/repo/../other",
                "https://github.com/owner/..",
                "http://github.com/zhengbaiyang/remote-desk",
                "https://evil.example/zhengbaiyang/remote-desk",
                "https://user:pass@github.com/zhengbaiyang/remote-desk",
                "https://github.com/zhengbaiyang/remote-desk?ref=main",
                "https://github.com/zhengbaiyang/remote-desk#readme",
                "owner/repo with space",
                "owner/repo\n",
                "owner/repo\0",
                "owner/repo;calc",
                "owner\\repo",
                "owner/repo$1",
            ];

            foreach (var bad in badRepos)
            {
                Throws<ArgumentException>(() => AppUpdateService.NormalizeRepository(bad));
            }
        }),

        ("AppUpdateService.TryParseSemVersion and IsNewerVersion compare release tags accurately", () =>
        {
            Equal(true, AppUpdateService.TryParseSemVersion("v0.6.0", out var v060));
            Equal(new Version(0, 6, 0), v060);

            Equal(true, AppUpdateService.TryParseSemVersion("0.6.0", out var plain060));
            Equal(new Version(0, 6, 0), plain060);

            Equal(true, AppUpdateService.TryParseSemVersion("V1.2.3-beta.1", out var v123));
            Equal(new Version(1, 2, 3), v123);

            Equal(true, AppUpdateService.TryParseSemVersion("1.2", out var v120));
            Equal(new Version(1, 2, 0), v120);

            Equal(true, AppUpdateService.TryParseSemVersion("1.2.3.4+build.9", out var v1234));
            Equal(new Version(1, 2, 3, 4), v1234);

            Equal(false, AppUpdateService.TryParseSemVersion(null, out _));
            Equal(false, AppUpdateService.TryParseSemVersion("", out _));
            Equal(false, AppUpdateService.TryParseSemVersion("v", out _));
            Equal(false, AppUpdateService.TryParseSemVersion("1", out _));
            Equal(false, AppUpdateService.TryParseSemVersion("1.2.3.4.5", out _));
            Equal(false, AppUpdateService.TryParseSemVersion("vv0.6.0", out _));
            Equal(false, AppUpdateService.TryParseSemVersion("v0.6.0-", out _));
            Equal(false, AppUpdateService.TryParseSemVersion("0.6.x", out _));

            Equal(true, AppUpdateService.IsNewerVersion("0.5.0", "v0.6.0"));
            Equal(false, AppUpdateService.IsNewerVersion("0.6.0", "v0.6.0"));
            Equal(false, AppUpdateService.IsNewerVersion("0.6.0.0", "v0.6.0"));
            Equal(false, AppUpdateService.IsNewerVersion("0.6.0", "v0.5.9"));
            Equal(true, AppUpdateService.IsNewerVersion("0.5.0", "v0.6.0-rc.1"));
            Equal(false, AppUpdateService.IsNewerVersion("1.2", "v1.2.0"));
            Equal(true, AppUpdateService.IsNewerVersion("1.2", "v1.2.1"));
            Equal(false, AppUpdateService.IsNewerVersion("invalid", "v0.6.0"));
            Equal(false, AppUpdateService.IsNewerVersion("0.5.0", "invalid"));
        }),

        ("AppUpdateService URL builders handle default/custom repositories and mirrors", () =>
        {
            Equal(
                "https://github.com/zhengbaiyang/remote-desk/releases/latest/download/",
                AppUpdateService.BuildVelopackFeedUrl(null));
            Equal(
                "https://ghfast.top/https://github.com/zhengbaiyang/remote-desk/releases/latest/download/",
                AppUpdateService.BuildVelopackFeedUrl("", "https://ghfast.top"));
            Equal(
                "https://ghfast.top/https://github.com/myorg/myfork/releases/latest/download/",
                AppUpdateService.BuildVelopackFeedUrl("https://github.com/myorg/myfork.git", "https://ghfast.top/"));
            Equal(
                "https://github.com/myorg/myfork/releases/latest/download/",
                AppUpdateService.BuildVelopackFeedUrl("myorg/myfork", "http://insecure.example.com"));

            Equal(
                "https://api.github.com/repos/myorg/myfork/releases/latest",
                AppUpdateService.BuildLatestReleaseApiUrl("https://github.com/myorg/myfork.git"));
            Equal(
                "https://github.com/myorg/myfork/releases/latest",
                AppUpdateService.BuildReleasesPageUrl("myorg/myfork"));
        }),

        ("AppUpdateService.ParseLatestReleaseJson selects preferred installer, detects Velopack support, and enforces HTTPS", () =>
        {
            const string setupUrl = "https://github.com/zhengbaiyang/remote-desk/releases/download/v0.6.0/RemoteDesk.Windows-win-Setup.exe";
            var releaseJson = JsonSerializer.Serialize(new
            {
                tag_name = "v0.6.0",
                name = "RemoteDesk v0.6.0",
                body = "新增 EasyTier P2P 跨网直连与自动更新支持。",
                html_url = "https://github.com/zhengbaiyang/remote-desk/releases/tag/v0.6.0",
                assets = new object[]
                {
                    new { name = "RemoteDesk.Windows-win-Setup.exe.sha256", browser_download_url = setupUrl + ".sha256", size = 64L },
                    new { name = "Other-Setup.exe", browser_download_url = "https://github.com/zhengbaiyang/remote-desk/releases/download/v0.6.0/Other-Setup.exe", size = 100L },
                    new { name = "RemoteDesk.Windows-win-Setup.exe", browser_download_url = setupUrl, size = 25_000_000L },
                    new { name = "RemoteDesk.Windows-0.6.0-full.nupkg", browser_download_url = "https://github.com/zhengbaiyang/remote-desk/releases/download/v0.6.0/RemoteDesk.Windows-0.6.0-full.nupkg", size = 24_000_000L },
                    new { name = "releases.win.json", browser_download_url = "https://github.com/zhengbaiyang/remote-desk/releases/download/v0.6.0/releases.win.json", size = 512L },
                },
            });

            var result = AppUpdateService.ParseLatestReleaseJson(releaseJson, "0.5.0", AppUpdateService.DefaultRepository);
            Equal(true, result.IsUpdateAvailable);
            Equal("0.5.0", result.CurrentVersion);
            Equal("0.6.0", result.LatestVersion);
            Equal("v0.6.0", result.TagName);
            Equal("RemoteDesk v0.6.0", result.ReleaseName);
            Equal("新增 EasyTier P2P 跨网直连与自动更新支持。", result.ReleaseNotes);
            Equal("https://github.com/zhengbaiyang/remote-desk/releases/tag/v0.6.0", result.ReleasePageUrl);
            Equal(true, result.SupportsInPlaceVelopack);
            Equal(new ReleaseAssetInfo("RemoteDesk.Windows-win-Setup.exe", setupUrl, 25_000_000L), result.SetupAsset);

            var upToDate = AppUpdateService.ParseLatestReleaseJson(releaseJson, "0.6.0", AppUpdateService.DefaultRepository);
            Equal(false, upToDate.IsUpdateAvailable);

            // Fallback to *Setup*.exe, reject http:// asset, and SupportsInPlaceVelopack = false when missing nupkg.
            var longNotes = string.Join("\n", Enumerable.Repeat("更新说明条目", 900));
            var fallbackJson = JsonSerializer.Serialize(new
            {
                tag_name = "v0.7.0",
                name = "",
                body = longNotes,
                html_url = "http://insecure.example/release",
                assets = new object[]
                {
                    new { name = "RemoteDesk.Windows-win-Setup.exe", browser_download_url = "http://github.com/insecure.exe", size = 100L },
                    new { name = "RemoteDesk-0.7.0-Setup.exe.sig", browser_download_url = "https://github.com/myorg/myfork/releases/download/v0.7.0/RemoteDesk-0.7.0-Setup.exe.sig", size = 128L },
                    new { name = "helper.exe", browser_download_url = "https://github.com/myorg/myfork/releases/download/v0.7.0/helper.exe", size = 500L },
                    new { name = "Custom-Setup-x64.exe", browser_download_url = "https://github.com/myorg/myfork/releases/download/v0.7.0/Custom-Setup-x64.exe", size = 12_345L },
                    new { name = "releases.win.json", browser_download_url = "https://github.com/myorg/myfork/releases/download/v0.7.0/releases.win.json", size = 256L },
                },
            });

            var fallback = AppUpdateService.ParseLatestReleaseJson(fallbackJson, "0.6.0", "myorg/myfork");
            Equal(true, fallback.IsUpdateAvailable);
            Equal("v0.7.0", fallback.ReleaseName);
            Equal("https://github.com/myorg/myfork/releases/latest", fallback.ReleasePageUrl);
            Equal("Custom-Setup-x64.exe", fallback.SetupAsset?.Name);
            Equal(false, fallback.SupportsInPlaceVelopack);
            Equal(true, fallback.ReleaseNotes.Length <= 4010 && fallback.ReleaseNotes.EndsWith("…", StringComparison.Ordinal));
        }),

        ("AppUpdateService.CreateInstallerLaunch validates existing fully-qualified .exe files", () =>
        {
            var dir = Path.Combine(tempRoot, "update-launch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            var validExe = Path.Combine(dir, "RemoteDesk.Windows-win-Setup.exe");
            File.WriteAllBytes(validExe, "MZ-SETUP"u8.ToArray());

            var startInfo = AppUpdateService.CreateInstallerLaunch(validExe);
            Equal(validExe, startInfo.FileName);
            Equal(true, startInfo.UseShellExecute);

            var textFile = Path.Combine(dir, "notes.txt");
            File.WriteAllText(textFile, "not an exe");

            Throws<ArgumentException>(() => AppUpdateService.CreateInstallerLaunch(""));
            Throws<ArgumentException>(() => AppUpdateService.CreateInstallerLaunch("   "));
            Throws<ArgumentException>(() => AppUpdateService.CreateInstallerLaunch("RemoteDesk-Setup.exe"));
            Throws<ArgumentException>(() => AppUpdateService.CreateInstallerLaunch(textFile));
            Throws<FileNotFoundException>(() => AppUpdateService.CreateInstallerLaunch(Path.Combine(dir, "missing-Setup.exe")));
        }),

        ("AppUpdateService checks latest release and downloads installer with mirror fallback, sanitization, and atomic move", () =>
        {
            var downloadDir = Path.Combine(tempRoot, "update-download-" + Guid.NewGuid().ToString("N"));
            const string directAssetUrl = "https://github.com/zhengbaiyang/remote-desk/releases/download/v0.6.0/RemoteDesk.Windows-win-Setup.exe";
            const string mirrorPrefix = "https://mirror.example.test";
            const string mirroredAssetUrl = mirrorPrefix + "/" + directAssetUrl;
            var payload = "MZ-REMOTEDESK-INSTALLER-PAYLOAD"u8.ToArray();

            var requestedUrls = new List<string>();
            string? apiUserAgent = null;
            string? apiAccept = null;

            using var handler = new StubHttpMessageHandler(request =>
            {
                var uri = request.RequestUri!.AbsoluteUri;
                requestedUrls.Add(uri);

                if (uri == AppUpdateService.BuildLatestReleaseApiUrl(AppUpdateService.DefaultRepository))
                {
                    apiUserAgent = request.Headers.UserAgent.ToString();
                    apiAccept = request.Headers.Accept.ToString();
                    var json = JsonSerializer.Serialize(new
                    {
                        tag_name = "v0.6.0",
                        name = "v0.6.0",
                        body = "Release notes",
                        html_url = "https://github.com/zhengbaiyang/remote-desk/releases/tag/v0.6.0",
                        assets = new[]
                        {
                            new { name = "../../RemoteDesk.Windows-win-Setup.exe", browser_download_url = directAssetUrl, size = (long)payload.Length },
                        },
                    });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    };
                }

                if (uri == mirroredAssetUrl)
                    return new HttpResponseMessage(HttpStatusCode.BadGateway);

                if (uri == directAssetUrl)
                {
                    var content = new ByteArrayContent(payload);
                    content.Headers.ContentLength = payload.Length;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            using var httpClient = new HttpClient(handler);
            var service = new AppUpdateService(httpClient, downloadDir);

            var check = service.CheckLatestReleaseAsync("0.5.0", null, CancellationToken.None)
                .GetAwaiter().GetResult();

            Equal(true, check.IsUpdateAvailable);
            Equal("0.6.0", check.LatestVersion);
            Equal(true, apiUserAgent is not null && apiUserAgent.Contains("RemoteDesk", StringComparison.Ordinal));
            Equal(true, apiAccept is not null && apiAccept.Contains("application/vnd.github+json", StringComparison.OrdinalIgnoreCase));
            Equal(true, check.SetupAsset is not null);

            var progressEvents = new List<UpdateDownloadProgress>();
            var downloadedPath = service.DownloadInstallerAsync(
                check.SetupAsset!,
                mirrorPrefix,
                new SyncProgress<UpdateDownloadProgress>(progressEvents.Add),
                CancellationToken.None).GetAwaiter().GetResult();

            Equal(3, requestedUrls.Count);
            Equal(mirroredAssetUrl, requestedUrls[1]);
            Equal(directAssetUrl, requestedUrls[2]);
            Equal(Path.Combine(downloadDir, "RemoteDesk.Windows-win-Setup.exe"), downloadedPath);
            Equal(true, File.Exists(downloadedPath));
            Equal(true, File.ReadAllBytes(downloadedPath).SequenceEqual(payload));
            Equal(0, Directory.GetFiles(downloadDir, "*.part").Length);
            Equal(true, progressEvents.Count > 0 && progressEvents[^1].Percentage == 100.0);

            // Empty payload must be rejected cleanly without leaving partial files.
            using var emptyHandler = new StubHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) });
            using var emptyClient = new HttpClient(emptyHandler);
            var emptyService = new AppUpdateService(emptyClient, downloadDir);
            var emptyAsset = new ReleaseAssetInfo("Empty-Setup.exe", directAssetUrl, 0);
            Throws<InvalidDataException>(() =>
                emptyService.DownloadInstallerAsync(emptyAsset, null, null, CancellationToken.None)
                    .GetAwaiter().GetResult());
            Equal(0, Directory.GetFiles(downloadDir, "*.part").Length);
        }),
    ];

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
