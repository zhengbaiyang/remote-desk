using RemoteDesk;
using System.Text.Json;
using Microsoft.Win32;

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
            Equal("my_PC-2", DeviceProfile.Create("家里", "my_PC-2").Id);
        }),
        ("rejects an option-like target and shell syntax", () =>
        {
            Throws<ArgumentException>(() => DeviceProfile.Create("家里", "--relay"));
            Throws<ArgumentException>(() => DeviceProfile.Create("家里", "123 & calc"));
            Throws<ArgumentException>(() => DeviceProfile.Create(" ", "123456789"));
        }),
        ("migrates a legacy device and persists an ordered list without credentials", () =>
        {
            var path = Path.Combine(location, "prefs", "settings.json");
            var store = new SettingsStore(path);
            Equal(0, store.Load().Devices.Count);
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
            Equal(3, json.RootElement.EnumerateObject().Count());
            Equal(2, json.RootElement.GetProperty("devices")[1].EnumerateObject().Count());
            store.Save(loaded with { Devices = [] });
            Equal(0, store.Load().Devices.Count);
        }),
        ("reports malformed settings to the caller", () =>
        {
            var path = Path.Combine(location, "broken.json");
            File.WriteAllText(path, "{");
            Throws<JsonException>(() => new SettingsStore(path).Load());
        }),
        ("passes a single device ID to RustDesk without credentials", () =>
        {
            var path = Path.Combine(location, "RustDesk Portable", "rustdesk.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []);
            var startInfo = RustDeskClient.CreateConnection(path, DeviceProfile.Create("家里的电脑", "123456789"));
            Equal(path, startInfo.FileName);
            Equal("--connect|123456789", string.Join('|', startInfo.ArgumentList));
            Equal(false, startInfo.UseShellExecute);
        }),
        ("refuses to construct a command for a missing client", () =>
        {
            var path = Path.Combine(location, "missing.exe");
            Throws<FileNotFoundException>(() => RustDeskClient.CreateConnection(path, DeviceProfile.Create("家里的电脑", "123456789")));
        }),
        ("discovers RustDesk from custom install records and quoted service commands", () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            var folder = Path.Combine(location, "Custom Applications");
            Directory.CreateDirectory(folder);
            var exe = Path.Combine(folder, "rustdesk.exe");
            File.WriteAllBytes(exe, []);
            var keyName = @"Software\RemoteDesk-Discovery-Test-" + Guid.NewGuid().ToString("N");
            using var root = Registry.CurrentUser.CreateSubKey(keyName);
            try
            {
                using var msi = root.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{test-install}");
                msi.SetValue("DisplayName", "RustDesk");
                msi.SetValue("InstallLocation", folder);
                Equal(exe, RustDeskClient.FindRegisteredClient(root));
                msi.DeleteValue("InstallLocation");
                msi.SetValue("DisplayIcon", $"\"{exe}\",0");
                Equal(exe, RustDeskClient.FindRegisteredClient(root));
                msi.DeleteValue("DisplayIcon");
                using var service = root.CreateSubKey(@"SYSTEM\CurrentControlSet\Services\RustDesk");
                service.SetValue("ImagePath", $"\"{exe}\" --service");
                Equal(exe, RustDeskClient.FindRegisteredClient(root));
                service.SetValue("ImagePath", exe + ".bak");
                Equal<string?>(null, RustDeskClient.FindRegisteredClient(root));
            }
            finally { Registry.CurrentUser.DeleteSubKeyTree(keyName); }
        }),
    };

    var failed = 0;
    foreach (var (name, run) in tests)
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
