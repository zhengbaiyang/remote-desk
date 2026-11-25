using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteDesk;

internal sealed record SunshinePinSubmitResult(
    bool Success,
    bool IsUnauthorized,
    bool IsUnreachable,
    string Message);

internal sealed record SunshinePendingPairing(
    string Id,
    string? Name,
    string? Address);

internal sealed record SunshinePendingPairingsQueryResult(
    bool Supported,
    bool IsUnauthorized,
    bool IsUnreachable,
    IReadOnlyList<SunshinePendingPairing> Pairings);

internal sealed record SunshineSelfHealResult(
    bool Success,
    string Message,
    bool SunshineRestarted = false);

internal static class SunshineManager
{
    public const string DefaultAdminUser = "remotedesk";
    public const int DefaultControlPort = 47990;
    public const int DefaultStreamPort = 47989;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool IsManagedSunshinePath(string? sunshineExecutable)
    {
        if (string.IsNullOrWhiteSpace(sunshineExecutable))
            return false;

        var normalized = sunshineExecutable.Replace('/', Path.DirectorySeparatorChar);
        var marker = $"{Path.DirectorySeparatorChar}RemoteDesk{Path.DirectorySeparatorChar}engines{Path.DirectorySeparatorChar}";
        return normalized.Contains(marker, StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolveConfigPath(string sunshineExecutable)
    {
        if (string.IsNullOrWhiteSpace(sunshineExecutable))
            throw new ArgumentException("Sunshine 路径不能为空。", nameof(sunshineExecutable));

        var dir = Path.GetDirectoryName(Path.GetFullPath(sunshineExecutable))
            ?? throw new ArgumentException("无法解析 Sunshine 安装目录。", nameof(sunshineExecutable));

        var nested = Path.Combine(dir, "config", "sunshine.conf");
        if (File.Exists(nested))
            return nested;

        var flat = Path.Combine(dir, "sunshine.conf");
        if (File.Exists(flat))
            return flat;

        return nested;
    }

    public static void EnsureSafeLocalConfig(string configPath, bool isManagedInstall = false)
    {
        if (string.IsNullOrWhiteSpace(configPath))
            throw new ArgumentException("Sunshine 配置文件路径无效。", nameof(configPath));

        var fullPath = Path.GetFullPath(configPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var existingLines = File.Exists(fullPath)
            ? File.ReadAllLines(fullPath, Encoding.UTF8)
            : [];
        var lines = existingLines.ToList();

        UpsertConfigKey(lines, "origin_web_ui_allowed", "pc");
        UpsertConfigKey(lines, "upnp", "off");
        UpsertConfigKey(lines, "address_family", "both");
        if (isManagedInstall)
        {
            UpsertConfigKey(lines, "system_tray", "disabled");
            UpsertConfigKey(lines, "notify_pre_releases", "disabled");
        }

        if (File.Exists(fullPath) && existingLines.SequenceEqual(lines, StringComparer.Ordinal))
            return;

        File.WriteAllLines(fullPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static ProcessStartInfo CreateSetCredentialsStartInfo(
        string sunshineExecutable,
        string username,
        string password)
    {
        if (!IsExecutable(sunshineExecutable))
            throw new FileNotFoundException("找不到 Sunshine 可执行文件。", sunshineExecutable);

        if (string.IsNullOrWhiteSpace(username) || username.Any(char.IsControl) || username.Any(char.IsWhiteSpace))
            throw new ArgumentException("Sunshine 用户名格式无效。", nameof(username));

        if (string.IsNullOrWhiteSpace(password) || password.Any(char.IsControl) || password.Any(char.IsWhiteSpace))
            throw new ArgumentException("Sunshine 密码格式无效。", nameof(password));

        var startInfo = new ProcessStartInfo(sunshineExecutable)
        {
            WorkingDirectory = Path.GetDirectoryName(sunshineExecutable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--creds");
        startInfo.ArgumentList.Add(username);
        startInfo.ArgumentList.Add(password);
        return startInfo;
    }

    public static string GenerateRandomPassword(int length = 24)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars);
    }

    public static (bool Ok, string Message) ProvisionCredentialsSync(
        string sunshineExecutable,
        string username,
        string password)
    {
        if (!IsExecutable(sunshineExecutable))
            return (false, "找不到 Sunshine 可执行文件。");

        try
        {
            var configPath = ResolveConfigPath(sunshineExecutable);
            EnsureSafeLocalConfig(configPath, IsManagedSunshinePath(sunshineExecutable));

            HostServiceManager.TryStopSunshineService();
            HostServiceManager.StopSunshineHost();

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline && HostServiceManager.GetHostStatus(sunshineExecutable).SunshineRunning)
                Thread.Sleep(200);

            var startInfo = CreateSetCredentialsStartInfo(sunshineExecutable, username, password);
            using var proc = Process.Start(startInfo);
            if (proc is null)
                return (false, "无法启动 Sunshine 凭据配置进程。");

            if (!proc.WaitForExit(8000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (false, "配置 Sunshine 管理员凭据超时。");
            }

            if (proc.ExitCode != 0)
            {
                var stderr = "";
                try { stderr = proc.StandardError.ReadToEnd().Trim(); } catch { }
                return (false, string.IsNullOrEmpty(stderr)
                    ? $"Sunshine 凭据配置失败（退出码 {proc.ExitCode}）。"
                    : $"Sunshine 凭据配置失败：{stderr}");
            }

            return (true, "已同步 Sunshine 托管凭据。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            return (false, $"配置 Sunshine 凭据失败：{ex.Message}");
        }
    }

    public static async Task<(bool Reachable, bool Authorized)> VerifyLocalCredentialsAsync(
        string username,
        string password,
        Uri? baseUri = null,
        HttpClient? httpClient = null,
        CancellationToken ct = default)
    {
        var targetBase = baseUri ?? new Uri($"https://127.0.0.1:{DefaultControlPort}");
        var ownsClient = httpClient is null;
        var client = httpClient ?? CreateLocalApiClient(TimeSpan.FromSeconds(3));
        try
        {
            var endpoint = new Uri(targetBase, "/api/config");
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return (true, false);

            return (true, response.IsSuccessStatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, false);
        }
        finally
        {
            if (ownsClient)
                client.Dispose();
        }
    }

    public static async Task<(bool Success, string Message)> SubmitPinWithSelfHealAsync(
        string pin,
        string clientName,
        Func<(string Username, string Password)?> getCredentials,
        Func<CancellationToken, Task<(bool Ok, string Username, string Password, string? Error)>> reprovisionAndStartAsync,
        Func<Task<bool>> wakeHostAsync,
        Uri? baseUri = null,
        HttpClient? httpClient = null,
        CancellationToken ct = default,
        string? remoteClientIp = null)
    {
        var result = await SubmitPinWithSelfHealDetailedAsync(
            pin,
            clientName,
            getCredentials,
            reprovisionAndStartAsync,
            wakeHostAsync,
            baseUri,
            httpClient,
            ct,
            remoteClientIp).ConfigureAwait(false);
        return (result.Success, result.Message);
    }

    public static async Task<SunshineSelfHealResult> SubmitPinWithSelfHealDetailedAsync(
        string pin,
        string clientName,
        Func<(string Username, string Password)?> getCredentials,
        Func<CancellationToken, Task<(bool Ok, string Username, string Password, string? Error)>> reprovisionAndStartAsync,
        Func<Task<bool>> wakeHostAsync,
        Uri? baseUri = null,
        HttpClient? httpClient = null,
        CancellationToken ct = default,
        string? remoteClientIp = null)
    {
        var restarted = false;
        var creds = getCredentials();
        if (creds is null || string.IsNullOrEmpty(creds.Value.Password))
        {
            var prov = await reprovisionAndStartAsync(ct).ConfigureAwait(false);
            if (!prov.Ok)
                return new SunshineSelfHealResult(false, prov.Error ?? "初始化 Sunshine 托管凭据失败。");

            creds = (prov.Username, prov.Password);
            restarted = true;
            await WaitUntilLocalApiReadyAsync(creds.Value.Username, creds.Value.Password, requireAuth: true, baseUri, httpClient, ct).ConfigureAwait(false);
        }
        else
        {
            var probe = await VerifyLocalCredentialsAsync(creds.Value.Username, creds.Value.Password, baseUri, httpClient, ct).ConfigureAwait(false);
            if (!probe.Reachable)
            {
                await wakeHostAsync().ConfigureAwait(false);
                restarted = true;
                await WaitUntilLocalApiReadyAsync(creds.Value.Username, creds.Value.Password, requireAuth: false, baseUri, httpClient, ct).ConfigureAwait(false);

                var postWakeProbe = await VerifyLocalCredentialsAsync(creds.Value.Username, creds.Value.Password, baseUri, httpClient, ct).ConfigureAwait(false);
                if (postWakeProbe.Reachable && !postWakeProbe.Authorized)
                {
                    var healedAfterWake = await reprovisionAndStartAsync(ct).ConfigureAwait(false);
                    if (!healedAfterWake.Ok)
                        return new SunshineSelfHealResult(false, healedAfterWake.Error ?? "重置并修复 Sunshine 管理员凭据失败。");

                    creds = (healedAfterWake.Username, healedAfterWake.Password);
                    await WaitUntilLocalApiReadyAsync(creds.Value.Username, creds.Value.Password, requireAuth: true, baseUri, httpClient, ct).ConfigureAwait(false);
                }
            }
            else if (!probe.Authorized)
            {
                var prov = await reprovisionAndStartAsync(ct).ConfigureAwait(false);
                if (!prov.Ok)
                    return new SunshineSelfHealResult(false, prov.Error ?? "重置并修复 Sunshine 管理员凭据失败。");

                creds = (prov.Username, prov.Password);
                restarted = true;
                await WaitUntilLocalApiReadyAsync(creds.Value.Username, creds.Value.Password, requireAuth: true, baseUri, httpClient, ct).ConfigureAwait(false);
            }
        }

        var attempt = await SubmitPairingPinDetailedAsync(
            pin, clientName, creds.Value.Username, creds.Value.Password, baseUri, httpClient, ct, remoteClientIp).ConfigureAwait(false);

        if (attempt.Success)
            return new SunshineSelfHealResult(true, attempt.Message, SunshineRestarted: false);

        if (attempt.IsUnauthorized)
        {
            var healed = await reprovisionAndStartAsync(ct).ConfigureAwait(false);
            if (!healed.Ok)
                return new SunshineSelfHealResult(false, healed.Error ?? "重置并修复 Sunshine 管理员凭据失败。");

            await WaitUntilLocalApiReadyAsync(healed.Username, healed.Password, requireAuth: true, baseUri, httpClient, ct).ConfigureAwait(false);
            var retried = await SubmitPairingPinDetailedAsync(
                pin, clientName, healed.Username, healed.Password, baseUri, httpClient, ct, remoteClientIp).ConfigureAwait(false);
            if (retried.Success)
                return new SunshineSelfHealResult(true, retried.Message, SunshineRestarted: false);

            return new SunshineSelfHealResult(false, "Sunshine 已自动修复托管凭据并重启，正在重新发起配对握手。", SunshineRestarted: true);
        }

        if (attempt.IsUnreachable)
        {
            await wakeHostAsync().ConfigureAwait(false);
            restarted = true;
            await WaitUntilLocalApiReadyAsync(creds.Value.Username, creds.Value.Password, requireAuth: false, baseUri, httpClient, ct).ConfigureAwait(false);
            var retried = await SubmitPairingPinDetailedAsync(
                pin, clientName, creds.Value.Username, creds.Value.Password, baseUri, httpClient, ct, remoteClientIp).ConfigureAwait(false);
            if (retried.Success)
                return new SunshineSelfHealResult(true, retried.Message, SunshineRestarted: false);

            return new SunshineSelfHealResult(false, retried.Message, SunshineRestarted: restarted);
        }

        if (restarted)
            return new SunshineSelfHealResult(false, "Sunshine 已就绪，正在重新发起配对握手。", SunshineRestarted: true);

        return new SunshineSelfHealResult(false, attempt.Message, SunshineRestarted: false);
    }

    private static async Task WaitUntilLocalApiReadyAsync(
        string username,
        string password,
        bool requireAuth,
        Uri? baseUri,
        HttpClient? httpClient,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var (reachable, authorized) = await VerifyLocalCredentialsAsync(
                username, password, baseUri, httpClient, ct).ConfigureAwait(false);
            if (reachable && (!requireAuth || authorized))
                return;

            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    public static async Task<SunshinePendingPairingsQueryResult> GetPendingPairingsAsync(
        string username,
        string password,
        Uri? baseUri = null,
        HttpClient? httpClient = null,
        CancellationToken ct = default)
    {
        var targetBase = baseUri ?? new Uri($"https://127.0.0.1:{DefaultControlPort}");
        var ownsClient = httpClient is null;
        var client = httpClient ?? CreateLocalApiClient();
        try
        {
            var endpoint = new Uri(targetBase, "/api/pin");
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new SunshinePendingPairingsQueryResult(true, true, false, []);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
                return new SunshinePendingPairingsQueryResult(false, false, false, []);

            if (!response.IsSuccessStatusCode)
                return new SunshinePendingPairingsQueryResult(false, false, false, []);

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
                return new SunshinePendingPairingsQueryResult(false, false, false, []);

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("pairings", out var pairingsProp) ||
                pairingsProp.ValueKind != JsonValueKind.Array)
            {
                return new SunshinePendingPairingsQueryResult(false, false, false, []);
            }

            var list = new List<SunshinePendingPairing>();
            foreach (var item in pairingsProp.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var id = item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                    ? idProp.GetString()?.Trim()
                    : null;
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var name = item.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                    ? nameProp.GetString()
                    : null;
                var address = item.TryGetProperty("address", out var addrProp) && addrProp.ValueKind == JsonValueKind.String
                    ? addrProp.GetString()
                    : null;
                list.Add(new SunshinePendingPairing(id, name, address));
            }

            return new SunshinePendingPairingsQueryResult(true, false, false, list);
        }
        catch (JsonException)
        {
            return new SunshinePendingPairingsQueryResult(false, false, false, []);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new SunshinePendingPairingsQueryResult(false, false, true, []);
        }
        finally
        {
            if (ownsClient)
                client.Dispose();
        }
    }

    public static async Task<string?> TryFetchCsrfTokenAsync(
        string username,
        string password,
        Uri? baseUri = null,
        HttpClient? httpClient = null,
        CancellationToken ct = default)
    {
        var targetBase = baseUri ?? new Uri($"https://127.0.0.1:{DefaultControlPort}");
        var ownsClient = httpClient is null;
        var client = httpClient ?? CreateLocalApiClient(TimeSpan.FromSeconds(3));
        try
        {
            var endpoint = new Uri(targetBase, "/api/csrf-token");
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
                return null;

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("csrf_token", out var tokProp) &&
                tokProp.ValueKind == JsonValueKind.String)
            {
                var tok = tokProp.GetString();
                return string.IsNullOrWhiteSpace(tok) ? null : tok;
            }
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
        finally
        {
            if (ownsClient)
                client.Dispose();
        }
    }

    internal static IReadOnlyList<SunshinePendingPairing> OrderPendingPairings(
        IReadOnlyList<SunshinePendingPairing> pairings,
        string? remoteClientIp)
    {
        if (pairings.Count <= 1)
            return pairings;

        var targetIp = NormalizeIpAddress(remoteClientIp);
        var matched = new List<SunshinePendingPairing>();
        var others = new List<SunshinePendingPairing>();

        for (var i = pairings.Count - 1; i >= 0; i--)
        {
            var p = pairings[i];
            var pIp = NormalizeIpAddress(p.Address);
            if (targetIp is not null && pIp is not null && string.Equals(targetIp, pIp, StringComparison.OrdinalIgnoreCase))
                matched.Add(p);
            else
                others.Add(p);
        }

        if (matched.Count == 0)
            return others;

        matched.AddRange(others);
        return matched;
    }

    private static string? NormalizeIpAddress(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']') && trimmed.Length > 2)
            trimmed = trimmed[1..^1];

        var scopeIdx = trimmed.IndexOf('%');
        if (scopeIdx >= 0)
            trimmed = trimmed[..scopeIdx];

        const string mappedPrefix = "::ffff:";
        if (trimmed.StartsWith(mappedPrefix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[mappedPrefix.Length..];

        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    public static async Task<(bool Success, string Message)> SubmitPairingPinAsync(
        string pin,
        string clientName,
        string username,
        string password,
        Uri? baseUri = null,
        HttpClient? httpClient = null,
        CancellationToken ct = default)
    {
        var detailed = await SubmitPairingPinDetailedAsync(
            pin, clientName, username, password, baseUri, httpClient, ct).ConfigureAwait(false);
        return (detailed.Success, detailed.Message);
    }

    public static async Task<SunshinePinSubmitResult> SubmitPairingPinDetailedAsync(
        string pin,
        string clientName,
        string username,
        string password,
        Uri? baseUri = null,
        HttpClient? httpClient = null,
        CancellationToken ct = default,
        string? remoteClientIp = null)
    {
        if (string.IsNullOrWhiteSpace(pin) || pin.Trim().Length != 4 || !pin.Trim().All(char.IsAsciiDigit))
            return new SunshinePinSubmitResult(false, false, false, "PIN 码必须为 4 位数字。");

        var normalizedPin = pin.Trim();
        var normalizedClient = string.IsNullOrWhiteSpace(clientName) ? "RemoteDesk-Client" : clientName.Trim();
        var targetBase = baseUri ?? new Uri($"https://127.0.0.1:{DefaultControlPort}");

        var ownsClient = httpClient is null;
        var client = httpClient ?? CreateLocalApiClient(TimeSpan.FromSeconds(20));
        var localApiConfirmedReachable = false;
        try
        {
            var pendingQuery = await GetPendingPairingsAsync(username, password, targetBase, client, ct).ConfigureAwait(false);
            if (pendingQuery.IsUnauthorized)
            {
                return new SunshinePinSubmitResult(
                    false,
                    IsUnauthorized: true,
                    IsUnreachable: false,
                    "Sunshine 管理员凭据鉴权失败（HTTP 401）。");
            }

            if (pendingQuery.IsUnreachable)
            {
                return new SunshinePinSubmitResult(
                    false,
                    IsUnauthorized: false,
                    IsUnreachable: true,
                    "无法连接本地 Sunshine API。");
            }

            localApiConfirmedReachable = true;

            if (pendingQuery.Supported)
            {
                var pairings = pendingQuery.Pairings;
                if (pairings.Count == 0)
                {
                    var pollDeadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(3000);
                    while (DateTime.UtcNow < pollDeadline && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(200, ct).ConfigureAwait(false);
                        pendingQuery = await GetPendingPairingsAsync(username, password, targetBase, client, ct).ConfigureAwait(false);
                        if (pendingQuery.IsUnauthorized)
                        {
                            return new SunshinePinSubmitResult(false, true, false, "Sunshine 管理员凭据鉴权失败（HTTP 401）。");
                        }
                        if (pendingQuery.IsUnreachable)
                        {
                            return new SunshinePinSubmitResult(false, false, true, "无法连接本地 Sunshine API。");
                        }
                        localApiConfirmedReachable = true;
                        if (!pendingQuery.Supported || pendingQuery.Pairings.Count > 0)
                        {
                            pairings = pendingQuery.Pairings;
                            break;
                        }
                    }
                }

                if (pendingQuery.Supported)
                {
                    if (pairings.Count == 0)
                    {
                        return new SunshinePinSubmitResult(
                            false,
                            IsUnauthorized: false,
                            IsUnreachable: false,
                            "Sunshine 暂未收到待配对会话（请确认目标主机 47989 端口可达且配对未超时）。");
                    }

                    var ordered = OrderPendingPairings(pairings, remoteClientIp);
                    var csrfToken = await TryFetchCsrfTokenAsync(username, password, targetBase, client, ct).ConfigureAwait(false);
                    SunshinePinSubmitResult? lastFailure = null;

                    foreach (var candidate in ordered)
                    {
                        var payloadJson = JsonSerializer.Serialize(
                            new { pairing_id = candidate.Id, pin = normalizedPin, name = normalizedClient },
                            JsonOptions);
                        var result = await PostPinPayloadWithCsrfRetryAsync(
                            targetBase,
                            client,
                            username,
                            password,
                            payloadJson,
                            csrfToken,
                            ct).ConfigureAwait(false);

                        if (result.Success || result.IsUnauthorized || result.IsUnreachable)
                            return result;

                        lastFailure = result;
                    }

                    return lastFailure ?? new SunshinePinSubmitResult(false, false, false, "Sunshine 未匹配到待配对会话（可能超时或已配对）。");
                }
            }

            var legacyJson = JsonSerializer.Serialize(new { pin = normalizedPin, name = normalizedClient }, JsonOptions);
            return await PostPinPayloadWithCsrfRetryAsync(
                targetBase,
                client,
                username,
                password,
                legacyJson,
                csrfToken: null,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (ex is TaskCanceledException && localApiConfirmedReachable)
            {
                return new SunshinePinSubmitResult(
                    false,
                    IsUnauthorized: false,
                    IsUnreachable: false,
                    "等待客户端完成 Sunshine 配对握手超时，请重试。");
            }

            return new SunshinePinSubmitResult(false, false, IsUnreachable: true, $"无法连接本地 Sunshine API：{ex.Message}");
        }
        finally
        {
            if (ownsClient)
                client.Dispose();
        }
    }

    private static async Task<SunshinePinSubmitResult> PostPinPayloadWithCsrfRetryAsync(
        Uri targetBase,
        HttpClient client,
        string username,
        string password,
        string payloadJson,
        string? csrfToken,
        CancellationToken ct)
    {
        var tokenToUse = csrfToken;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var endpoint = new Uri(targetBase, "/api/pin");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            if (!string.IsNullOrWhiteSpace(tokenToUse))
                request.Headers.TryAddWithoutValidation("X-CSRF-Token", tokenToUse);
            request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new SunshinePinSubmitResult(
                    false,
                    IsUnauthorized: true,
                    IsUnreachable: false,
                    $"Sunshine 管理员凭据鉴权失败（HTTP {(int)response.StatusCode}）。");
            }

            if (response.StatusCode == HttpStatusCode.BadRequest &&
                attempt == 0 &&
                TryExtractApiError(body) is { } badReqError &&
                badReqError.Contains("CSRF", StringComparison.OrdinalIgnoreCase))
            {
                tokenToUse = await TryFetchCsrfTokenAsync(username, password, targetBase, client, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(tokenToUse))
                    continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var apiErr = TryExtractApiError(body);
                return new SunshinePinSubmitResult(
                    false,
                    IsUnauthorized: false,
                    IsUnreachable: false,
                    string.IsNullOrWhiteSpace(apiErr)
                        ? $"Sunshine 配对接口拒绝请求（HTTP {(int)response.StatusCode}）。"
                        : $"Sunshine 配对接口拒绝请求（HTTP {(int)response.StatusCode}：{apiErr}）。");
            }

            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("status", out var statusProp))
                    {
                        var ok = statusProp.ValueKind switch
                        {
                            JsonValueKind.True => true,
                            JsonValueKind.String => string.Equals(statusProp.GetString(), "true", StringComparison.OrdinalIgnoreCase),
                            _ => false,
                        };
                        if (ok)
                            return new SunshinePinSubmitResult(true, false, false, "Sunshine 已静默完成配对。");

                        var errMsg = TryExtractApiError(body);
                        return new SunshinePinSubmitResult(
                            false,
                            false,
                            false,
                            string.IsNullOrWhiteSpace(errMsg)
                                ? "Sunshine 未匹配到待配对会话（可能超时或已配对）。"
                                : $"Sunshine 配对未通过：{errMsg}");
                    }
                }
                catch (JsonException)
                {
                    // Treat HTTP 200 with non-JSON body as success unless status=false explicitly appears.
                }
            }

            return new SunshinePinSubmitResult(true, false, false, "Sunshine 已静默完成配对。");
        }

        return new SunshinePinSubmitResult(false, false, false, "Sunshine 配对请求未通过 CSRF 校验。");
    }

    private static string? TryExtractApiError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var errProp) &&
                errProp.ValueKind == JsonValueKind.String)
            {
                return errProp.GetString()?.Trim();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static HttpClient CreateLocalApiClient(TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                AllowTlsResume = false,
                RemoteCertificateValidationCallback = (sender, _, _, _) =>
                    sender is SslStream ssl &&
                    (string.Equals(ssl.TargetHostName, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(ssl.TargetHostName, "localhost", StringComparison.OrdinalIgnoreCase)),
            },
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(5),
        };
    }

    private static void UpsertConfigKey(List<string> lines, string key, string value)
    {
        var newLine = $"{key} = {value}";
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith('#') || trimmed.StartsWith(';'))
                continue;

            var eqIndex = trimmed.IndexOf('=');
            if (eqIndex <= 0)
                continue;

            var existingKey = trimmed[..eqIndex].Trim();
            if (string.Equals(existingKey, key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = newLine;
                return;
            }
        }

        lines.Add(newLine);
    }

    private static bool IsExecutable(string? path) =>
        path is not null &&
        Path.IsPathFullyQualified(path) &&
        string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) &&
        File.Exists(path);
}
