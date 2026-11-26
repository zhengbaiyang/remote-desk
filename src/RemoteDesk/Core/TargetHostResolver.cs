using System.Net;
using System.Net.Sockets;

namespace RemoteDesk;

internal sealed record TargetHostCandidate(string Host, bool IsLanFallback);

internal sealed record TargetHostResolution(
    bool Succeeded,
    string? ResolvedHost,
    bool UsedLanFallback,
    AgentPeerInfoDto? PeerInfo,
    string? FailureMessage,
    IReadOnlyList<string> TriedHosts);

internal static class TargetHostResolver
{
    private static readonly TimeSpan DefaultLanProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultReachableVirtualProbeTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan DefaultUnreachableVirtualProbeTimeout = TimeSpan.FromSeconds(3.5);

    public static IReadOnlyList<TargetHostCandidate> BuildCandidates(
        SyncedDeviceInfo synced,
        IReadOnlyList<EasyTierPeerInfo>? easyTierPeers)
    {
        ArgumentNullException.ThrowIfNull(synced);

        var virtualIp = NormalizeCandidateHost(synced.VirtualIpv4);
        var lanIp = NormalizeCandidateHost(synced.LanIpv4);

        var hasVirtual = !string.IsNullOrEmpty(virtualIp);
        var hasLan = !string.IsNullOrEmpty(lanIp) &&
                     !string.Equals(lanIp, virtualIp, StringComparison.OrdinalIgnoreCase);

        var virtualPeerReachable = hasVirtual &&
                                   easyTierPeers is not null &&
                                   easyTierPeers.Any(p =>
                                       !p.IsSelf &&
                                       string.Equals(p.VirtualIpv4?.Trim(), virtualIp, StringComparison.OrdinalIgnoreCase));

        var candidates = new List<TargetHostCandidate>(2);
        if (virtualPeerReachable)
        {
            candidates.Add(new TargetHostCandidate(virtualIp!, IsLanFallback: false));
            if (hasLan)
                candidates.Add(new TargetHostCandidate(lanIp!, IsLanFallback: true));
        }
        else if (hasLan)
        {
            // When the EasyTier peer is not currently visible in the mesh, probe the synced LAN IP first
            // so same-LAN / campus-LAN connections succeed in milliseconds without waiting on a dead tunnel.
            candidates.Add(new TargetHostCandidate(lanIp!, IsLanFallback: true));
            if (hasVirtual)
                candidates.Add(new TargetHostCandidate(virtualIp!, IsLanFallback: false));
        }
        else if (hasVirtual)
        {
            candidates.Add(new TargetHostCandidate(virtualIp!, IsLanFallback: false));
        }

        return candidates;
    }

    public static async Task<TargetHostResolution> ResolveAsync(
        SyncedDeviceInfo synced,
        IReadOnlyList<EasyTierPeerInfo>? easyTierPeers,
        int agentPort,
        string agentAuthKey,
        AgentPeerClient peerClient,
        CancellationToken ct = default,
        TimeSpan? virtualProbeTimeout = null,
        TimeSpan? lanProbeTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(synced);
        ArgumentNullException.ThrowIfNull(peerClient);

        var candidates = BuildCandidates(synced, easyTierPeers);
        if (candidates.Count == 0)
        {
            return new TargetHostResolution(
                Succeeded: false,
                ResolvedHost: null,
                UsedLanFallback: false,
                PeerInfo: null,
                FailureMessage: "该设备尚未分配有效地址，请等待目标设备在线同步。",
                TriedHosts: []);
        }

        var virtualIp = NormalizeCandidateHost(synced.VirtualIpv4);
        var virtualPeerReachable = !string.IsNullOrEmpty(virtualIp) &&
                                   easyTierPeers is not null &&
                                   easyTierPeers.Any(p =>
                                       !p.IsSelf &&
                                       string.Equals(p.VirtualIpv4?.Trim(), virtualIp, StringComparison.OrdinalIgnoreCase));
        var hasAnyRemoteMeshPeer = easyTierPeers is not null && easyTierPeers.Any(p => !p.IsSelf);

        var triedHosts = new List<string>(candidates.Count);
        string? lastError = null;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            triedHosts.Add(candidate.Host);

            var timeout = candidate.IsLanFallback
                ? (lanProbeTimeout ?? DefaultLanProbeTimeout)
                : (virtualProbeTimeout ?? (virtualPeerReachable ? DefaultReachableVirtualProbeTimeout : DefaultUnreachableVirtualProbeTimeout));

            var (ok, info, err) = await peerClient
                .GetInfoAsync(candidate.Host, agentPort, agentAuthKey, ct, timeout)
                .ConfigureAwait(false);

            if (!ok || info is null)
            {
                lastError = err;
                continue;
            }

            if (candidate.IsLanFallback &&
                !string.IsNullOrWhiteSpace(synced.DeviceKey) &&
                !string.IsNullOrWhiteSpace(info.DeviceKey) &&
                !string.Equals(info.DeviceKey, synced.DeviceKey, StringComparison.OrdinalIgnoreCase))
            {
                lastError = $"局域网地址 {candidate.Host} 响应设备 ({info.Hostname}) 与目标设备标识不匹配，已跳过。";
                continue;
            }

            return new TargetHostResolution(
                Succeeded: true,
                ResolvedHost: candidate.Host,
                UsedLanFallback: candidate.IsLanFallback,
                PeerInfo: info,
                FailureMessage: null,
                TriedHosts: triedHosts);
        }

        string failureMessage;
        if (!hasAnyRemoteMeshPeer && !string.IsNullOrWhiteSpace(synced.VirtualIpv4))
        {
            var detailSuffix = string.IsNullOrWhiteSpace(lastError) ? "" : $"（{lastError}）";
            failureMessage =
                $"无法连接目标节点「{synced.Name}」：当前 EasyTier 虚拟内网尚未连上任何远端节点（可能原因：中继端口 11010 被网络拦截或代理软件虚拟网卡分流干扰），且局域网直连不可达{detailSuffix}。请在「网络节点」重新同步以启用 WS 备用通道，或将代理改为绕过大陆/局域网模式后重试。";
        }
        else
        {
            failureMessage = lastError ?? $"无法连接目标节点「{synced.Name}」的控制服务。";
        }

        return new TargetHostResolution(
            Succeeded: false,
            ResolvedHost: null,
            UsedLanFallback: false,
            PeerInfo: null,
            FailureMessage: failureMessage,
            TriedHosts: triedHosts);
    }

    private static string? NormalizeCandidateHost(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim();
        var slashIndex = trimmed.IndexOf('/');
        if (slashIndex >= 0)
            trimmed = trimmed[..slashIndex].Trim();

        if (string.IsNullOrEmpty(trimmed))
            return null;

        if (IPAddress.TryParse(trimmed, out var ip))
        {
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
                return null;

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = ip.GetAddressBytes();
                if (bytes[0] == 127 || bytes[0] == 0 || (bytes[0] == 169 && bytes[1] == 254))
                    return null;
            }
            else if (ip.IsIPv6LinkLocal)
            {
                return null;
            }
        }

        try
        {
            return DeviceProfile.ValidateHostAddress(trimmed, nameof(raw));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
