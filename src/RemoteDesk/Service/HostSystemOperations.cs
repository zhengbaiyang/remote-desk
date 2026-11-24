using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace RemoteDesk;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class HostSystemOperations : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan BridgeIoTimeout = TimeSpan.FromSeconds(5);

    private readonly bool _isSystemService;
    private readonly SemaphoreSlim _bridgeLaunchLock = new(1, 1);
    private readonly Action<string>? _logger;

    public HostSystemOperations(bool isSystemService, Action<string>? logger = null)
    {
        _isSystemService = isSystemService;
        _logger = logger;
    }

    public AgentPeerFeatureHandlers CreatePeerFeatureHandlers() => new(
        GetClipboardAsync: GetClipboardAsync,
        SetClipboardAsync: SetClipboardAsync,
        ResolveUploadDirectoryAsync: ResolveUploadDirectoryAsync,
        GetDisplayStateAsync: GetDisplayStateAsync,
        ApplyDisplayModeAsync: ApplyDisplayModeAsync,
        RestoreDisplayModeAsync: RestoreDisplayModeAsync,
        SetPrivacyScreenAsync: SetPrivacyScreenAsync,
        ExecutePowerActionAsync: ExecutePowerActionAsync);

    public async Task CleanupOnStopAsync()
    {
        try
        {
            await SendBridgeDirectOnceAsync(
                new SessionBridgeRequest("privacy_set", JsonSerializer.Serialize(new AgentPrivacyRequestDto(false), JsonOptions)),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            await SendBridgeDirectOnceAsync(new SessionBridgeRequest("display_restore"), CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    public void Dispose() => _bridgeLaunchLock.Dispose();

    private async Task<AgentClipboardDto> GetClipboardAsync(CancellationToken ct)
    {
        var resp = await SendToSessionBridgeAsync(new SessionBridgeRequest("clipboard_get"), ct).ConfigureAwait(false);
        return TryDeserializePayload<AgentClipboardDto>(resp.PayloadJson)
            ?? new AgentClipboardDto(0, "none", null, null, null);
    }

    private async Task<AgentClipboardSetResponseDto> SetClipboardAsync(AgentClipboardSetRequestDto req, CancellationToken ct)
    {
        var resp = await SendToSessionBridgeAsync(
            new SessionBridgeRequest("clipboard_set", JsonSerializer.Serialize(req, JsonOptions)),
            ct).ConfigureAwait(false);
        return TryDeserializePayload<AgentClipboardSetResponseDto>(resp.PayloadJson)
            ?? new AgentClipboardSetResponseDto(resp.Ok, Error: resp.Ok ? null : "BRIDGE_UNAVAILABLE", Message: resp.Error ?? "桌面交互会话不可用。");
    }

    private async Task<string> ResolveUploadDirectoryAsync(CancellationToken ct)
    {
        var resp = await SendToSessionBridgeAsync(new SessionBridgeRequest("get_upload_dir"), ct).ConfigureAwait(false);
        if (resp.Ok)
        {
            var dir = TryDeserializePayload<string>(resp.PayloadJson);
            if (!string.IsNullOrWhiteSpace(dir))
                return dir;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "RemoteDesk");
    }

    private async Task<AgentDisplayStateDto> GetDisplayStateAsync(CancellationToken ct)
    {
        var resp = await SendToSessionBridgeAsync(new SessionBridgeRequest("display_get"), ct).ConfigureAwait(false);
        if (resp.Ok)
        {
            var dto = TryDeserializePayload<AgentDisplayStateDto>(resp.PayloadJson);
            if (dto is not null)
                return dto;
        }

        return new AgentDisplayStateDto(new DisplayModeInfo(1920, 1080, 60), [], false, null);
    }

    private async Task<AgentDisplayApplyResultDto> ApplyDisplayModeAsync(AgentDisplayApplyRequestDto req, CancellationToken ct)
    {
        var resp = await SendToSessionBridgeAsync(
            new SessionBridgeRequest("display_apply", JsonSerializer.Serialize(req, JsonOptions)),
            ct).ConfigureAwait(false);
        return TryDeserializePayload<AgentDisplayApplyResultDto>(resp.PayloadJson)
            ?? new AgentDisplayApplyResultDto(false, false, Error: "BRIDGE_UNAVAILABLE", Message: resp.Error ?? "当前未登录用户桌面，无法调整显示器分辨率。");
    }

    private async Task<AgentDisplayApplyResultDto> RestoreDisplayModeAsync(CancellationToken ct)
    {
        var resp = await SendToSessionBridgeAsync(new SessionBridgeRequest("display_restore"), ct).ConfigureAwait(false);
        return TryDeserializePayload<AgentDisplayApplyResultDto>(resp.PayloadJson)
            ?? new AgentDisplayApplyResultDto(false, false, Error: "BRIDGE_UNAVAILABLE", Message: resp.Error ?? "桌面交互会话不可用。");
    }

    private async Task<AgentPrivacyResultDto> SetPrivacyScreenAsync(bool enabled, CancellationToken ct)
    {
        var resp = await SendToSessionBridgeAsync(
            new SessionBridgeRequest("privacy_set", JsonSerializer.Serialize(new AgentPrivacyRequestDto(enabled), JsonOptions)),
            ct).ConfigureAwait(false);
        return TryDeserializePayload<AgentPrivacyResultDto>(resp.PayloadJson)
            ?? new AgentPrivacyResultDto(false, false, "BRIDGE_UNAVAILABLE", resp.Error ?? "桌面交互会话不可用，无法启用防窥黑屏。");
    }

    private async Task<AgentPowerResultDto> ExecutePowerActionAsync(string action, CancellationToken ct)
    {
        if (action == "lock")
        {
            var resp = await SendToSessionBridgeAsync(new SessionBridgeRequest("power_lock"), ct).ConfigureAwait(false);
            return TryDeserializePayload<AgentPowerResultDto>(resp.PayloadJson)
                ?? new AgentPowerResultDto(false, action, "BRIDGE_UNAVAILABLE", resp.Error ?? "无法连接用户桌面会话，无法锁屏。");
        }

        string args = action switch
        {
            "restart" => "/r /t 5 /c \"RemoteDesk remote restart\"",
            "shutdown" => "/s /t 5 /c \"RemoteDesk remote shutdown\"",
            "cancel_shutdown" => "/a",
            _ => throw new ArgumentException($"Unsupported power action: {action}", nameof(action)),
        };

        try
        {
            var si = new ProcessStartInfo("shutdown.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(si);
            var message = action switch
            {
                "restart" => "目标主机将在 5 秒内重启。",
                "shutdown" => "目标主机将在 5 秒内关机。",
                _ => "已取消计划中的关机/重启任务。",
            };
            return new AgentPowerResultDto(true, action, null, message);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return new AgentPowerResultDto(false, action, "POWER_EXEC_FAILED", $"执行电源操作失败：{ex.Message}");
        }
    }

    private static T? TryDeserializePayload<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private async Task<SessionBridgeResponse> SendToSessionBridgeAsync(SessionBridgeRequest request, CancellationToken ct)
    {
        var first = await SendBridgeDirectOnceAsync(request, ct).ConfigureAwait(false);
        if (first is not null)
            return first;

        if (_isSystemService)
        {
            var spawned = await EnsureInteractiveSessionBridgeAsync(ct).ConfigureAwait(false);
            if (spawned)
            {
                var retry = await SendBridgeDirectOnceAsync(request, ct).ConfigureAwait(false);
                if (retry is not null)
                    return retry;
            }
        }

        return new SessionBridgeResponse(false, Error: "被控端桌面交互桥接未启动或当前处于锁屏界面。");
    }

    private static async Task<SessionBridgeResponse?> SendBridgeDirectOnceAsync(SessionBridgeRequest request, CancellationToken ct)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", AgentIpcConstants.SessionBridgePipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(900));
            await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);

            using var ioCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ioCts.CancelAfter(BridgeIoTimeout);

            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

            await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions)).ConfigureAwait(false);
            var raw = await reader.ReadToEndAsync(ioCts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw))
                return new SessionBridgeResponse(false, Error: "交互桥接未返回有效响应。");

            return JsonSerializer.Deserialize<SessionBridgeResponse>(raw, JsonOptions)
                ?? new SessionBridgeResponse(false, Error: "无法解析交互桥接响应。");
        }
        catch (JsonException)
        {
            return new SessionBridgeResponse(false, Error: "交互桥接返回了无效的 JSON 数据。");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            if (ct.IsCancellationRequested)
                throw;
            return null;
        }
    }

    private async Task<bool> EnsureInteractiveSessionBridgeAsync(CancellationToken ct)
    {
        await _bridgeLaunchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await IsPipeReachableAsync(ct).ConfigureAwait(false))
                return true;

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                return false;

            if (!LaunchInActiveConsoleSession(exePath))
                return false;

            for (var i = 0; i < 16; i++)
            {
                await Task.Delay(150, ct).ConfigureAwait(false);
                if (await IsPipeReachableAsync(ct).ConfigureAwait(false))
                    return true;
            }

            return false;
        }
        finally
        {
            _bridgeLaunchLock.Release();
        }
    }

    private static async Task<bool> IsPipeReachableAsync(CancellationToken ct)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", AgentIpcConstants.SessionBridgePipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(200);
            await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal bool LaunchInteractiveExecutable(string exePath, string arguments = "")
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return false;

        var sessionId = WtsGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF)
            return false;

        if (!WTSQueryUserToken(sessionId, out var userToken))
        {
            _logger?.Invoke($"WTSQueryUserToken({sessionId}) failed: {Marshal.GetLastWin32Error()}");
            return false;
        }

        var primaryToken = IntPtr.Zero;
        var envBlock = IntPtr.Zero;
        try
        {
            if (!DuplicateTokenEx(userToken, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primaryToken))
                return false;

            CreateEnvironmentBlock(out envBlock, primaryToken, false);

            var si = new STARTUPINFOW
            {
                cb = Marshal.SizeOf<STARTUPINFOW>(),
                lpDesktop = @"winsta0\default",
            };

            var cmdLine = string.IsNullOrWhiteSpace(arguments) ? $"\"{exePath}\"" : $"\"{exePath}\" {arguments}";
            var flags = CreateNoWindow | (envBlock != IntPtr.Zero ? CreateUnicodeEnvironment : 0u);
            var ok = CreateProcessAsUserW(
                primaryToken,
                exePath,
                cmdLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                flags,
                envBlock,
                Path.GetDirectoryName(exePath),
                ref si,
                out var pi);

            if (ok)
            {
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
            }
            return ok;
        }
        finally
        {
            if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            CloseHandle(userToken);
        }
    }

    private bool LaunchInActiveConsoleSession(string exePath) =>
        LaunchInteractiveExecutable(exePath, "--session-bridge");

    private const uint MaximumAllowed = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", EntryPoint = "WTSGetActiveConsoleSessionId")]
    private static extern uint WtsGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        IntPtr hExistingToken,
        uint dwDesiredAccess,
        IntPtr lpTokenAttributes,
        int impersonationLevel,
        int tokenType,
        out IntPtr phNewToken);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, [MarshalAs(UnmanagedType.Bool)] bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUserW(
        IntPtr hToken,
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
