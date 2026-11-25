namespace RemoteDesk;

internal enum DeviceRemoteControlStatus
{
    Idle,
    BeingControlled,
    Controlling,
}

internal sealed record PeerSessionClaimDecision(
    bool Ok,
    string? ErrorCode,
    string Message);

internal sealed record PeerSessionSnapshot(
    bool IsBeingControlled,
    bool IsControllingOther,
    string? ControlledByDeviceKey,
    string? ControlledByName,
    string? ActiveSessionId);

internal sealed class PeerSessionLeaseTracker
{
    public const long DefaultLeaseTtlSeconds = 45;

    private readonly object _sync = new();
    private readonly long _leaseTtlSeconds;

    private bool _isControllingOther;
    private ActiveIncomingLease? _activeIncoming;

    public PeerSessionLeaseTracker(long leaseTtlSeconds = DefaultLeaseTtlSeconds)
    {
        _leaseTtlSeconds = leaseTtlSeconds > 0 ? leaseTtlSeconds : DefaultLeaseTtlSeconds;
    }

    public void SetControllingOther(bool isControlling)
    {
        lock (_sync)
        {
            _isControllingOther = isControlling;
        }
    }

    public PeerSessionClaimDecision TryClaim(
        string sessionId,
        string controllerDeviceKey,
        string controllerName,
        long? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return new PeerSessionClaimDecision(false, "INVALID_SESSION", "远控会话标识无效。");

        var now = nowUtc ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cleanName = string.IsNullOrWhiteSpace(controllerName) ? controllerDeviceKey : controllerName.Trim();

        lock (_sync)
        {
            PruneExpiredLocked(now);

            if (_isControllingOther)
                return new PeerSessionClaimDecision(false, "HOST_IS_CONTROLLING", "目标设备当前正在远控其他设备，暂不可被远控。");

            if (_activeIncoming is { } active && !string.Equals(active.SessionId, sessionId, StringComparison.Ordinal))
            {
                return new PeerSessionClaimDecision(
                    false,
                    "HOST_BEING_CONTROLLED",
                    $"目标设备当前正被「{active.ControllerName}」远控，暂不可连接。");
            }

            _activeIncoming = new ActiveIncomingLease(
                sessionId,
                controllerDeviceKey ?? "",
                string.IsNullOrWhiteSpace(cleanName) ? "其他设备" : cleanName,
                now);

            return new PeerSessionClaimDecision(true, null, "已锁定远控会话。");
        }
    }

    public void Release(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        lock (_sync)
        {
            if (_activeIncoming is { } active && string.Equals(active.SessionId, sessionId, StringComparison.Ordinal))
                _activeIncoming = null;
        }
    }

    public PeerSessionSnapshot GetSnapshot(long? nowUtc = null)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_sync)
        {
            PruneExpiredLocked(now);
            return new PeerSessionSnapshot(
                IsBeingControlled: _activeIncoming is not null,
                IsControllingOther: _isControllingOther,
                ControlledByDeviceKey: _activeIncoming?.ControllerDeviceKey,
                ControlledByName: _activeIncoming?.ControllerName,
                ActiveSessionId: _activeIncoming?.SessionId);
        }
    }

    private void PruneExpiredLocked(long nowUtc)
    {
        if (_activeIncoming is { } active && nowUtc - active.LastHeartbeatUtc > _leaseTtlSeconds)
            _activeIncoming = null;
    }

    private sealed record ActiveIncomingLease(
        string SessionId,
        string ControllerDeviceKey,
        string ControllerName,
        long LastHeartbeatUtc);
}

internal static class RemoteControlLockPolicy
{
    public static DeviceRemoteControlStatus ParseStatus(string? rawState)
    {
        if (string.Equals(rawState, "controlled", StringComparison.OrdinalIgnoreCase))
            return DeviceRemoteControlStatus.BeingControlled;
        if (string.Equals(rawState, "controlling", StringComparison.OrdinalIgnoreCase))
            return DeviceRemoteControlStatus.Controlling;
        return DeviceRemoteControlStatus.Idle;
    }

    public static bool CanInitiateConnection(
        DeviceRemoteControlStatus targetStatus,
        bool isControlledByLocal,
        out string? blockReason)
    {
        if (targetStatus == DeviceRemoteControlStatus.BeingControlled && !isControlledByLocal)
        {
            blockReason = "该设备当前正被其他设备远控，暂不可连接。";
            return false;
        }

        if (targetStatus == DeviceRemoteControlStatus.Controlling)
        {
            blockReason = "该设备当前正在远控其他设备，期间不可被远控。";
            return false;
        }

        blockReason = null;
        return true;
    }
}
