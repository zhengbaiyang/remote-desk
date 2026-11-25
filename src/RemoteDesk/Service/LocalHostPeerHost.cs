using System.IO;
using System.Net;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace RemoteDesk;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class LocalHostPeerHost : IDisposable
{
    private sealed record StoredLocalSunshineCreds(
        string Username,
        string ProtectedPassword);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly HostSystemOperations _hostOps = new(isSystemService: false);
    private readonly SemaphoreSlim _provisionLock = new(1, 1);

    private AgentPeerServer? _server;
    private bool _hostModeEnabled;
    private string _deviceKey = "";
    private string _deviceName = Environment.MachineName;
    private string? _authKey;
    private string? _sunshineExe;
    private bool _securityPinRequired;
    private string? _securityPinSalt;
    private string? _securityPinHash;

    public bool IsListening => _server is not null;

    public void Reconcile(
        bool shouldRun,
        string deviceKey,
        string deviceName,
        string? authKey,
        string? sunshineExe,
        bool securityPinRequired,
        string? securityPinSalt,
        string? securityPinHash)
    {
        _hostModeEnabled = shouldRun;
        _deviceKey = string.IsNullOrWhiteSpace(deviceKey) ? Environment.MachineName : deviceKey;
        _deviceName = string.IsNullOrWhiteSpace(deviceName) ? Environment.MachineName : deviceName;
        _authKey = authKey;
        _sunshineExe = sunshineExe;
        _securityPinRequired = securityPinRequired;
        _securityPinSalt = securityPinSalt;
        _securityPinHash = securityPinHash;

        if (!shouldRun || string.IsNullOrEmpty(authKey))
        {
            Stop();
            return;
        }

        if (_server is null)
        {
            var server = new AgentPeerServer(BuildState, HandlePairPinAsync, _hostOps.CreatePeerFeatureHandlers());
            try
            {
                server.Start(IPAddress.Any, AgentIpcConstants.DefaultAgentPort);
                _server = server;
            }
            catch
            {
                server.Dispose();
            }
        }
    }

    public void Stop()
    {
        var server = _server;
        _server = null;
        server?.Dispose();
    }

    public void Dispose()
    {
        Stop();
        _hostOps.Dispose();
        _provisionLock.Dispose();
    }

    public AgentSunshineCredsDto? TryGetOrProvisionManagedCredentials(string? sunshineExeOverride = null)
    {
        var stored = LoadStoredCreds();
        if (stored is { } existing)
            return new AgentSunshineCredsDto(existing.Username, existing.Password);

        var exe = sunshineExeOverride ?? _sunshineExe ?? HostServiceManager.FindSunshineExecutable(null, null);
        if (string.IsNullOrEmpty(exe))
            return null;

        var res = ProvisionAndStartInternal(exe, forceReprovision: true);
        return res.Ok ? new AgentSunshineCredsDto(res.Username, res.Password) : null;
    }

    private AgentPeerServerState BuildState()
    {
        var exe = _sunshineExe ?? HostServiceManager.FindSunshineExecutable(null, null);
        var status = HostServiceManager.GetHostStatus(exe);

        if (_hostModeEnabled && status.SunshineInstalled && !status.SunshineRunning)
        {
            _ = Task.Run(() =>
            {
                if (!string.IsNullOrEmpty(exe))
                    _ = ProvisionAndStartInternal(exe, forceReprovision: false);
            });
        }

        return new AgentPeerServerState(
            DeviceKey: _deviceKey,
            Hostname: _deviceName,
            SunshineReady: status.SunshineInstalled && status.SunshineRunning,
            SunshinePort: SunshineManager.DefaultStreamPort,
            AgentAuthKey: _authKey,
            SecurityPinRequired: _securityPinRequired,
            SecurityPinSalt: _securityPinSalt,
            SecurityPinHash: _securityPinHash);
    }

    private async Task<(bool Success, string Message)> HandlePairPinAsync(string pin, string clientName, CancellationToken ct)
    {
        if (!_hostModeEnabled)
            return (false, "目标电脑未开启「允许本机被控」。");

        return await SunshineManager.SubmitPinWithSelfHealAsync(
            pin,
            clientName,
            getCredentials: () =>
            {
                var creds = LoadStoredCreds();
                return creds is not null ? (creds.Value.Username, creds.Value.Password) : null;
            },
            reprovisionAndStartAsync: ct2 => ProvisionAndStartAsync(forceReprovision: true, ct2),
            wakeHostAsync: async () =>
            {
                var res = await ProvisionAndStartAsync(forceReprovision: false, ct).ConfigureAwait(false);
                return res.Ok;
            },
            baseUri: null,
            httpClient: null,
            ct: ct).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Username, string Password, string? Error)> ProvisionAndStartAsync(
        bool forceReprovision,
        CancellationToken ct)
    {
        await _provisionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var exe = _sunshineExe ?? HostServiceManager.FindSunshineExecutable(null, null);
            if (string.IsNullOrEmpty(exe))
                return (false, SunshineManager.DefaultAdminUser, "", "未检测到 Sunshine 安装程序。");

            return ProvisionAndStartInternal(exe, forceReprovision);
        }
        finally
        {
            _provisionLock.Release();
        }
    }

    private (bool Ok, string Username, string Password, string? Error) ProvisionAndStartInternal(
        string sunshineExe,
        bool forceReprovision)
    {
        try
        {
            var stored = LoadStoredCreds();
            var user = stored?.Username ?? SunshineManager.DefaultAdminUser;
            var pass = stored?.Password ?? "";

            if (forceReprovision || string.IsNullOrEmpty(pass))
            {
                var generated = SunshineManager.GenerateRandomPassword();
                var (ok, message) = SunshineManager.ProvisionCredentialsSync(sunshineExe, SunshineManager.DefaultAdminUser, generated);
                if (!ok)
                    return (false, SunshineManager.DefaultAdminUser, "", message);

                user = SunshineManager.DefaultAdminUser;
                pass = generated;
                SaveStoredCreds(user, pass);
            }

            var status = HostServiceManager.GetHostStatus(sunshineExe);
            if (!status.SunshineRunning)
            {
                if (!status.SunshineServiceRegistered || !HostServiceManager.TryStartSunshineService())
                    HostServiceManager.StartSunshineHost(sunshineExe);
            }

            return (true, user, pass, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return (false, SunshineManager.DefaultAdminUser, "", $"本地 Sunshine 托管初始化失败：{ex.Message}");
        }
    }

    private static string GetCredsFilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RemoteDesk",
        "sunshine-local-creds.json");

    private static (string Username, string Password)? LoadStoredCreds()
    {
        var path = GetCredsFilePath();
        if (!File.Exists(path))
            return null;

        try
        {
            var doc = JsonSerializer.Deserialize<StoredLocalSunshineCreds>(File.ReadAllText(path, Encoding.UTF8), JsonOptions);
            if (doc is null || string.IsNullOrWhiteSpace(doc.Username) || string.IsNullOrWhiteSpace(doc.ProtectedPassword))
                return null;

            if (!SecretProtector.TryUnprotect(doc.ProtectedPassword, out var plain) || string.IsNullOrEmpty(plain))
                return null;

            return (doc.Username, plain);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveStoredCreds(string username, string password)
    {
        var path = GetCredsFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = new StoredLocalSunshineCreds(username, SecretProtector.ProtectCurrentUser(password));
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload, JsonOptions), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }
}
