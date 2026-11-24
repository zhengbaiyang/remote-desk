using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace RemoteDesk;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class AgentDaemonRuntime : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _configLock = new(1, 1);
    private readonly SyncApiClient _syncClient = new();
    private readonly bool _isSystemService;
    private readonly HostSystemOperations _hostOps;

    private AgentConfigDocument _config = new();
    private AgentPeerServer? _peerServer;
    private CancellationTokenSource? _cts;
    private Task? _pipeLoopTask;
    private Task? _checkinLoopTask;
    private string? _lastAppliedEasyTierFingerprint;
    private long? _lastCheckinUtc;
    private string? _lastError;

    public AgentDaemonRuntime(bool isSystemService)
    {
        _isSystemService = isSystemService;
        _hostOps = new HostSystemOperations(isSystemService, Log);
    }

    public async Task StartAsync(CancellationToken externalCt = default)
    {
        Directory.CreateDirectory(AgentIpcConstants.GetProgramDataDirectory());
        Directory.CreateDirectory(Path.GetDirectoryName(AgentIpcConstants.GetAgentLogPath())!);
        Log($"AgentDaemonRuntime starting (systemService={_isSystemService}).");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        LoadConfigFromDisk();
        await ReconcileManagedProcessesAsync(forceRestartEasyTier: false, _cts.Token).ConfigureAwait(false);
        StartPeerServer();

        _pipeLoopTask = Task.Run(() => RunPipeServerLoopAsync(_cts.Token), _cts.Token);
        _checkinLoopTask = Task.Run(() => RunHeartbeatLoopAsync(_cts.Token), _cts.Token);
    }

    public async Task StopAsync()
    {
        Log("AgentDaemonRuntime stopping...");
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _peerServer?.Stop();

        var tasks = new List<Task>();
        if (_pipeLoopTask is not null) tasks.Add(_pipeLoopTask);
        if (_checkinLoopTask is not null) tasks.Add(_checkinLoopTask);
        if (tasks.Count > 0)
        {
            try
            {
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        await _hostOps.CleanupOnStopAsync().ConfigureAwait(false);
        EasyTierManager.StopMesh();
        Log("AgentDaemonRuntime stopped.");
    }

    public void Dispose()
    {
        _peerServer?.Dispose();
        _hostOps.Dispose();
        _cts?.Dispose();
        _configLock.Dispose();
    }

    private void StartPeerServer()
    {
        _peerServer?.Dispose();
        _peerServer = new AgentPeerServer(BuildPeerServerState, HandleRemotePairPinAsync, _hostOps.CreatePeerFeatureHandlers());
        try
        {
            var port = _config.AgentPort > 0 ? _config.AgentPort : AgentIpcConstants.DefaultAgentPort;
            _peerServer.Start(IPAddress.Any, port);
            Log($"AgentPeerServer listening on 0.0.0.0:{_peerServer.BoundPort}.");
        }
        catch (Exception ex)
        {
            _lastError = $"节点控制端口启动失败：{ex.Message}";
            Log($"Failed to start AgentPeerServer: {ex}");
        }
    }

    private AgentPeerServerState BuildPeerServerState()
    {
        var cfg = _config;
        var sunshineExe = ResolveSunshineExe(cfg);
        var hostStatus = HostServiceManager.GetHostStatus(sunshineExe);
        SecretProtector.TryUnprotect(cfg.ProtectedAgentAuthKey, out var authKey);

        return new AgentPeerServerState(
            DeviceKey: cfg.DeviceKey ?? Environment.MachineName,
            Hostname: cfg.DeviceName ?? Environment.MachineName,
            SunshineReady: hostStatus.SunshineInstalled && hostStatus.SunshineRunning,
            SunshinePort: SunshineManager.DefaultStreamPort,
            AgentAuthKey: string.IsNullOrEmpty(authKey) ? null : authKey,
            SecurityPinRequired: cfg.SecurityPinRequired,
            SecurityPinSalt: cfg.SecurityPinSalt,
            SecurityPinHash: cfg.SecurityPinHash);
    }

    private async Task<(bool Success, string Message)> HandleRemotePairPinAsync(string pin, string clientName, CancellationToken ct)
    {
        var cfg = _config;
        if (!cfg.HostModeEnabled)
            return (false, "目标电脑未开启「允许本机被控」。");

        if (!SecretProtector.TryUnprotect(cfg.ProtectedSunshineAdminPassword, out var adminPass) || string.IsNullOrEmpty(adminPass))
            return (false, "目标电脑 Sunshine 自动托管凭据尚未初始化，请先启用被控服务。");

        var adminUser = string.IsNullOrWhiteSpace(cfg.SunshineAdminUser)
            ? SunshineManager.DefaultAdminUser
            : cfg.SunshineAdminUser;

        return await SunshineManager
            .SubmitPairingPinAsync(pin, clientName, adminUser, adminPass, null, null, ct)
            .ConfigureAwait(false);
    }

    private async Task RunPipeServerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                var pipeSecurity = CreatePipeSecurity();
                pipe = NamedPipeServerStreamAcl.Create(
                    AgentIpcConstants.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 64 * 1024,
                    outBufferSize: 64 * 1024,
                    pipeSecurity);

                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var connectedPipe = pipe;
                pipe = null;
                _ = Task.Run(() => HandlePipeClientAsync(connectedPipe, ct), ct);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                Log($"Pipe accept error: {ex.Message}");
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HandlePipeClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line))
                    return;

                AgentIpcRequest? req;
                try
                {
                    req = JsonSerializer.Deserialize<AgentIpcRequest>(line, JsonOptions);
                }
                catch (JsonException)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new AgentIpcResponse(false, null, "IPC 请求 JSON 无效。"), JsonOptions)).ConfigureAwait(false);
                    return;
                }

                if (req is null)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new AgentIpcResponse(false, null, "IPC 请求为空。"), JsonOptions)).ConfigureAwait(false);
                    return;
                }

                var response = await ExecuteIpcCommandAsync(req, ct).ConfigureAwait(false);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log($"Pipe client error: {ex.Message}");
            }
        }
    }

    private async Task<AgentIpcResponse> ExecuteIpcCommandAsync(AgentIpcRequest req, CancellationToken ct)
    {
        switch (req.Command)
        {
            case "get_status":
            {
                var status = await QueryRuntimeStatusAsync(ct).ConfigureAwait(false);
                return new AgentIpcResponse(true, JsonSerializer.Serialize(status, JsonOptions));
            }
            case "apply_config":
            {
                var payload = string.IsNullOrWhiteSpace(req.PayloadJson)
                    ? null
                    : JsonSerializer.Deserialize<AgentApplyConfigPayload>(req.PayloadJson, JsonOptions);
                if (payload is null)
                    return new AgentIpcResponse(false, null, "缺少配置载荷。");

                await ApplyConfigPayloadAsync(payload, ct).ConfigureAwait(false);
                var status = await QueryRuntimeStatusAsync(ct).ConfigureAwait(false);
                return new AgentIpcResponse(true, JsonSerializer.Serialize(status, JsonOptions));
            }
            case "start_easytier":
            {
                await _configLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (_config.EasyTier is null)
                        return new AgentIpcResponse(false, null, "尚未同步或填写 EasyTier 组网配置。");
                    _config = _config with { EasyTier = _config.EasyTier with { Enabled = true } };
                    SaveConfigToDisk(_config);
                    await ReconcileEasyTierAsync(forceRestart: true, ct).ConfigureAwait(false);
                }
                finally
                {
                    _configLock.Release();
                }

                var status = await QueryRuntimeStatusAsync(ct).ConfigureAwait(false);
                return new AgentIpcResponse(true, JsonSerializer.Serialize(status, JsonOptions));
            }
            case "stop_easytier":
            {
                await _configLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (_config.EasyTier is not null)
                    {
                        _config = _config with { EasyTier = _config.EasyTier with { Enabled = false } };
                        SaveConfigToDisk(_config);
                    }
                    EasyTierManager.StopMesh();
                    _lastAppliedEasyTierFingerprint = null;
                }
                finally
                {
                    _configLock.Release();
                }

                var status = await QueryRuntimeStatusAsync(ct).ConfigureAwait(false);
                return new AgentIpcResponse(true, JsonSerializer.Serialize(status, JsonOptions));
            }
            case "start_sunshine":
            {
                await _configLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    _config = _config with { HostModeEnabled = true };
                    SaveConfigToDisk(_config);
                    EnsureSunshineRunning();
                }
                finally
                {
                    _configLock.Release();
                }

                var status = await QueryRuntimeStatusAsync(ct).ConfigureAwait(false);
                return new AgentIpcResponse(true, JsonSerializer.Serialize(status, JsonOptions));
            }
            case "stop_sunshine":
            {
                await _configLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    _config = _config with { HostModeEnabled = false };
                    SaveConfigToDisk(_config);
                    HostServiceManager.StopSunshineHost();
                }
                finally
                {
                    _configLock.Release();
                }

                var status = await QueryRuntimeStatusAsync(ct).ConfigureAwait(false);
                return new AgentIpcResponse(true, JsonSerializer.Serialize(status, JsonOptions));
            }
            case "set_security_pin":
            {
                var pinPayload = string.IsNullOrWhiteSpace(req.PayloadJson)
                    ? null
                    : JsonSerializer.Deserialize<AgentSetSecurityPinPayload>(req.PayloadJson, JsonOptions);
                if (pinPayload is null)
                    return new AgentIpcResponse(false, null, "缺少安全密码配置。");

                await _configLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (!pinPayload.Required)
                    {
                        _config = _config with
                        {
                            SecurityPinRequired = false,
                            SecurityPinSalt = null,
                            SecurityPinHash = null,
                        };
                    }
                    else if (!string.IsNullOrEmpty(pinPayload.NewPin))
                    {
                        var (salt, hash) = SecurityPinHasher.HashPin(pinPayload.NewPin);
                        _config = _config with
                        {
                            SecurityPinRequired = true,
                            SecurityPinSalt = salt,
                            SecurityPinHash = hash,
                        };
                    }
                    else if (!string.IsNullOrEmpty(_config.SecurityPinHash))
                    {
                        _config = _config with { SecurityPinRequired = true };
                    }
                    else
                    {
                        return new AgentIpcResponse(false, null, "首次开启独立安全密码时必须设置安全密码。");
                    }

                    SaveConfigToDisk(_config);
                }
                finally
                {
                    _configLock.Release();
                }

                _ = TrySendCheckinAsync(ct);
                var status = await QueryRuntimeStatusAsync(ct).ConfigureAwait(false);
                return new AgentIpcResponse(true, JsonSerializer.Serialize(status, JsonOptions));
            }
            default:
                return new AgentIpcResponse(false, null, $"未知命令：{req.Command}");
        }
    }

    private async Task ApplyConfigPayloadAsync(AgentApplyConfigPayload payload, CancellationToken ct)
    {
        await _configLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var protectedAuthKey = string.IsNullOrEmpty(payload.AgentAuthKey)
                ? null
                : SecretProtector.ProtectLocalMachine(payload.AgentAuthKey);
            var protectedToken = string.IsNullOrEmpty(payload.SyncSessionToken)
                ? null
                : SecretProtector.ProtectLocalMachine(payload.SyncSessionToken);

            EasyTierConfig? storedEasyTier = payload.EasyTier;
            if (storedEasyTier is not null && !string.IsNullOrEmpty(storedEasyTier.NetworkSecret))
            {
                storedEasyTier = storedEasyTier with
                {
                    NetworkSecret = SecretProtector.ProtectLocalMachine(storedEasyTier.NetworkSecret),
                };
            }

            var updated = _config with
            {
                EasyTierExecutablePath = payload.EasyTierExecutablePath ?? _config.EasyTierExecutablePath,
                SunshineExecutablePath = payload.SunshineExecutablePath ?? _config.SunshineExecutablePath,
                EasyTier = storedEasyTier,
                HostModeEnabled = payload.HostModeEnabled,
                DeviceKey = payload.DeviceKey ?? _config.DeviceKey,
                DeviceName = payload.DeviceName ?? _config.DeviceName,
                AccountId = payload.AccountId,
                ProtectedAgentAuthKey = protectedAuthKey,
                AgentPort = payload.AgentPort > 0 ? payload.AgentPort : AgentIpcConstants.DefaultAgentPort,
                SecurityPinRequired = payload.SecurityPinRequired,
                SecurityPinSalt = payload.SecurityPinSalt ?? _config.SecurityPinSalt,
                SecurityPinHash = payload.SecurityPinHash ?? _config.SecurityPinHash,
                SyncServerUrl = payload.SyncServerUrl,
                SyncServerCertThumbprint = payload.SyncServerCertThumbprint,
                ProtectedSyncSessionToken = protectedToken,
            };

            _config = updated;
            SaveConfigToDisk(updated);
            await ReconcileManagedProcessesAsync(forceRestartEasyTier: false, ct).ConfigureAwait(false);
        }
        finally
        {
            _configLock.Release();
        }
    }

    private async Task ReconcileManagedProcessesAsync(bool forceRestartEasyTier, CancellationToken ct)
    {
        await ReconcileEasyTierAsync(forceRestartEasyTier, ct).ConfigureAwait(false);

        if (_config.HostModeEnabled)
            EnsureSunshineRunning();
    }

    private async Task ReconcileEasyTierAsync(bool forceRestart, CancellationToken ct)
    {
        var etCfg = UnprotectEasyTierConfig(_config.EasyTier);
        var coreExe = ResolveEasyTierExe(_config);
        if (etCfg is not { Enabled: true } || string.IsNullOrEmpty(coreExe))
            return;

        try
        {
            var validated = EasyTierConfig.Validate(etCfg);
            var fingerprint = $"{coreExe}|{validated.NetworkName}|{validated.NetworkSecret}|{validated.PeerUrl}|{validated.AutoAssignIpv4}|{validated.VirtualIpv4}";

            if (forceRestart || (!string.Equals(fingerprint, _lastAppliedEasyTierFingerprint, StringComparison.Ordinal) && EasyTierManager.IsMeshRunning()))
            {
                EasyTierManager.StopMesh();
                await Task.Delay(250, ct).ConfigureAwait(false);
            }

            if (!EasyTierManager.IsMeshRunning())
            {
                if (EasyTierManager.StartMesh(coreExe, validated))
                    Log($"Started EasyTier mesh ({validated.NetworkName}, staticIp={validated.VirtualIpv4 ?? "dhcp"}).");
                else
                    _lastError = "EasyTier 进程启动失败。";
            }

            _lastAppliedEasyTierFingerprint = fingerprint;
        }
        catch (Exception ex)
        {
            _lastError = $"EasyTier 启动异常：{ex.Message}";
            Log($"EasyTier reconcile failed: {ex.Message}");
        }
    }

    private void EnsureSunshineRunning()
    {
        var sunshineExe = ResolveSunshineExe(_config);
        if (string.IsNullOrEmpty(sunshineExe))
            return;

        try
        {
            var configPath = SunshineManager.ResolveConfigPath(sunshineExe);
            SunshineManager.EnsureSafeLocalConfig(configPath);

            var hasCreds = !string.IsNullOrEmpty(_config.SunshineAdminUser) &&
                           SecretProtector.TryUnprotect(_config.ProtectedSunshineAdminPassword, out var existingPass) &&
                           !string.IsNullOrEmpty(existingPass);

            if (!hasCreds)
            {
                var wasRunning = HostServiceManager.GetHostStatus(sunshineExe).SunshineRunning;
                if (wasRunning)
                    HostServiceManager.StopSunshineHost();

                var generatedPass = SunshineManager.GenerateRandomPassword();
                var credsStart = SunshineManager.CreateSetCredentialsStartInfo(sunshineExe, SunshineManager.DefaultAdminUser, generatedPass);
                using (var proc = Process.Start(credsStart))
                {
                    proc?.WaitForExit(5000);
                }

                _config = _config with
                {
                    SunshineAdminUser = SunshineManager.DefaultAdminUser,
                    ProtectedSunshineAdminPassword = SecretProtector.ProtectLocalMachine(generatedPass),
                };
                SaveConfigToDisk(_config);
                Log("Initialized auto-managed Sunshine admin credentials.");
            }

            if (!HostServiceManager.GetHostStatus(sunshineExe).SunshineRunning)
            {
                HostServiceManager.StartSunshineHost(sunshineExe);
                Log("Started Sunshine host process.");
            }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException or ArgumentException)
        {
            _lastError = $"Sunshine 自动托管失败：{ex.Message}";
            Log($"EnsureSunshineRunning error: {ex.Message}");
        }
    }

    private async Task<AgentRuntimeStatus> QueryRuntimeStatusAsync(CancellationToken ct)
    {
        var cfg = _config;
        var coreExe = ResolveEasyTierExe(cfg);
        var sunshineExe = ResolveSunshineExe(cfg);
        var etCfg = UnprotectEasyTierConfig(cfg.EasyTier) ?? new EasyTierConfig();
        var etStatus = await EasyTierManager.QueryStatusAsync(coreExe, etCfg, ct).ConfigureAwait(false);
        var hostStatus = HostServiceManager.GetHostStatus(sunshineExe);

        var sunshineManagedReady = hostStatus.SunshineInstalled &&
                                   hostStatus.SunshineRunning &&
                                   !string.IsNullOrEmpty(cfg.ProtectedSunshineAdminPassword);

        return new AgentRuntimeStatus(
            ServiceRunning: true,
            IsSystemService: _isSystemService,
            EasyTierRunning: etStatus.Running,
            EasyTierVirtualIpv4: etStatus.VirtualIpv4,
            EasyTierPeers: etStatus.Peers,
            EasyTierDetail: etStatus.DetailMessage,
            SunshineRunning: hostStatus.SunshineRunning,
            SunshineManagedReady: sunshineManagedReady,
            AgentPort: _peerServer?.BoundPort is > 0 ? _peerServer.BoundPort : cfg.AgentPort,
            HostModeEnabled: cfg.HostModeEnabled,
            SecurityPinRequired: cfg.SecurityPinRequired,
            LastCheckinUtc: _lastCheckinUtc,
            LastError: _lastError);
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TrySendCheckinAsync(ct).ConfigureAwait(false);
                await ReconcileManagedProcessesAsync(forceRestartEasyTier: false, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"Heartbeat error: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TrySendCheckinAsync(CancellationToken ct)
    {
        var cfg = _config;
        if (string.IsNullOrWhiteSpace(cfg.SyncServerUrl) ||
            string.IsNullOrWhiteSpace(cfg.DeviceKey) ||
            !SecretProtector.TryUnprotect(cfg.ProtectedSyncSessionToken, out var token) ||
            string.IsNullOrEmpty(token))
        {
            return;
        }

        var net = HostServiceManager.GetLocalNetworkInfo();
        var gpus = HostServiceManager.DetectGpuEncoders();
        var hostStatus = HostServiceManager.GetHostStatus(ResolveSunshineExe(cfg));
        var req = new SyncCheckinRequest(
            DeviceKey: cfg.DeviceKey,
            Name: cfg.DeviceName ?? net.HostName,
            Hostname: net.HostName,
            LanIpv4: net.IPv4Addresses.FirstOrDefault(),
            LanIpv6: net.IPv6Addresses.FirstOrDefault(),
            SunshineReady: hostStatus.SunshineInstalled && hostStatus.SunshineRunning,
            SunshinePort: SunshineManager.DefaultStreamPort,
            AgentPort: cfg.AgentPort > 0 ? cfg.AgentPort : AgentIpcConstants.DefaultAgentPort,
            SecurityPinRequired: cfg.SecurityPinRequired,
            GpuEncoder: gpus.FirstOrDefault()?.EncoderName,
            OsVersion: Environment.OSVersion.VersionString);

        var result = await _syncClient.CheckinDeviceAsync(
            cfg.SyncServerUrl,
            token,
            req,
            cfg.SyncServerCertThumbprint,
            allowTrustOnFirstUse: false,
            ct).ConfigureAwait(false);

        _lastCheckinUtc = result.ServerTimeUtc;
        _lastError = null;

        // If server allocated a virtual IP and local EasyTier isn't pinned to it yet, sync immediately.
        var assignedIp = result.Device.VirtualIpv4;
        if (!string.IsNullOrWhiteSpace(assignedIp))
        {
            await _configLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var latestCfg = _config;
                if (latestCfg.EasyTier is { } etCfg &&
                    !string.Equals(StripCidr(etCfg.VirtualIpv4), assignedIp, StringComparison.Ordinal))
                {
                    var updatedEt = etCfg with
                    {
                        Enabled = true,
                        AutoAssignIpv4 = false,
                        VirtualIpv4 = $"{assignedIp}/24",
                    };
                    _config = latestCfg with { EasyTier = updatedEt };
                    SaveConfigToDisk(_config);
                    await ReconcileEasyTierAsync(forceRestart: true, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                _configLock.Release();
            }
        }
    }

    private void LoadConfigFromDisk()
    {
        var path = AgentIpcConstants.GetAgentConfigPath();
        if (!File.Exists(path))
        {
            _config = new AgentConfigDocument();
            return;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<AgentConfigDocument>(File.ReadAllText(path, Encoding.UTF8), JsonOptions);
            _config = loaded ?? new AgentConfigDocument();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log($"Failed to load agent-config.json: {ex.Message}");
            _config = new AgentConfigDocument();
        }
    }

    private static void SaveConfigToDisk(AgentConfigDocument config)
    {
        var path = AgentIpcConstants.GetAgentConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOptions), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }

    private static EasyTierConfig? UnprotectEasyTierConfig(EasyTierConfig? cfg)
    {
        if (cfg is null)
            return null;

        if (!SecretProtector.TryUnprotect(cfg.NetworkSecret, out var plainSecret))
            plainSecret = cfg.NetworkSecret;

        return cfg with { NetworkSecret = plainSecret };
    }

    private static string? ResolveEasyTierExe(AgentConfigDocument cfg)
    {
        var candidate = EasyTierManager.FindCoreExecutable(cfg.EasyTierExecutablePath, null);
        return candidate;
    }

    private static string? ResolveSunshineExe(AgentConfigDocument cfg) =>
        HostServiceManager.FindSunshineExecutable(cfg.SunshineExecutablePath, null);

    private static string StripCidr(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
            return "";
        var slash = ip.IndexOf('/');
        return slash >= 0 ? ip[..slash].Trim() : ip.Trim();
    }

    private static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        return security;
    }

    private static void Log(string message)
    {
        try
        {
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
            File.AppendAllText(AgentIpcConstants.GetAgentLogPath(), line, Encoding.UTF8);
        }
        catch
        {
        }
    }
}
