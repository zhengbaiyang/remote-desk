using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace RemoteDesk;

internal static class EasyTierManager
{
    public const string CoreExecutableName = "easytier-core.exe";
    public const string CliExecutableName = "easytier-cli.exe";
    public const string DefaultRpcPortal = "127.0.0.1:15888";

    public const string ConnectionTypeSelf = "本机";
    public const string ConnectionTypeP2P = "直连 (P2P)";
    public const string ConnectionTypeRelay = "中继 (Relay)";
    public const string ConnectionTypeUnknown = "未知";

    private const string CoreProcessName = "easytier-core";
    private const string FallbackHostname = "remotedesk-node";
    private const int MaxHostnameLength = 63;
    private static readonly TimeSpan CliQueryTimeout = TimeSpan.FromSeconds(2);

    public static string? FindCoreExecutable(string? selectedPath, string? managedPath = null)
    {
        if (IsExecutable(selectedPath))
            return selectedPath;

        if (IsExecutable(managedPath))
            return managedPath;

        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EasyTier", CoreExecutableName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "EasyTier", CoreExecutableName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "EasyTier", CoreExecutableName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteDesk", "engines", "easytier", CoreExecutableName),
            Path.Combine(AppContext.BaseDirectory, CoreExecutableName),
        ];

        var standard = candidates.FirstOrDefault(IsExecutable);
        if (standard is not null)
            return standard;

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var rawDir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var dir = rawDir.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(dir))
                    continue;

                try
                {
                    var candidate = Path.Combine(dir, CoreExecutableName);
                    if (IsExecutable(candidate))
                        return candidate;
                }
                catch (ArgumentException)
                {
                    // Skip malformed PATH entries.
                }
            }
        }

        return null;
    }

    public static string? FindCliExecutable(string? coreExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(coreExecutablePath))
            return null;

        try
        {
            var directory = Path.GetDirectoryName(coreExecutablePath);
            if (string.IsNullOrWhiteSpace(directory))
                return null;

            var cliPath = Path.Combine(directory, CliExecutableName);
            return IsExecutable(cliPath) ? cliPath : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static ProcessStartInfo CreateCoreStartInfo(string coreExecutable, EasyTierConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!IsExecutable(coreExecutable))
            throw new FileNotFoundException("找不到 EasyTier 核心程序 (easytier-core.exe)。", coreExecutable);

        var validated = EasyTierConfig.Validate(config);

        var startInfo = new ProcessStartInfo(coreExecutable)
        {
            WorkingDirectory = Path.GetDirectoryName(coreExecutable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("--network-name");
        startInfo.ArgumentList.Add(validated.NetworkName);
        startInfo.ArgumentList.Add("--network-secret");
        startInfo.ArgumentList.Add(validated.NetworkSecret);

        if (validated.AutoAssignIpv4 || string.IsNullOrEmpty(validated.VirtualIpv4))
        {
            startInfo.ArgumentList.Add("-d");
        }
        else
        {
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(validated.VirtualIpv4);
        }

        foreach (var peerUrl in validated.GetEffectivePeerUrls())
        {
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add(peerUrl);
        }

        startInfo.ArgumentList.Add("--hostname");
        startInfo.ArgumentList.Add(SanitizeHostname(Environment.MachineName));
        startInfo.ArgumentList.Add("--rpc-portal");
        startInfo.ArgumentList.Add(DefaultRpcPortal);

        return startInfo;
    }

    public static ProcessStartInfo CreateElevatedCoreStartInfo(string coreExecutable, EasyTierConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!IsExecutable(coreExecutable))
            throw new FileNotFoundException("找不到 EasyTier 核心程序 (easytier-core.exe)。", coreExecutable);

        var validated = EasyTierConfig.Validate(config);
        var args = new List<string>
        {
            "--network-name",
            validated.NetworkName,
            "--network-secret",
            validated.NetworkSecret,
        };

        if (validated.AutoAssignIpv4 || string.IsNullOrEmpty(validated.VirtualIpv4))
        {
            args.Add("-d");
        }
        else
        {
            args.Add("-i");
            args.Add(validated.VirtualIpv4);
        }

        foreach (var peerUrl in validated.GetEffectivePeerUrls())
        {
            args.Add("-p");
            args.Add(peerUrl);
        }

        args.Add("--hostname");
        args.Add(SanitizeHostname(Environment.MachineName));
        args.Add("--rpc-portal");
        args.Add(DefaultRpcPortal);

        return new ProcessStartInfo(coreExecutable)
        {
            WorkingDirectory = Path.GetDirectoryName(coreExecutable)!,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = string.Join(' ', args.Select(QuoteArgument)),
        };
    }

    public static bool IsMeshRunning()
    {
        var processes = Process.GetProcessesByName(CoreProcessName);
        var running = processes.Length > 0;
        foreach (var process in processes)
            process.Dispose();
        return running;
    }

    public static bool StartMesh(string coreExecutable, EasyTierConfig config)
    {
        var startInfo = CreateCoreStartInfo(coreExecutable, config);
        if (IsMeshRunning())
            return true;

        using var process = Process.Start(startInfo);
        return process is not null;
    }

    public static bool StartMeshElevated(string coreExecutable, EasyTierConfig config)
    {
        var startInfo = CreateElevatedCoreStartInfo(coreExecutable, config);
        if (IsMeshRunning())
            return true;

        using var process = Process.Start(startInfo);
        return process is not null;
    }

    public static bool IsRelayOnlyPeer(EasyTierPeerInfo peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        if (peer.IsSelf || !string.IsNullOrWhiteSpace(peer.VirtualIpv4))
            return false;

        return peer.Hostname.Contains("relay", StringComparison.OrdinalIgnoreCase) ||
               peer.Hostname.Contains("server", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCurrentProcessElevated()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        const uint TokenQuery = 0x0008;
        const uint TokenElevationClass = 20;

        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var tokenHandle))
            return false;

        try
        {
            var size = Marshal.SizeOf<int>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(tokenHandle, TokenElevationClass, buffer, size, out _))
                    return false;

                return Marshal.ReadInt32(buffer) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(tokenHandle);
        }
    }

    public static bool StopMesh()
    {
        var allStopped = true;
        foreach (var process in Process.GetProcessesByName(CoreProcessName))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited)
                        continue;

                    process.Kill(entireProcessTree: false);
                    process.WaitForExit(1500);
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    allStopped = false;
                }
            }
        }

        return allStopped && !IsMeshRunning();
    }

    public static async Task<EasyTierStatus> QueryStatusAsync(
        string? coreExecutable,
        EasyTierConfig config,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var resolvedCore = IsExecutable(coreExecutable) ? coreExecutable : null;
        var installed = resolvedCore is not null;
        var running = IsMeshRunning();
        var networkName = string.IsNullOrWhiteSpace(config.NetworkName) ? null : config.NetworkName;
        var peerUrl = string.IsNullOrWhiteSpace(config.PeerUrl) ? null : config.PeerUrl;

        if (!installed && !running)
        {
            return new EasyTierStatus(
                Installed: false,
                Running: false,
                CoreExecutablePath: null,
                VirtualIpv4: null,
                NetworkName: networkName,
                PeerUrl: peerUrl,
                Peers: [],
                DetailMessage: "尚未安装 EasyTier 引擎");
        }

        if (!running)
        {
            return new EasyTierStatus(
                Installed: installed,
                Running: false,
                CoreExecutablePath: resolvedCore,
                VirtualIpv4: null,
                NetworkName: networkName,
                PeerUrl: peerUrl,
                Peers: [],
                DetailMessage: "EasyTier 组网未启动");
        }

        var cliPath = FindCliExecutable(resolvedCore);
        if (cliPath is null)
        {
            return new EasyTierStatus(
                Installed: installed,
                Running: true,
                CoreExecutablePath: resolvedCore,
                VirtualIpv4: null,
                NetworkName: networkName,
                PeerUrl: peerUrl,
                Peers: [],
                DetailMessage: "EasyTier 运行中（未找到 easytier-cli.exe，无法读取节点列表）");
        }

        var startInfo = new ProcessStartInfo(cliPath)
        {
            WorkingDirectory = Path.GetDirectoryName(cliPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(DefaultRpcPortal);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("peer");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new EasyTierStatus(
                    Installed: installed,
                    Running: true,
                    CoreExecutablePath: resolvedCore,
                    VirtualIpv4: null,
                    NetworkName: networkName,
                    PeerUrl: peerUrl,
                    Peers: [],
                    DetailMessage: "EasyTier 运行中，但无法启动节点查询工具");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(CliQueryTimeout);

            try
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                var stdout = await stdoutTask.ConfigureAwait(false);
                _ = await stderrTask.ConfigureAwait(false);

                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                {
                    return new EasyTierStatus(
                        Installed: installed,
                        Running: true,
                        CoreExecutablePath: resolvedCore,
                        VirtualIpv4: null,
                        NetworkName: networkName,
                        PeerUrl: peerUrl,
                        Peers: [],
                        DetailMessage: "EasyTier 运行中，正在等待节点同步...");
                }

                var peers = ParsePeersJson(stdout);
                var localIpv4 = TryExtractLocalIpv4(peers);
                var remotePeerCount = peers.Count(p => !p.IsSelf);
                var needsElevation = localIpv4 is null && !IsCurrentProcessElevated();
                var detail = localIpv4 is not null
                    ? (remotePeerCount == 0 && !string.IsNullOrEmpty(peerUrl)
                        ? $"虚拟网卡就绪（本机 {localIpv4}），但暂未连上中继或远端节点，请检查网络/代理或等待 WS 备用通道重连"
                        : $"已连接虚拟网段（本机 {localIpv4}，共 {peers.Count} 个节点）")
                    : needsElevation
                        ? "EasyTier 运行中但未能分配虚拟 IP：Windows 创建虚拟网卡需要管理员权限，请点击「启用后台守护服务」修复。"
                        : peers.Count > 0
                            ? $"EasyTier 运行中（发现 {peers.Count} 个节点，正在等待虚拟 IP 就绪）"
                            : "EasyTier 运行中，等待节点加入";

                return new EasyTierStatus(
                    Installed: installed,
                    Running: true,
                    CoreExecutablePath: resolvedCore,
                    VirtualIpv4: localIpv4,
                    NetworkName: networkName,
                    PeerUrl: peerUrl,
                    Peers: peers,
                    DetailMessage: detail,
                    NeedsElevationWarning: needsElevation);
            }
            catch (OperationCanceledException)
            {
                TryKillProcess(process);
                return new EasyTierStatus(
                    Installed: installed,
                    Running: true,
                    CoreExecutablePath: resolvedCore,
                    VirtualIpv4: null,
                    NetworkName: networkName,
                    PeerUrl: peerUrl,
                    Peers: [],
                    DetailMessage: "EasyTier 运行中（节点状态查询超时）");
            }
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            return new EasyTierStatus(
                Installed: installed,
                Running: true,
                CoreExecutablePath: resolvedCore,
                VirtualIpv4: null,
                NetworkName: networkName,
                PeerUrl: peerUrl,
                Peers: [],
                DetailMessage: "EasyTier 运行中（读取节点状态失败）");
        }
    }

    public static IReadOnlyList<EasyTierPeerInfo> ParsePeersJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement arrayElement;

            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                arrayElement = document.RootElement;
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Object &&
                     document.RootElement.TryGetProperty("peers", out var peersProp) &&
                     peersProp.ValueKind == JsonValueKind.Array)
            {
                arrayElement = peersProp;
            }
            else
            {
                return [];
            }

            var peers = new List<EasyTierPeerInfo>();
            foreach (var item in arrayElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var hostname = NormalizeDashString(
                    ReadStringProperty(item, "hostname") ??
                    ReadStringProperty(item, "host_name") ??
                    ReadStringProperty(item, "name"));

                var rawIpv4 = NormalizeDashString(
                    ReadStringProperty(item, "ipv4") ??
                    ReadStringProperty(item, "virtual_ipv4") ??
                    ReadStringProperty(item, "virtualIpv4"));
                var virtualIpv4 = StripCidrSuffix(rawIpv4);

                var (connectionType, isSelf) = ParseConnectionType(item);
                var latencyMs = ParseLatencyMs(item);
                var protocol = NormalizeDashString(
                    ReadStringProperty(item, "tunnel_proto") ??
                    ReadStringProperty(item, "protocol") ??
                    ReadStringProperty(item, "proto"));

                peers.Add(new EasyTierPeerInfo(
                    Hostname: hostname,
                    VirtualIpv4: virtualIpv4,
                    ConnectionType: connectionType,
                    LatencyMs: latencyMs,
                    Protocol: protocol,
                    IsSelf: isSelf));
            }

            return peers;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string? TryExtractLocalIpv4(IReadOnlyList<EasyTierPeerInfo> peers)
    {
        if (peers is null || peers.Count == 0)
            return null;

        foreach (var peer in peers)
        {
            if (!peer.IsSelf)
                continue;

            var ip = StripCidrSuffix(NormalizeDashString(peer.VirtualIpv4));
            if (!string.IsNullOrEmpty(ip))
                return ip;
        }

        return null;
    }

    internal static string SanitizeHostname(string? rawHostname)
    {
        if (string.IsNullOrWhiteSpace(rawHostname))
            return FallbackHostname;

        var builder = new StringBuilder(Math.Min(rawHostname.Length, MaxHostnameLength));
        var previousWasDash = false;

        foreach (var ch in rawHostname.Trim())
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '_' or '.')
            {
                builder.Append(ch);
                previousWasDash = false;
            }
            else if (ch == '-' || char.IsWhiteSpace(ch))
            {
                if (builder.Length > 0 && !previousWasDash)
                {
                    builder.Append('-');
                    previousWasDash = true;
                }
            }

            if (builder.Length >= MaxHostnameLength)
                break;
        }

        var cleaned = builder.ToString().Trim('-', '.');
        if (string.IsNullOrEmpty(cleaned) || cleaned[0] == '-')
            return FallbackHostname;

        return cleaned;
    }

    private static (string ConnectionType, bool IsSelf) ParseConnectionType(JsonElement item)
    {
        if (item.TryGetProperty("is_self", out var isSelfProp) && isSelfProp.ValueKind == JsonValueKind.True)
            return (ConnectionTypeSelf, true);

        if (item.TryGetProperty("cost", out var costProp))
        {
            if (costProp.ValueKind == JsonValueKind.String)
            {
                var costText = costProp.GetString()?.Trim() ?? "";
                return ClassifyCostString(costText);
            }

            if (costProp.ValueKind == JsonValueKind.Number && costProp.TryGetInt32(out var costNum))
            {
                return costNum switch
                {
                    0 => (ConnectionTypeSelf, true),
                    1 => (ConnectionTypeP2P, false),
                    > 1 => (ConnectionTypeRelay, false),
                    _ => (ConnectionTypeUnknown, false),
                };
            }
        }

        var fallbackType = ReadStringProperty(item, "connection_type") ?? ReadStringProperty(item, "type");
        if (!string.IsNullOrWhiteSpace(fallbackType))
            return ClassifyCostString(fallbackType.Trim());

        return (ConnectionTypeUnknown, false);
    }

    private static (string ConnectionType, bool IsSelf) ClassifyCostString(string costText)
    {
        if (string.IsNullOrEmpty(costText) || costText == "-")
            return (ConnectionTypeUnknown, false);

        if (costText.Equals("local", StringComparison.OrdinalIgnoreCase) ||
            costText.Equals("self", StringComparison.OrdinalIgnoreCase) ||
            costText.Equals(ConnectionTypeSelf, StringComparison.Ordinal))
        {
            return (ConnectionTypeSelf, true);
        }

        if (costText.Contains("p2p", StringComparison.OrdinalIgnoreCase) ||
            costText.Contains("direct", StringComparison.OrdinalIgnoreCase) ||
            costText.Contains("直连", StringComparison.Ordinal))
        {
            return (ConnectionTypeP2P, false);
        }

        if (costText.Contains("relay", StringComparison.OrdinalIgnoreCase) ||
            costText.Contains("中继", StringComparison.Ordinal))
        {
            return (ConnectionTypeRelay, false);
        }

        if (int.TryParse(costText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            return numeric switch
            {
                0 => (ConnectionTypeSelf, true),
                1 => (ConnectionTypeP2P, false),
                > 1 => (ConnectionTypeRelay, false),
                _ => (ConnectionTypeUnknown, false),
            };
        }

        return (ConnectionTypeUnknown, false);
    }

    private static double? ParseLatencyMs(JsonElement item)
    {
        foreach (var propName in new[] { "lat_ms", "latency_ms", "latencyMs" })
        {
            if (!item.TryGetProperty(propName, out var prop))
                continue;

            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var num))
                return double.IsFinite(num) && num >= 0 ? num : null;

            if (prop.ValueKind == JsonValueKind.String)
            {
                var raw = prop.GetString()?.Trim();
                if (string.IsNullOrEmpty(raw) || raw == "-")
                    return null;

                if (raw.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
                    raw = raw[..^2].Trim();

                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
                    double.IsFinite(parsed) && parsed >= 0)
                {
                    return parsed;
                }

                return null;
            }
        }

        return null;
    }

    private static string? ReadStringProperty(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var prop))
            return null;

        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    private static string NormalizeDashString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var trimmed = value.Trim();
        return trimmed == "-" ? "" : trimmed;
    }

    private static string StripCidrSuffix(string ipv4)
    {
        if (string.IsNullOrEmpty(ipv4))
            return "";

        var slashIndex = ipv4.IndexOf('/');
        return slashIndex >= 0 ? ipv4[..slashIndex].Trim() : ipv4;
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Ignore best-effort cleanup errors.
        }
    }

    private static string QuoteArgument(string value) =>
        value.Contains(' ') || value.Contains('\t') ? $"\"{value}\"" : value;

    private static bool IsExecutable(string? path) =>
        path is not null &&
        Path.IsPathFullyQualified(path) &&
        string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) &&
        File.Exists(path);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        uint tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
