using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteDesk;

internal sealed record AppSettings
{
    public IReadOnlyList<DeviceProfile> Devices { get; init; } = [];

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

    public bool UseDarkTheme { get; init; } = false;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HostModeEnabled { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StreamOptions? DefaultStream { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AccountSettings? Account { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LocalDeviceKey { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LocalDeviceName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? DeviceAliases { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? PairedDeviceIds { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SecurityPinRequired { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SecurityPinSalt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SecurityPinHash { get; init; }

    public bool AutoMatchResolution { get; init; } = true;

    public bool AutoSyncClipboard { get; init; } = true;
}

internal sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private sealed record StoredDeviceEntry(
        string? Name,
        string? HostAddress,
        StreamOptions? Stream,
        string? StreamAppName);

    private sealed record StoredSettingsDocument
    {
        public IReadOnlyList<StoredDeviceEntry?>? Devices { get; init; }
        public string? SunshinePath { get; init; }
        public string? EasyTierPath { get; init; }
        public EasyTierConfig? EasyTier { get; init; }
        public string? DownloadMirrorPrefix { get; init; }
        public string? UpdateRepository { get; init; }
        public bool UseDarkTheme { get; init; } = false;
        public bool HostModeEnabled { get; init; }
        public StreamOptions? DefaultStream { get; init; }
        public AccountSettings? Account { get; init; }
        public string? LocalDeviceKey { get; init; }
        public string? LocalDeviceName { get; init; }
        public IReadOnlyDictionary<string, string>? DeviceAliases { get; init; }
        public IReadOnlyList<string>? PairedDeviceIds { get; init; }
        public bool SecurityPinRequired { get; init; }
        public string? SecurityPinSalt { get; init; }
        public string? SecurityPinHash { get; init; }
        public bool AutoMatchResolution { get; init; } = true;
        public bool AutoSyncClipboard { get; init; } = true;

        [JsonPropertyName("device")]
        public StoredDeviceEntry? LegacyDevice { get; init; }
    }

    public AppSettings Load()
    {
        try
        {
            var stored = JsonSerializer.Deserialize<StoredSettingsDocument>(File.ReadAllText(path), JsonOptions)
                ?? throw new JsonException("设备设置为空。");

            var rawDevices = stored.Devices ?? [];
            if (rawDevices.Any(device => device is null))
                throw new JsonException("设备列表包含空记录。");

            var migratedDevices = new List<DeviceProfile>(rawDevices.Count);
            foreach (var entry in rawDevices)
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.HostAddress) || string.IsNullOrWhiteSpace(entry.Name))
                    continue;
                migratedDevices.Add(DeviceProfile.Create(entry.Name, entry.HostAddress, entry.Stream, entry.StreamAppName));
            }

            if (migratedDevices.Count == 0 &&
                stored.LegacyDevice is { } legacy &&
                !string.IsNullOrWhiteSpace(legacy.HostAddress) &&
                !string.IsNullOrWhiteSpace(legacy.Name))
            {
                migratedDevices.Add(DeviceProfile.Create(legacy.Name, legacy.HostAddress, legacy.Stream, legacy.StreamAppName));
            }

            var validatedDefaultStream = stored.DefaultStream?.Validate();
            var validatedEasyTier = stored.EasyTier is { Enabled: true } enabledEasyTier
                ? EasyTierConfig.Validate(enabledEasyTier)
                : stored.EasyTier;

            var normalizedLocalName = string.IsNullOrWhiteSpace(stored.LocalDeviceName)
                ? null
                : stored.LocalDeviceName.Trim();
            Dictionary<string, string>? normalizedAliases = null;
            if (stored.DeviceAliases is { Count: > 0 })
            {
                normalizedAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (k, v) in stored.DeviceAliases)
                {
                    if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(v))
                        normalizedAliases[k.Trim()] = v.Trim();
                }
                if (normalizedAliases.Count == 0)
                    normalizedAliases = null;
            }

            return new AppSettings
            {
                Devices = migratedDevices,
                SunshinePath = stored.SunshinePath,
                EasyTierPath = stored.EasyTierPath,
                EasyTier = validatedEasyTier,
                DownloadMirrorPrefix = stored.DownloadMirrorPrefix,
                UpdateRepository = stored.UpdateRepository,
                UseDarkTheme = stored.UseDarkTheme,
                HostModeEnabled = stored.HostModeEnabled,
                DefaultStream = validatedDefaultStream,
                Account = stored.Account,
                LocalDeviceKey = stored.LocalDeviceKey,
                LocalDeviceName = normalizedLocalName,
                DeviceAliases = normalizedAliases,
                PairedDeviceIds = stored.PairedDeviceIds,
                SecurityPinRequired = stored.SecurityPinRequired,
                SecurityPinSalt = stored.SecurityPinSalt,
                SecurityPinHash = stored.SecurityPinHash,
                AutoMatchResolution = stored.AutoMatchResolution,
                AutoSyncClipboard = stored.AutoSyncClipboard,
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
