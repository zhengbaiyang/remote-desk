using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteDesk;

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

    public static async Task<(bool Success, string Message)> SubmitPairingPinAsync(
        string pin,
        string clientName,
        string username,
        string password,
        Uri? baseUri = null,
        HttpClient? httpClient = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pin) || pin.Trim().Length != 4 || !pin.Trim().All(char.IsAsciiDigit))
            return (false, "PIN 码必须为 4 位数字。");

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

            if (!response.IsSuccessStatusCode)
                return (false, $"Sunshine 配对接口拒绝请求（HTTP {(int)response.StatusCode}）。");

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
                            ? (true, "Sunshine 已静默完成配对。")
                            : (false, "Sunshine 未匹配到待配对会话（可能超时或已配对）。");
                    }
                }
                catch (JsonException)
                {
                    // Treat HTTP 200 with non-JSON body as success unless status=false explicitly appears.
                }
            }

            return (true, "Sunshine 已静默完成配对。");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, $"无法连接本地 Sunshine API：{ex.Message}");
        }
        finally
        {
            if (ownsClient)
                client.Dispose();
        }
    }

    private static HttpClient CreateLocalApiClient()
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
            Timeout = TimeSpan.FromSeconds(5),
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
