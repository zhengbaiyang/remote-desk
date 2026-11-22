using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteDesk;

internal sealed record AppSettings
{
    public IReadOnlyList<DeviceProfile> Devices { get; init; } = [];
    public string? ClientPath { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UseDarkTheme { get; init; }

    [JsonPropertyName("device")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DeviceProfile? LegacyDevice { get; init; }
}

internal sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public AppSettings Load()
    {
        try
        {
            var stored = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions)
                ?? throw new JsonException("设备设置为空。");
            var devices = stored.Devices ?? [];
            if (devices.Any(device => device is null))
                throw new JsonException("设备列表包含空记录。");
            if (devices.Count == 0 && stored.LegacyDevice is { } legacy)
                devices = [legacy];
            return stored with { Devices = devices, LegacyDevice = null };
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
