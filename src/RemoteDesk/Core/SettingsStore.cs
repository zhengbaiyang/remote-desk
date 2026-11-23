using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteDesk;

internal sealed record AppSettings
{
    public IReadOnlyList<DeviceProfile> Devices { get; init; } = [];

    public string? ClientPath { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MoonlightPath { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SunshinePath { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EasyTierPath { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EasyTierConfig? EasyTier { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DownloadMirrorPrefix { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpdateRepository { get; init; }

    public bool UseDarkTheme { get; init; } = true;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HostModeEnabled { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StreamOptions? DefaultStream { get; init; }

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
            var validatedDefaultStream = stored.DefaultStream?.Validate();
            var validatedEasyTier = stored.EasyTier is { Enabled: true } enabledEasyTier
                ? EasyTierConfig.Validate(enabledEasyTier)
                : stored.EasyTier;
            return stored with
            {
                Devices = devices,
                DefaultStream = validatedDefaultStream,
                EasyTier = validatedEasyTier,
                LegacyDevice = null,
            };
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.DefaultStream?.Validate();
        if (settings.EasyTier is { Enabled: true } enabledEasyTier)
            EasyTierConfig.Validate(enabledEasyTier);

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
