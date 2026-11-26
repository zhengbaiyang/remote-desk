namespace RemoteDesk;

internal enum PeerAddState
{
    CanAdd,
    IsSelf,
    RelayOnly,
    WaitingIp,
    AlreadySynced,
    AlreadyLocal,
}

internal sealed record PeerClassification(
    PeerAddState State,
    string? ExistingDeviceUniqueKey,
    string? ExistingDeviceName);

internal static class DeviceIdentityHelper
{
    public const int MaxDeviceNameLength = 40;

    public static string NormalizeDeviceName(string? rawName)
    {
        if (rawName is null)
            throw new ArgumentException("请输入 1 到 40 个字符的设备名称。", nameof(rawName));

        var trimmed = rawName.Trim();
        if (trimmed.Length is < 1 or > MaxDeviceNameLength || trimmed.Any(char.IsControl))
            throw new ArgumentException("请输入 1 到 40 个字符的设备名称（不能包含控制字符）。", nameof(rawName));

        return trimmed;
    }

    public static string GetEffectiveLocalDeviceName(string? configuredLocalName, string? osHostName)
    {
        if (!string.IsNullOrWhiteSpace(configuredLocalName))
            return configuredLocalName.Trim();
        if (!string.IsNullOrWhiteSpace(osHostName))
            return osHostName.Trim();
        return "本机";
    }

    public static string ResolveSyncedDeviceDisplayName(
        SyncedDeviceInfo device,
        IReadOnlyDictionary<string, string>? aliases,
        string localDeviceKey,
        string? configuredLocalName)
    {
        if (!string.IsNullOrWhiteSpace(localDeviceKey) &&
            string.Equals(device.DeviceKey, localDeviceKey, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(configuredLocalName))
        {
            return configuredLocalName.Trim();
        }

        if (aliases is not null &&
            aliases.TryGetValue(device.DeviceKey, out var alias) &&
            !string.IsNullOrWhiteSpace(alias))
        {
            return alias.Trim();
        }

        return device.Name;
    }

    public static bool IsLocalHostAddress(
        string? hostAddress,
        string? localVirtualIpv4,
        IEnumerable<string>? localLanIpv4s,
        IEnumerable<string>? localLanIpv6s,
        string? localHostName)
    {
        if (string.IsNullOrWhiteSpace(hostAddress))
            return false;

        var candidate = hostAddress.Trim();
        if (string.Equals(candidate, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate, "::1", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("127.", StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(localVirtualIpv4) &&
            string.Equals(candidate, localVirtualIpv4.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(localHostName) &&
            string.Equals(candidate, localHostName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (localLanIpv4s is not null &&
            localLanIpv4s.Any(ip => string.Equals(ip?.Trim(), candidate, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (localLanIpv6s is not null &&
            localLanIpv6s.Any(ip => string.Equals(ip?.Trim(), candidate, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    public static bool IsLocalPeer(
        EasyTierPeerInfo peer,
        string? localVirtualIpv4,
        string? assignedVirtualIpv4,
        string? localHostName)
    {
        if (peer.IsSelf)
            return true;

        var peerIp = peer.VirtualIpv4?.Trim();
        if (!string.IsNullOrWhiteSpace(peerIp))
        {
            if (!string.IsNullOrWhiteSpace(localVirtualIpv4) &&
                string.Equals(peerIp, localVirtualIpv4.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!string.IsNullOrWhiteSpace(assignedVirtualIpv4) &&
                string.Equals(peerIp, assignedVirtualIpv4.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (peer.LatencyMs is null &&
            string.IsNullOrWhiteSpace(peer.Protocol) &&
            !string.IsNullOrWhiteSpace(localHostName) &&
            string.Equals(peer.Hostname?.Trim(), EasyTierManager.SanitizeHostname(localHostName), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static PeerClassification ClassifyPeer(
        EasyTierPeerInfo peer,
        bool isSelf,
        IReadOnlyList<SyncedDeviceInfo> syncedDevices,
        IReadOnlyList<DeviceProfile> localDevices)
    {
        if (isSelf)
            return new PeerClassification(PeerAddState.IsSelf, null, null);

        if (EasyTierManager.IsRelayOnlyPeer(peer))
            return new PeerClassification(PeerAddState.RelayOnly, null, null);

        if (string.IsNullOrWhiteSpace(peer.VirtualIpv4))
            return new PeerClassification(PeerAddState.WaitingIp, null, null);

        var peerIp = peer.VirtualIpv4.Trim();

        foreach (var synced in syncedDevices)
        {
            if ((!string.IsNullOrWhiteSpace(synced.VirtualIpv4) &&
                 string.Equals(synced.VirtualIpv4.Trim(), peerIp, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(synced.LanIpv4) &&
                 string.Equals(synced.LanIpv4.Trim(), peerIp, StringComparison.OrdinalIgnoreCase)))
            {
                return new PeerClassification(PeerAddState.AlreadySynced, $"cloud:{synced.Id}", synced.Name);
            }
        }

        foreach (var local in localDevices)
        {
            if (string.Equals(local.HostAddress, peerIp, StringComparison.OrdinalIgnoreCase))
            {
                return new PeerClassification(PeerAddState.AlreadyLocal, $"local:{local.Name}:{local.HostAddress}", local.Name);
            }
        }

        return new PeerClassification(PeerAddState.CanAdd, null, null);
    }
}
