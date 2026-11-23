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
        ("normalizes device names and pasted IDs", () =>
        {
            var device = DeviceProfile.Create("  家里的电脑  ", "123 456 789");
            Equal("家里的电脑", device.Name);
            Equal("123456789", device.Id);
            Equal<string?>(null, device.HostAddress);
            Equal(ConnectionMode.Office, device.PreferredMode);
            Equal("Desktop", device.StreamAppName);
            Equal(StreamOptions.FromPreset(StreamPresetKind.High2K), device.Stream);
            Equal("my_PC-2", DeviceProfile.Create("家里", "my_PC-2").Id);
        }),
        ("rejects an option-like target and shell syntax", () =>
        {
            Throws<ArgumentException>(() => DeviceProfile.Create("家里", "--relay"));
            Throws<ArgumentException>(() => DeviceProfile.Create("家里", "123 & calc"));
            Throws<ArgumentException>(() => DeviceProfile.Create(" ", "123456789"));
            Throws<ArgumentException>(() => DeviceProfile.Create("家里", "123\n456"));
        }),
        ("migrates a legacy v0.4.1 device and persists settings cleanly", () =>
        {
            var path = Path.Combine(location, "prefs", "settings.json");
            var store = new SettingsStore(path);
            var initial = store.Load();
            Equal(0, initial.Devices.Count);
            Equal(true, initial.UseDarkTheme);

            var clientPath = Path.Combine(location, "RustDesk Portable", "rustdesk.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                device = new { name = "家里的电脑", id = "123456789" },
                clientPath,
                useDarkTheme = true,
            }));
            var old = store.Load();
            Equal(DeviceProfile.Create("家里的电脑", "123456789"), old.Devices.Single());

            var expected = old with { Devices = [..old.Devices, DeviceProfile.Create("工位电脑", "111222333")] };
            store.Save(expected);
            var loaded = new SettingsStore(path).Load();
            Equal(true, loaded.Devices.SequenceEqual(expected.Devices));
            Equal(clientPath, loaded.ClientPath);
            Equal(true, loaded.UseDarkTheme);

            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Equal(false, json.RootElement.TryGetProperty("device", out _));
            Equal(3, json.RootElement.EnumerateObject().Count()); // devices, clientPath, useDarkTheme
            var secondDevice = json.RootElement.GetProperty("devices")[1];
            Equal(5, secondDevice.EnumerateObject().Count()); // name, id, preferredMode, stream, streamAppName
            Equal(false, secondDevice.TryGetProperty("hostAddress", out _));

            // Explicit light theme (false) must persist and reload as false (not revert to default true).
            store.Save(loaded with { Devices = [], UseDarkTheme = false });
            using var lightJson = JsonDocument.Parse(File.ReadAllText(path));
            Equal(true, lightJson.RootElement.TryGetProperty("useDarkTheme", out var darkProp));
            Equal(false, darkProp.GetBoolean());
            var reloadedLight = new SettingsStore(path).Load();
            Equal(0, reloadedLight.Devices.Count);
            Equal(false, reloadedLight.UseDarkTheme);
        }),
        ("persists extended Phase 1 AppSettings fields and validates DefaultStream", () =>
        {
            var path = Path.Combine(location, "prefs-v2", "settings.json");
            var store = new SettingsStore(path);
            var customStream = StreamOptions.Create(StreamPresetKind.Custom, 3840, 2160, 120, 80, VideoCodecPreference.Av1, false);
            var device = DeviceProfile.Create(
                "宿舍主力机",
                "987 654 321",
                "10.12.34.56",
                ConnectionMode.Stream,
                StreamOptions.FromPreset(StreamPresetKind.Ultra120),
                "Steam Big Picture");

            var saved = new AppSettings
            {
                Devices = [device],
                ClientPath = @"C:\Tools\rustdesk.exe",
                MoonlightPath = @"C:\Tools\Moonlight.exe",
                SunshinePath = @"C:\Tools\sunshine.exe",
                DownloadMirrorPrefix = "https://gh-proxy.example/",
                UseDarkTheme = false,
                HostModeEnabled = true,
                DefaultStream = customStream,
            };
            store.Save(saved);

            var loaded = new SettingsStore(path).Load();
            Equal(device, loaded.Devices.Single());
            Equal(@"C:\Tools\Moonlight.exe", loaded.MoonlightPath);
            Equal(@"C:\Tools\sunshine.exe", loaded.SunshinePath);
            Equal("https://gh-proxy.example/", loaded.DownloadMirrorPrefix);
            Equal(false, loaded.UseDarkTheme);
            Equal(true, loaded.HostModeEnabled);
            Equal(customStream, loaded.DefaultStream);

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
        ("supports dual-mode DeviceProfile and rejects HostAddress / StreamAppName injection", () =>
        {
            // Moonlight-only (no RustDesk ID) with IPv4, IPv6, and DNS hostname.
            var ipv4 = DeviceProfile.Create("实验室主机", null, "192.168.31.88", ConnectionMode.Stream);
            Equal<string?>(null, ipv4.Id);
            Equal("192.168.31.88", ipv4.HostAddress);
            Equal(ConnectionMode.Stream, ipv4.PreferredMode);
            Equal(true, ipv4.SupportsStream);
            Equal(true, ipv4.SupportsOffice);

            var ipv6 = DeviceProfile.Create("校园网 IPv6", null, "2001:da8:201::1", ConnectionMode.Stream);
            Equal("2001:da8:201::1", ipv6.HostAddress);

            var dns = DeviceProfile.Create("宿舍主机", "123 456 789", "dorm-pc.campus.edu.cn", ConnectionMode.Stream, StreamOptions.FromPreset(StreamPresetKind.Ultra120), "Desktop");
            Equal("123456789", dns.Id);
            Equal("dorm-pc.campus.edu.cn", dns.HostAddress);

            // At least one of Id or HostAddress is required.
            Throws<ArgumentException>(() => DeviceProfile.Create("空设备", null, null, ConnectionMode.Stream));
            Throws<ArgumentException>(() => DeviceProfile.Create("空设备", "   ", "   ", ConnectionMode.Office));

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
                Throws<ArgumentException>(() => DeviceProfile.Create("测试", null, badHost, ConnectionMode.Stream));
            }

            // StreamAppName validation.
            Throws<ArgumentException>(() => DeviceProfile.Create("测试", null, "10.0.0.1", ConnectionMode.Stream, null, ""));
            Throws<ArgumentException>(() => DeviceProfile.Create("测试", null, "10.0.0.1", ConnectionMode.Stream, null, "   "));
            Throws<ArgumentException>(() => DeviceProfile.Create("测试", null, "10.0.0.1", ConnectionMode.Stream, null, new string('A', 65)));
            Throws<ArgumentException>(() => DeviceProfile.Create("测试", null, "10.0.0.1", ConnectionMode.Stream, null, "Desktop\n--fps"));
            Throws<ArgumentException>(() => DeviceProfile.Create("测试", null, "10.0.0.1", ConnectionMode.Stream, null, "--fps"));
        }),
        ("passes a single device ID or HostAddress fallback to RustDesk and starts host cleanly", () =>
        {
            var path = Path.Combine(location, "RustDesk Portable", "rustdesk.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []);

            var idStart = RustDeskClient.CreateConnection(path, DeviceProfile.Create("家里的电脑", "123456789"));
            Equal(path, idStart.FileName);
            Equal("--connect|123456789", string.Join('|', idStart.ArgumentList));
            Equal(false, idStart.UseShellExecute);

            var hostFallbackDevice = DeviceProfile.Create("局域网电脑", null, "192.168.1.20", ConnectionMode.Office);
            var hostStart = RustDeskClient.CreateConnection(path, hostFallbackDevice);
            Equal("--connect|192.168.1.20", string.Join('|', hostStart.ArgumentList));
            Equal(false, hostStart.UseShellExecute);

            var localHost = RustDeskClient.CreateHostStart(path);
            Equal(path, localHost.FileName);
            Equal(0, localHost.ArgumentList.Count);
            Equal(false, localHost.UseShellExecute);
        }),
        ("enforces selectedPath over managedPath precedence in RustDeskClient and MoonlightClient", () =>
        {
            var selectedRustDesk = Path.Combine(location, "selected", "rustdesk.exe");
            var managedRustDesk = Path.Combine(location, "managed", "rustdesk.exe");
            var selectedMoonlight = Path.Combine(location, "selected", "Moonlight.exe");
            var managedMoonlight = Path.Combine(location, "managed", "Moonlight.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(selectedRustDesk)!);
            Directory.CreateDirectory(Path.GetDirectoryName(managedRustDesk)!);
            File.WriteAllBytes(selectedRustDesk, []);
            File.WriteAllBytes(managedRustDesk, []);
            File.WriteAllBytes(selectedMoonlight, []);
            File.WriteAllBytes(managedMoonlight, []);

            Equal(selectedRustDesk, RustDeskClient.FindExecutable(selectedRustDesk, managedRustDesk));
            Equal(managedRustDesk, RustDeskClient.FindExecutable(null, managedRustDesk));
            Equal(managedRustDesk, RustDeskClient.FindExecutable(Path.Combine(location, "missing.exe"), managedRustDesk));

            Equal(selectedMoonlight, MoonlightClient.FindExecutable(selectedMoonlight, managedMoonlight));
            Equal(managedMoonlight, MoonlightClient.FindExecutable(null, managedMoonlight));
            Equal(managedMoonlight, MoonlightClient.FindExecutable(Path.Combine(location, "missing-ml.exe"), managedMoonlight));
        }),
        ("builds safe MoonlightClient stream and pair ArgumentLists", () =>
        {
            var exe = Path.Combine(location, "Moonlight Portable", "Moonlight.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllBytes(exe, []);

            var device = DeviceProfile.Create(
                "宿舍主力机",
                null,
                "10.20.30.40",
                ConnectionMode.Stream,
                StreamOptions.FromPreset(StreamPresetKind.Ultra120),
                "Desktop");

            var streamInfo = MoonlightClient.CreateStreamConnection(exe, device);
            Equal(exe, streamInfo.FileName);
            Equal(false, streamInfo.UseShellExecute);
            Equal(
                "stream|10.20.30.40|Desktop|--resolution|1920x1080|--fps|120|--bitrate|45000|--video-codec|HEVC|--video-decoder|hardware",
                string.Join('|', streamInfo.ArgumentList));

            var overrideOpts = StreamOptions.Create(StreamPresetKind.Custom, 2560, 1440, 90, 28, VideoCodecPreference.Av1, false);
            var overrideInfo = MoonlightClient.CreateStreamConnection(exe, device, overrideOpts);
            Equal(
                "stream|10.20.30.40|Desktop|--resolution|2560x1440|--fps|90|--bitrate|28000|--video-codec|AV1|--video-decoder|software",
                string.Join('|', overrideInfo.ArgumentList));

            var h264Opts = StreamOptions.FromPreset(StreamPresetKind.Saver);
            Equal("H.264", MoonlightClient.CreateStreamConnection(exe, device, h264Opts).ArgumentList[10]);

            var autoOpts = h264Opts with { Codec = VideoCodecPreference.Auto };
            Equal("auto", MoonlightClient.CreateStreamConnection(exe, device, autoOpts).ArgumentList[10]);

            var pairInfo = MoonlightClient.CreatePairConnection(exe, "2001:da8:201::1");
            Equal(exe, pairInfo.FileName);
            Equal(false, pairInfo.UseShellExecute);
            Equal("pair|2001:da8:201::1", string.Join('|', pairInfo.ArgumentList));

            // Missing host or bad executable must fail.
            var idOnlyDevice = DeviceProfile.Create("仅办公电脑", "123456789");
            Throws<ArgumentException>(() => MoonlightClient.CreateStreamConnection(exe, idOnlyDevice));
            Throws<FileNotFoundException>(() => MoonlightClient.CreateStreamConnection(Path.Combine(location, "missing.exe"), device));
            Throws<ArgumentException>(() => MoonlightClient.CreatePairConnection(exe, "10.0.0.1 & calc"));
            Throws<FileNotFoundException>(() => MoonlightClient.CreatePairConnection(Path.Combine(location, "missing.exe"), "10.0.0.1"));
        }),
        ("refuses to construct a command for a missing client", () =>
        {
            var path = Path.Combine(location, "missing.exe");
            Throws<FileNotFoundException>(() => RustDeskClient.CreateConnection(path, DeviceProfile.Create("家里的电脑", "123456789")));
            Throws<FileNotFoundException>(() => RustDeskClient.CreateHostStart(path));
            Throws<FileNotFoundException>(() => HostServiceManager.CreateSunshineHostStart(path));
        }),
        ("discovers RustDesk and Moonlight from custom install records and quoted service commands", () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            var folder = Path.Combine(location, "Custom Applications");
            Directory.CreateDirectory(folder);
            var rustDeskExe = Path.Combine(folder, "rustdesk.exe");
            var moonlightExe = Path.Combine(folder, "Moonlight.exe");
            File.WriteAllBytes(rustDeskExe, []);
            File.WriteAllBytes(moonlightExe, []);

            var keyName = @"Software\RemoteDesk-Discovery-Test-" + Guid.NewGuid().ToString("N");
            using var root = Registry.CurrentUser.CreateSubKey(keyName);
            try
            {
                using var msi = root.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{test-install}");
                msi.SetValue("DisplayName", "RustDesk");
                msi.SetValue("InstallLocation", folder);
                Equal(rustDeskExe, RustDeskClient.FindRegisteredClient(root));
                msi.DeleteValue("InstallLocation");
                msi.SetValue("DisplayIcon", $"\"{rustDeskExe}\",0");
                Equal(rustDeskExe, RustDeskClient.FindRegisteredClient(root));
                msi.DeleteValue("DisplayIcon");
                using var service = root.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\RustDesk");
                service.SetValue("ImagePath", $"\"{rustDeskExe}\" --service");
                Equal(rustDeskExe, RustDeskClient.FindRegisteredClient(root));
                service.SetValue("ImagePath", rustDeskExe + ".bak");
                Equal<string?>(null, RustDeskClient.FindRegisteredClient(root));

                using var mlEntry = root.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Moonlight Game Streaming");
                mlEntry.SetValue("DisplayName", "Moonlight Game Streaming");
                mlEntry.SetValue("InstallLocation", folder);
                Equal(moonlightExe, MoonlightClient.FindRegisteredClient(root));
                mlEntry.DeleteValue("InstallLocation");
                mlEntry.SetValue("DisplayIcon", $"\"{moonlightExe}\",0");
                Equal(moonlightExe, MoonlightClient.FindRegisteredClient(root));
            }
            finally { Registry.CurrentUser.DeleteSubKeyTree(keyName); }
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
            var status = HostServiceManager.GetHostStatus(fakeSunshine, Path.Combine(location, "missing-rd.exe"));
            Equal(true, status.SunshineInstalled);
            Equal(fakeSunshine, status.SunshineExecutablePath);
            Equal(false, status.RustDeskInstalled);
            Equal<string?>(null, status.RustDeskExecutablePath);

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
        .Concat(UpdateChecks.CreateTests(location));

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
