using RemoteDesk;
using System.Text.Json;

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
        ("persists the updated device and RustDesk path without credentials", () =>
        {
            var path = Path.Combine(location, "prefs", "settings.json");
            var store = new SettingsStore(path);
            Equal(new AppSettings(), store.Load());
            var clientPath = Path.Combine(location, "RustDesk Portable", "rustdesk.exe");
            store.Save(new AppSettings(DeviceProfile.Create("之前的电脑", "111222333"), clientPath));
            var expected = new AppSettings(DeviceProfile.Create("家里的电脑", "123456789"), clientPath);
            store.Save(expected);
            Equal(expected, new SettingsStore(path).Load());
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Equal(2, json.RootElement.EnumerateObject().Count());
            Equal(2, json.RootElement.GetProperty("device").EnumerateObject().Count());
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
