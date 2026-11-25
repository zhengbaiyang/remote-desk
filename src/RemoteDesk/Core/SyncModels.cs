using System.Text.Json.Serialization;

namespace RemoteDesk;

internal sealed record AccountMeshConfig(
    string PeerUrl,
    string FallbackWsUrl,
    string NetworkName,
    string NetworkSecret,
    string AgentAuthKey,
    string SubnetCidr);

internal sealed record AccountUserInfo(
    string Id,
    string Username,
    long CreatedAtUtc);

internal sealed record AccountSettings
{
    public string ServerUrl { get; init; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServerCertThumbprint { get; init; }

    public string UserId { get; init; } = "";

    public string Username { get; init; } = "";

    public string ProtectedSessionToken { get; init; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AccountMeshConfig? MeshConfig { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AssignedVirtualIpv4 { get; init; }

    public bool AutoSyncEnabled { get; init; } = true;
}

internal sealed record SyncedDeviceInfo(
    string Id,
    string DeviceKey,
    string Name,
    string Hostname,
    string VirtualIpv4,
    string? LanIpv4,
    string? LanIpv6,
    bool SunshineReady,
    int SunshinePort,
    int AgentPort,
    bool SecurityPinRequired,
    string? GpuEncoder,
    string? OsVersion,
    long LastSeenAtUtc,
    bool IsOnline,
    string? RemoteControlState = null,
    string? ControlledByDeviceKey = null,
    string? ControlledByDeviceName = null,
    string? ControllingTargetDeviceKey = null,
    string? ControllingTargetDeviceName = null);

internal sealed record SyncHealthResponse(
    bool Ok,
    string Version,
    string RegistrationMode);

internal sealed record SyncAuthResult(
    string Token,
    AccountUserInfo User,
    AccountMeshConfig MeshConfig,
    string? ObservedCertThumbprint);

internal sealed record SyncProfileResult(
    AccountUserInfo User,
    AccountMeshConfig MeshConfig,
    string? ObservedCertThumbprint);

internal sealed record SyncCheckinRequest(
    string DeviceKey,
    string Name,
    string Hostname,
    string? LanIpv4,
    string? LanIpv6,
    bool SunshineReady,
    int SunshinePort,
    int AgentPort,
    bool SecurityPinRequired,
    string? GpuEncoder,
    string? OsVersion);

internal sealed record SyncCheckinResult(
    SyncedDeviceInfo Device,
    IReadOnlyList<SyncedDeviceInfo> Devices,
    long ServerTimeUtc,
    string? ObservedCertThumbprint);

internal sealed record SyncDevicesResult(
    IReadOnlyList<SyncedDeviceInfo> Devices,
    long ServerTimeUtc);

internal sealed record SyncSessionLeaseResult(
    bool Ok,
    bool UnsupportedByServer,
    string? ErrorCode,
    string? Message,
    IReadOnlyList<SyncedDeviceInfo>? Devices,
    long ServerTimeUtc);

internal sealed class SyncApiException : Exception
{
    public SyncApiException(int statusCode, string errorCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public int StatusCode { get; }
    public string ErrorCode { get; }
}

internal sealed class SyncCertificateMismatchException : Exception
{
    public SyncCertificateMismatchException(string expectedThumbprint, string actualThumbprint)
        : base($"同步服务器 TLS 证书指纹与本地已信任记录不一致（预期 {expectedThumbprint[..Math.Min(12, expectedThumbprint.Length)]}...，实际 {actualThumbprint[..Math.Min(12, actualThumbprint.Length)]}...）。如果服务器刚重装，请重新验证或清空信任指纹后登录。")
    {
        ExpectedThumbprint = expectedThumbprint;
        ActualThumbprint = actualThumbprint;
    }

    public string ExpectedThumbprint { get; }
    public string ActualThumbprint { get; }
}
