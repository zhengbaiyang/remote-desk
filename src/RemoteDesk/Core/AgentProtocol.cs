using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace RemoteDesk;

internal static class AgentIpcConstants
{
    public const string PipeName = "RemoteDeskAgentPipe";
    public const string SessionBridgePipeName = "RemoteDeskSessionBridgePipe";
    public const string ServiceName = "RemoteDeskAgent";
    public const string ServiceDisplayName = "RemoteDesk 后台守护服务";
    public const string ServiceDescription = "自动维护 EasyTier 虚拟网卡与 Sunshine 高刷被控端，支持无 UAC 与锁屏无人值守远控。";
    public const int DefaultAgentPort = 47999;

    public static string GetProgramDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteDesk");

    public static string GetAgentConfigPath() =>
        Path.Combine(GetProgramDataDirectory(), "agent-config.json");

    public static string GetAgentLogPath() =>
        Path.Combine(GetProgramDataDirectory(), "logs", "agent.log");
}

internal sealed record AgentConfigDocument
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EasyTierExecutablePath { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SunshineExecutablePath { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EasyTierConfig? EasyTier { get; init; }

    public bool HostModeEnabled { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceKey { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AccountId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProtectedAgentAuthKey { get; init; }

    public int AgentPort { get; init; } = AgentIpcConstants.DefaultAgentPort;

    public bool SecurityPinRequired { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SecurityPinSalt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SecurityPinHash { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SunshineAdminUser { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProtectedSunshineAdminPassword { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SyncServerUrl { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SyncServerCertThumbprint { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProtectedSyncSessionToken { get; init; }
}

internal sealed record AgentRuntimeStatus(
    bool ServiceRunning,
    bool IsSystemService,
    bool EasyTierRunning,
    string? EasyTierVirtualIpv4,
    IReadOnlyList<EasyTierPeerInfo> EasyTierPeers,
    string? EasyTierDetail,
    bool SunshineRunning,
    bool SunshineManagedReady,
    int AgentPort,
    bool HostModeEnabled,
    bool SecurityPinRequired,
    long? LastCheckinUtc,
    string? LastError);

internal sealed record AgentIpcRequest(
    string Command,
    string? PayloadJson = null);

internal sealed record AgentIpcResponse(
    bool Ok,
    string? PayloadJson = null,
    string? Error = null);

internal sealed record AgentApplyConfigPayload(
    string? EasyTierExecutablePath,
    string? SunshineExecutablePath,
    EasyTierConfig? EasyTier,
    bool HostModeEnabled,
    string? DeviceKey,
    string? DeviceName,
    string? AccountId,
    string? AgentAuthKey,
    int AgentPort,
    bool SecurityPinRequired,
    string? SecurityPinSalt,
    string? SecurityPinHash,
    string? SyncServerUrl,
    string? SyncServerCertThumbprint,
    string? SyncSessionToken);

internal sealed record AgentSetSecurityPinPayload(
    bool Required,
    string? NewPin);

internal sealed record AgentPeerInfoDto(
    string DeviceKey,
    string Hostname,
    bool SunshineReady,
    int SunshinePort,
    bool SecurityPinRequired);

internal sealed record AgentPairRequestDto(
    string Pin,
    string ClientName,
    string? SecurityPin = null);

internal sealed record AgentPairResponseDto(
    bool Ok,
    string? Error = null,
    string? Message = null);

internal sealed record AgentClipboardDto(
    long Sequence,
    string Kind,
    string? Text,
    string? ImageBase64,
    string? Sha256);

internal sealed record AgentClipboardSetRequestDto(
    string Kind,
    string? Text,
    string? ImageBase64);

internal sealed record AgentClipboardSetResponseDto(
    bool Ok,
    string? Sha256 = null,
    string? Error = null,
    string? Message = null);

internal sealed record AgentFileUploadResultDto(
    bool Ok,
    string? FileName = null,
    string? SavedPath = null,
    long SizeBytes = 0,
    string? Sha256 = null,
    string? Error = null,
    string? Message = null);

internal sealed record DisplayModeInfo(
    int Width,
    int Height,
    int RefreshRate);

internal sealed record AgentDisplayStateDto(
    DisplayModeInfo Current,
    IReadOnlyList<DisplayModeInfo> Supported,
    bool OverrideActive,
    DisplayModeInfo? Original = null);

internal sealed record AgentDisplayApplyRequestDto(
    int Width,
    int Height,
    int RefreshRate,
    string? SessionId = null);

internal sealed record AgentDisplayApplyResultDto(
    bool Ok,
    bool Changed,
    DisplayModeInfo? Applied = null,
    DisplayModeInfo? Original = null,
    string? Error = null,
    string? Message = null);

internal sealed record AgentPrivacyRequestDto(
    bool Enabled);

internal sealed record AgentPrivacyResultDto(
    bool Ok,
    bool Enabled,
    string? Error = null,
    string? Message = null);

internal sealed record AgentPowerRequestDto(
    string Action);

internal sealed record AgentPowerResultDto(
    bool Ok,
    string Action,
    string? Error = null,
    string? Message = null);

internal static class SecurityPinHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 100_000;

    public static (string SaltHex, string HashHex) HashPin(string pin)
    {
        if (string.IsNullOrWhiteSpace(pin) || pin.Length < 4 || pin.Length > 64 || pin.Any(char.IsControl))
            throw new ArgumentException("安全密码需为 4 到 64 个非控制字符。", nameof(pin));

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(pin),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashBytes);

        return (Convert.ToHexString(salt).ToLowerInvariant(), Convert.ToHexString(hash).ToLowerInvariant());
    }

    public static bool VerifyPin(string? pin, string? saltHex, string? expectedHashHex)
    {
        if (string.IsNullOrEmpty(pin) || string.IsNullOrEmpty(saltHex) || string.IsNullOrEmpty(expectedHashHex))
            return false;

        try
        {
            var salt = Convert.FromHexString(saltHex);
            var expected = Convert.FromHexString(expectedHashHex);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(pin),
                salt,
                Iterations,
                HashAlgorithmName.SHA256,
                expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

internal sealed class AgentHmacAuth
{
    public const string HeaderTimestamp = "X-RD-Timestamp";
    public const string HeaderNonce = "X-RD-Nonce";
    public const string HeaderSignature = "X-RD-Signature";
    public const string HeaderFileSha256 = "X-RD-File-Sha256";
    public const string HeaderFileName = "X-RD-File-Name";
    private const long MaxClockSkewSeconds = 60;

    private readonly ConcurrentDictionary<string, long> _seenNonces = new(StringComparer.Ordinal);

    public static string ComputeSignature(
        string authKey,
        string method,
        string path,
        long timestamp,
        string nonce,
        byte[] body)
    {
        var bodyHash = Convert.ToHexString(SHA256.HashData(body ?? [])).ToLowerInvariant();
        return ComputeSignatureFromBodyHash(authKey, method, path, timestamp, nonce, bodyHash);
    }

    public static string ComputeSignatureFromBodyHash(
        string authKey,
        string method,
        string path,
        long timestamp,
        string nonce,
        string bodySha256Hex)
    {
        ArgumentException.ThrowIfNullOrEmpty(authKey);
        ArgumentException.ThrowIfNullOrEmpty(method);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(nonce);
        ArgumentException.ThrowIfNullOrEmpty(bodySha256Hex);

        var canonical = $"{method.ToUpperInvariant()}\n{path}\n{timestamp}\n{nonce}\n{bodySha256Hex.Trim().ToLowerInvariant()}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(authKey));
        var mac = hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    public bool VerifySignature(
        string? authKey,
        string method,
        string path,
        long timestamp,
        string? nonce,
        byte[] body,
        string? providedSignatureHex,
        long? nowUnixSeconds = null)
    {
        var bodyHash = Convert.ToHexString(SHA256.HashData(body ?? [])).ToLowerInvariant();
        return VerifySignatureFromBodyHash(authKey, method, path, timestamp, nonce, bodyHash, providedSignatureHex, nowUnixSeconds);
    }

    public bool VerifySignatureFromBodyHash(
        string? authKey,
        string method,
        string path,
        long timestamp,
        string? nonce,
        string? bodySha256Hex,
        string? providedSignatureHex,
        long? nowUnixSeconds = null)
    {
        if (string.IsNullOrEmpty(authKey) || string.IsNullOrEmpty(nonce) || string.IsNullOrEmpty(bodySha256Hex) || string.IsNullOrEmpty(providedSignatureHex))
            return false;

        if (nonce.Length is < 8 or > 64)
            return false;

        var normalizedHash = bodySha256Hex.Trim().ToLowerInvariant();
        if (normalizedHash.Length != 64 || !normalizedHash.All(char.IsAsciiHexDigitLower))
            return false;

        var now = nowUnixSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - timestamp) > MaxClockSkewSeconds)
            return false;

        var expectedHex = ComputeSignatureFromBodyHash(authKey, method, path, timestamp, nonce, normalizedHash);
        byte[] expectedBytes;
        byte[] providedBytes;
        try
        {
            expectedBytes = Convert.FromHexString(expectedHex);
            providedBytes = Convert.FromHexString(providedSignatureHex.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        if (expectedBytes.Length != providedBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes))
        {
            return false;
        }

        PruneExpiredNonces(now);
        return _seenNonces.TryAdd(nonce, now);
    }

    private void PruneExpiredNonces(long now)
    {
        if (_seenNonces.Count < 256)
            return;

        var cutoff = now - MaxClockSkewSeconds * 2;
        foreach (var pair in _seenNonces)
        {
            if (pair.Value < cutoff)
                _seenNonces.TryRemove(pair.Key, out _);
        }
    }
}
