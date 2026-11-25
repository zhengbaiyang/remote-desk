using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteDesk;

internal sealed record SunshinePinSubmitResult(
    bool Success,
    bool IsUnauthorized,
    bool IsUnreachable,
    string Message);

internal static class SunshineManager
{
    public const string DefaultAdminUser = "remotedesk";
    public const int DefaultControlPort = 47990;
    public const int DefaultStreamPort = 47989;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

    public static void EnsureSafeLocalConfig(string configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath))
            throw new ArgumentException("Sunshine 配置文件路径无效。", nameof(configPath));

        var fullPath = Path.GetFullPath(configPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var lines = File.Exists(fullPath)
            ? File.ReadAllLines(fullPath, Encoding.UTF8).ToList()
            : new List<string>();

        UpsertConfigKey(lines, "origin_web_ui_allowed", "pc");
        UpsertConfigKey(lines, "upnp", "off");

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
            EnsureSafeLocalConfig(configPath);

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
        CancellationToken ct = default)
    {
        var creds = getCredentials();
        if (creds is null || string.IsNullOrEmpty(creds.Value.Password))
        {
            var prov = await reprovisionAndStartAsync(ct).ConfigureAwait(false);
            if (!prov.Ok)
                return (false, prov.Error ?? "初始化 Sunshine 托管凭据失败。");

            creds = (prov.Username, prov.Password);
            await WaitUntilLocalApiReadyAsync(creds.Value.Username, creds.Value.Password, requireAuth: true, baseUri, httpClient, ct).ConfigureAwait(false);
        }

        var probe = await VerifyLocalCredentialsAsync(creds.Value.Username, creds.Value.Password, baseUri, httpClient, ct).ConfigureAwait(false);
        if (!probe.Reachable)
        {
            await wakeHostAsync().ConfigureAwait(false);
            await WaitUntilLocalApiReadyAsync(creds.Value.Username, creds.Value.Password, requireAuth: false, baseUri, httpClient, ct).ConfigureAwait(false);
        }

        var attempt = await SubmitPairingPinDetailedAsync(
            pin, clientName, creds.Value.Username, creds.Value.Password, baseUri, httpClient, ct).ConfigureAwait(false);

        if (attempt.Success)
            return (true, attempt.Message);

        if (attempt.IsUnauthorized)
        {
            var healed = await reprovisionAndStartAsync(ct).ConfigureAwait(false);
            if (!healed.Ok)
                return (false, healed.Error ?? "重置并修复 Sunshine 管理员凭据失败。");

            await WaitUntilLocalApiReadyAsync(healed.Username, healed.Password, requireAuth: true, baseUri, httpClient, ct).ConfigureAwait(false);
            var retried = await SubmitPairingPinDetailedAsync(
                pin, clientName, healed.Username, healed.Password, baseUri, httpClient, ct).ConfigureAwait(false);
            return (retried.Success, retried.Message);
        }

        if (attempt.IsUnreachable)
        {
            await wakeHostAsync().ConfigureAwait(false);
            await WaitUntilLocalApiReadyAsync(creds.Value.Username, creds.Value.Password, requireAuth: false, baseUri, httpClient, ct).ConfigureAwait(false);
            var retried = await SubmitPairingPinDetailedAsync(
                pin, clientName, creds.Value.Username, creds.Value.Password, baseUri, httpClient, ct).ConfigureAwait(false);
            return (retried.Success, retried.Message);
        }

        return (false, attempt.Message);
    }

    private static async Task WaitUntilLocalApiReadyAsync(
        string username,
        string password,
        bool requireAuth,
        Uri? baseUri,
        HttpClient? httpClient,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var (reachable, authorized) = await VerifyLocalCredentialsAsync(
                username, password, baseUri, httpClient, ct).ConfigureAwait(false);
            if (reachable && (!requireAuth || authorized))
                return;

            await Task.Delay(250, ct).ConfigureAwait(false);
        }
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
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pin) || pin.Trim().Length != 4 || !pin.Trim().All(char.IsAsciiDigit))
            return new SunshinePinSubmitResult(false, false, false, "PIN 码必须为 4 位数字。");

        var normalizedPin = pin.Trim();
        var normalizedClient = string.IsNullOrWhiteSpace(clientName) ? "RemoteDesk-Client" : clientName.Trim();
        var targetBase = baseUri ?? new Uri($"https://127.0.0.1:{DefaultControlPort}");

        var ownsClient = httpClient is null;
        var client = httpClient ?? CreateLocalApiClient();
        try
        {
            var endpoint = new Uri(targetBase, "/api/pin");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            var json = JsonSerializer.Serialize(new { pin = normalizedPin, name = normalizedClient }, JsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

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

            if (!response.IsSuccessStatusCode)
            {
                return new SunshinePinSubmitResult(
                    false,
                    IsUnauthorized: false,
                    IsUnreachable: false,
                    $"Sunshine 配对接口拒绝请求（HTTP {(int)response.StatusCode}）。");
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
                        return ok
                            ? new SunshinePinSubmitResult(true, false, false, "Sunshine 已静默完成配对。")
                            : new SunshinePinSubmitResult(false, false, false, "Sunshine 未匹配到待配对会话（可能超时或已配对）。");
                    }
                }
                catch (JsonException)
                {
                    // Treat HTTP 200 with non-JSON body as success unless status=false explicitly appears.
                }
            }

            return new SunshinePinSubmitResult(true, false, false, "Sunshine 已静默完成配对。");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new SunshinePinSubmitResult(false, false, IsUnreachable: true, $"无法连接本地 Sunshine API：{ex.Message}");
        }
        finally
        {
            if (ownsClient)
                client.Dispose();
        }
    }

    private static HttpClient CreateLocalApiClient(TimeSpan? timeout = null)
    {
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (req, _, _, _) =>
            {
                var host = req.RequestUri?.Host;
                return string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
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
