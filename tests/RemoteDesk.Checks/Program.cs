using System.Net;
using System.Text.Json;
using Microsoft.Win32;
using RemoteDesk;

var location = Path.Combine(Path.GetTempPath(), "RemoteDesk checks " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(location);
try
{
    var tests = new (string Name, Action Run)[]
    {
        ("normalizes device names and validates host addresses", () =>
        {
            var device = DeviceProfile.Create("  家里的电脑  ", " 10.126.126.2 ");
            Equal("家里的电脑", device.Name);
            Equal("10.126.126.2", device.HostAddress);
            Equal("Desktop", device.StreamAppName);
            Equal(StreamOptions.FromPreset(StreamPresetKind.High2K), device.Stream);
            Equal(true, device.SupportsStream);
        }),
        ("rejects an option-like target and shell syntax", () =>
        {
            Throws<ArgumentException>(() => DeviceProfile.Create("家里", "--relay"));
            Throws<ArgumentException>(() => DeviceProfile.Create("家里", "123 & calc"));
            Throws<ArgumentException>(() => DeviceProfile.Create(" ", "10.126.126.2"));
            Throws<ArgumentException>(() => DeviceProfile.Create("家里", "123\n456"));
        }),
        ("migrates legacy settings cleanly and persists AutoMatchResolution / AutoSyncClipboard", () =>
        {
            var path = Path.Combine(location, "prefs", "settings.json");
            var store = new SettingsStore(path);
            var initial = store.Load();
            Equal(0, initial.Devices.Count);
            Equal(true, initial.UseDarkTheme);
            Equal(true, initial.AutoMatchResolution);
            Equal(true, initial.AutoSyncClipboard);

            // Legacy v0.4.1 JSON with only RustDesk ID must load cleanly with RustDesk-only entry dropped.
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                device = new { name = "家里的电脑", id = "123456789" },
                clientPath = @"C:\Old\rustdesk.exe",
                useDarkTheme = true,
            }));
            var old = store.Load();
            Equal(0, old.Devices.Count);

            var d1 = DeviceProfile.Create("家里的电脑", "192.168.1.50");
            var d2 = DeviceProfile.Create("工位电脑", "10.126.126.3");
            var expected = old with { Devices = [d1, d2], AutoMatchResolution = false, AutoSyncClipboard = false };
            store.Save(expected);
            var loaded = new SettingsStore(path).Load();
            Equal(true, loaded.Devices.SequenceEqual(expected.Devices));
            Equal(false, loaded.AutoMatchResolution);
            Equal(false, loaded.AutoSyncClipboard);
            Equal(true, loaded.UseDarkTheme);

            // Explicit light theme (false) must persist and reload as false (not revert to default true).
            store.Save(loaded with { Devices = [], UseDarkTheme = false });
            using var lightJson = JsonDocument.Parse(File.ReadAllText(path));
            Equal(true, lightJson.RootElement.TryGetProperty("useDarkTheme", out var darkProp));
            Equal(false, darkProp.GetBoolean());
            var reloadedLight = new SettingsStore(path).Load();
            Equal(0, reloadedLight.Devices.Count);
            Equal(false, reloadedLight.UseDarkTheme);
        }),
        ("persists extended v2 AppSettings fields and validates DefaultStream", () =>
        {
            var path = Path.Combine(location, "prefs-v2", "settings.json");
            var store = new SettingsStore(path);
            var customStream = StreamOptions.Create(StreamPresetKind.Custom, 3840, 2160, 120, 80, VideoCodecPreference.Av1, false);
            var device = DeviceProfile.Create(
                "宿舍主力机",
                "10.12.34.56",
                StreamOptions.FromPreset(StreamPresetKind.Ultra120),
                "Steam Big Picture");

            var saved = new AppSettings
            {
                Devices = [device],
                SunshinePath = @"C:\Tools\sunshine.exe",
                DownloadMirrorPrefix = "https://gh-proxy.example/",
                UseDarkTheme = false,
                HostModeEnabled = true,
                DefaultStream = customStream,
                AutoMatchResolution = true,
                AutoSyncClipboard = false,
            };
            store.Save(saved);

            var loaded = new SettingsStore(path).Load();
            Equal(device, loaded.Devices.Single());
            Equal(@"C:\Tools\sunshine.exe", loaded.SunshinePath);
            Equal("https://gh-proxy.example/", loaded.DownloadMirrorPrefix);
            Equal(false, loaded.UseDarkTheme);
            Equal(true, loaded.HostModeEnabled);
            Equal(customStream, loaded.DefaultStream);
            Equal(true, loaded.AutoMatchResolution);
            Equal(false, loaded.AutoSyncClipboard);

            // Invalid DefaultStream on disk is rejected on load.
            File.WriteAllText(path, """{"defaultStream":{"preset":"Custom","width":100,"height":1080,"fps":60,"bitrateMbps":20,"codec":"H265","hardwareDecoding":true}}""");
            Throws<ArgumentOutOfRangeException>(() => new SettingsStore(path).Load());
        }),
        ("reports malformed settings to the caller", () =>
        {
            var path = Path.Combine(location, "broken.json");
            File.WriteAllText(path, "{");
            Throws<JsonException>(() => new SettingsStore(path).Load());
        }),
        ("validates StreamOptions presets, boundaries, and normalization", () =>
        {
            var ultra = StreamOptions.FromPreset(StreamPresetKind.Ultra120);
            Equal(1920, ultra.Width);
            Equal(1080, ultra.Height);
            Equal(120, ultra.Fps);
            Equal(45, ultra.BitrateMbps);
            Equal(VideoCodecPreference.H265, ultra.Codec);
            Equal(true, ultra.HardwareDecoding);

            var high2k = StreamOptions.FromPreset(StreamPresetKind.High2K);
            Equal(2560, high2k.Width);
            Equal(1440, high2k.Height);
            Equal(60, high2k.Fps);
            Equal(35, high2k.BitrateMbps);
            Equal(VideoCodecPreference.H265, high2k.Codec);
            Equal(true, high2k.HardwareDecoding);

            var saver = StreamOptions.FromPreset(StreamPresetKind.Saver);
            Equal(1920, saver.Width);
            Equal(1080, saver.Height);
            Equal(60, saver.Fps);
            Equal(12, saver.BitrateMbps);
            Equal(VideoCodecPreference.H264, saver.Codec);
            Equal(true, saver.HardwareDecoding);

            var custom = StreamOptions.FromPreset(StreamPresetKind.Custom);
            Equal(StreamPresetKind.Custom, custom.Preset);
            Equal(high2k with { Preset = StreamPresetKind.Custom }, custom);

            // Exact min and max boundaries succeed.
            StreamOptions.Create(StreamPresetKind.Custom, 640, 480, 30, 2, VideoCodecPreference.Auto, false);
            StreamOptions.Create(StreamPresetKind.Custom, 7680, 4320, 240, 150, VideoCodecPreference.Av1, true);

            // Out-of-range values fail Validate().
            Throws<ArgumentOutOfRangeException>(() => new StreamOptions(StreamPresetKind.Custom, 639, 1080, 60, 20, VideoCodecPreference.H265, true).Validate());
            Throws<ArgumentOutOfRangeException>(() => new StreamOptions(StreamPresetKind.Custom, 7681, 1080, 60, 20, VideoCodecPreference.H265, true).Validate());
            Throws<ArgumentOutOfRangeException>(() => new StreamOptions(StreamPresetKind.Custom, 1920, 479, 60, 20, VideoCodecPreference.H265, true).Validate());
            Throws<ArgumentOutOfRangeException>(() => new StreamOptions(StreamPresetKind.Custom, 1920, 4321, 60, 20, VideoCodecPreference.H265, true).Validate());
            Throws<ArgumentOutOfRangeException>(() => new StreamOptions(StreamPresetKind.Custom, 1920, 1080, 29, 20, VideoCodecPreference.H265, true).Validate());
            Throws<ArgumentOutOfRangeException>(() => new StreamOptions(StreamPresetKind.Custom, 1920, 1080, 241, 20, VideoCodecPreference.H265, true).Validate());
            Throws<ArgumentOutOfRangeException>(() => new StreamOptions(StreamPresetKind.Custom, 1920, 1080, 60, 1, VideoCodecPreference.H265, true).Validate());
            Throws<ArgumentOutOfRangeException>(() => new StreamOptions(StreamPresetKind.Custom, 1920, 1080, 60, 151, VideoCodecPreference.H265, true).Validate());
            Throws<ArgumentOutOfRangeException>(() => new StreamOptions((StreamPresetKind)99, 1920, 1080, 60, 20, VideoCodecPreference.H265, true).Validate());
            Throws<ArgumentOutOfRangeException>(() => new StreamOptions(StreamPresetKind.Custom, 1920, 1080, 60, 20, (VideoCodecPreference)99, true).Validate());

            // Normalize clamps invalid numbers and defaults null to High2K.
            Equal(high2k, StreamOptions.Normalize(null));
            var clamped = StreamOptions.Normalize(new StreamOptions(StreamPresetKind.Custom, 100, 9999, 500, 1, VideoCodecPreference.Auto, false));
            Equal(640, clamped.Width);
            Equal(4320, clamped.Height);
            Equal(240, clamped.Fps);
            Equal(2, clamped.BitrateMbps);
        }),
        ("validates DeviceProfile and rejects HostAddress / StreamAppName injection", () =>
        {
            var ipv4 = DeviceProfile.Create("实验室主机", "192.168.31.88");
            Equal("192.168.31.88", ipv4.HostAddress);
            Equal(true, ipv4.SupportsStream);

            var ipv6 = DeviceProfile.Create("校园网 IPv6", "2001:da8:201::1");
            Equal("2001:da8:201::1", ipv6.HostAddress);

            var dns = DeviceProfile.Create("宿舍主机", "dorm-pc.campus.edu.cn", StreamOptions.FromPreset(StreamPresetKind.Ultra120), "Desktop");
            Equal("dorm-pc.campus.edu.cn", dns.HostAddress);

            // Empty or invalid hostAddress must be rejected.
            Throws<ArgumentException>(() => DeviceProfile.Create("空设备", null!));
            Throws<ArgumentException>(() => DeviceProfile.Create("空设备", "   "));

            // HostAddress injection vectors must all be rejected.
            string[] badHosts =
            [
                "-v",
                "--resolution",
                "192.168.1.1 192.168.1.2",
                "192.168.1.1 & calc",
                "host;rm -rf /",
                "host|whoami",
                "$(calc)",
                "`id`",
                "host\"arg",
                "host'arg",
                "host>out.txt",
                "host<in.txt",
                "192.168.1.1\ncalc",
                "192.168.1.1\r\n",
                "192.168.1.1\t",
                "192.168.1.1\0",
                "fe80::1%eth0",
                "[::1]",
                "host%PATH%",
                "999.999.999.999",
                "12345",
                "bad..host",
                ".leadingdot.local",
            ];
            foreach (var badHost in badHosts)
            {
                Throws<ArgumentException>(() => DeviceProfile.Create("测试", badHost));
            }

            // StreamAppName validation.
            Throws<ArgumentException>(() => DeviceProfile.Create("测试", "10.0.0.1", null, ""));
            Throws<ArgumentException>(() => DeviceProfile.Create("测试", "10.0.0.1", null, "   "));
            Throws<ArgumentException>(() => DeviceProfile.Create("测试", "10.0.0.1", null, new string('A', 65)));
            Throws<ArgumentException>(() => DeviceProfile.Create("测试", "10.0.0.1", null, "Desktop\n--fps"));
            Throws<ArgumentException>(() => DeviceProfile.Create("测试", "10.0.0.1", null, "--fps"));
        }),
        ("refuses to construct a command for a missing host binary", () =>
        {
            var path = Path.Combine(location, "missing.exe");
            Throws<FileNotFoundException>(() => HostServiceManager.CreateSunshineHostStart(path));
        }),
        ("inspects local network info, filters loopback/APIPA/link-local, classifies GPUs, and inspects host status", () =>
        {
            Equal("https://localhost:47990", HostServiceManager.GetSunshineWebUiUrl());

            var netInfo = HostServiceManager.GetLocalNetworkInfo();
            Equal(true, !string.IsNullOrWhiteSpace(netInfo.HostName));
            Equal(false, netInfo.IPv4Addresses.Any(ip => ip.StartsWith("127.") || ip.StartsWith("169.254.")));
            Equal(false, netInfo.IPv6Addresses.Any(ip => ip == "::1" || ip.StartsWith("fe80:", StringComparison.OrdinalIgnoreCase)));

            var (filteredV4, filteredV6) = HostServiceManager.FilterIpAddresses(
            [
                IPAddress.Parse("127.0.0.1"),
                IPAddress.Parse("169.254.12.34"),
                IPAddress.Parse("10.12.34.56"),
                IPAddress.Parse("192.168.1.5"),
                IPAddress.Parse("10.12.34.56"),
                IPAddress.Parse("::1"),
                IPAddress.Parse("fe80::1%12"),
                IPAddress.Parse("2001:da8:201::42"),
            ]);
            Equal("10.12.34.56|192.168.1.5", string.Join('|', filteredV4));
            Equal("2001:da8:201::42", string.Join('|', filteredV6));

            // Direct GPU vendor/encoder classification checks.
            var nv = HostServiceManager.TryClassifyGpu("NVIDIA GeForce RTX 4060 Laptop GPU", "NVIDIA", @"pci\ven_10de&dev_28e0");
            Equal(new GpuEncoderInfo("NVIDIA GeForce RTX 4060 Laptop GPU", "NVIDIA", "NVENC"), nv);
            var intel = HostServiceManager.TryClassifyGpu("Intel(R) UHD Graphics", "Intel Corporation", @"pci\ven_8086&dev_a788");
            Equal(new GpuEncoderInfo("Intel(R) UHD Graphics", "Intel", "QSV"), intel);
            var amd = HostServiceManager.TryClassifyGpu("AMD Radeon RX 7800 XT", "Advanced Micro Devices, Inc.", @"pci\ven_1002&dev_747e");
            Equal(new GpuEncoderInfo("AMD Radeon RX 7800 XT", "AMD", "AMF"), amd);
            Equal<GpuEncoderInfo?>(null, HostServiceManager.TryClassifyGpu("Microsoft Basic Display Adapter", "Microsoft", ""));

            // Live GPU encoder detection does not throw.
            var liveGpus = HostServiceManager.DetectGpuEncoders();
            Equal(true, liveGpus is not null);

            var fakeSunshine = Path.Combine(location, "sunshine.exe");
            File.WriteAllBytes(fakeSunshine, []);
            var status = HostServiceManager.GetHostStatus(fakeSunshine);
            Equal(true, status.SunshineInstalled);
            Equal(fakeSunshine, status.SunshineExecutablePath);

            var sunshineStart = HostServiceManager.CreateSunshineHostStart(fakeSunshine);
            Equal(fakeSunshine, sunshineStart.FileName);
            Equal(false, sunshineStart.UseShellExecute);
            Equal(true, sunshineStart.CreateNoWindow);
        }),
        ("detects GPU encoders from Windows display adapter registry class", () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            var gpuKeyName = @"Software\RemoteDesk-Gpu-Test-" + Guid.NewGuid().ToString("N");
            using var root = Registry.CurrentUser.CreateSubKey(gpuKeyName);
            try
            {
                const string cls = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
                using (var k0 = root.CreateSubKey(cls + @"\0000"))
                {
                    k0.SetValue("DriverDesc", "NVIDIA GeForce RTX 4060 Laptop GPU");
                    k0.SetValue("ProviderName", "NVIDIA");
                    k0.SetValue("MatchingDeviceId", @"pci\ven_10de&dev_28e0");
                }
                using (var k1 = root.CreateSubKey(cls + @"\0001"))
                {
                    k1.SetValue("DriverDesc", "Intel(R) UHD Graphics");
                    k1.SetValue("ProviderName", "Intel Corporation");
                    k1.SetValue("MatchingDeviceId", @"pci\ven_8086&dev_a788");
                }
                using (var k2 = root.CreateSubKey(cls + @"\0002"))
                {
                    k2.SetValue("DriverDesc", "AMD Radeon RX 7800 XT");
                    k2.SetValue("ProviderName", "Advanced Micro Devices, Inc.");
                    k2.SetValue("MatchingDeviceId", @"pci\ven_1002&dev_747e");
                }
                using (var k3 = root.CreateSubKey(cls + @"\0003"))
                {
                    k3.SetValue("DriverDesc", "Microsoft Basic Display Adapter");
                    k3.SetValue("ProviderName", "Microsoft");
                }

                var detected = HostServiceManager.DetectGpuEncoders(root);
                Equal(3, detected.Count);
                Equal(true, detected.Any(g => g.Vendor == "NVIDIA" && g.EncoderName == "NVENC"));
                Equal(true, detected.Any(g => g.Vendor == "Intel" && g.EncoderName == "QSV"));
                Equal(true, detected.Any(g => g.Vendor == "AMD" && g.EncoderName == "AMF"));
            }
            finally { Registry.CurrentUser.DeleteSubKeyTree(gpuKeyName); }
        }),
    };

    var allTests = tests
        .Concat(EngineChecks.CreateTests(location))
        .Concat(EasyTierChecks.CreateTests(location))
        .Concat(UpdateChecks.CreateTests(location))
        .Concat(Milestone1Checks.CreateTests(location))
        .Concat(Milestone2Checks.CreateTests(location))
        .Concat(Milestone3Checks.CreateTests(location));

    var failed = 0;
    foreach (var (name, run) in allTests)
    {
        try
        {
            run();
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception error)
        {
            failed++;
            Console.Error.WriteLine($"FAIL {name}: {error.Message}");
        }
    }

    return failed == 0 ? 0 : 1;
}
finally
{
    Directory.Delete(location, recursive: true);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

static void Throws<T>(Action action) where T : Exception
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
