using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteDesk;

internal sealed class AgentPeerClient : IDisposable
{
    private const long MaxUploadBytes = 200L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(120);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public AgentPeerClient(HttpMessageHandler? customHandler = null)
    {
        if (customHandler is not null)
        {
            _httpClient = new HttpClient(customHandler, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            _ownsClient = false;
        }
        else
        {
            _httpClient = new HttpClient(new HttpClientHandler { UseProxy = false }, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            _ownsClient = true;
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
            _httpClient.Dispose();
    }

    public async Task<(bool Ok, AgentPeerInfoDto? Info, string? Error)> GetInfoAsync(
        string targetHost,
        int agentPort,
        string agentAuthKey,
        CancellationToken ct = default)
    {
        var validatedHost = DeviceProfile.ValidateHostAddress(targetHost, nameof(targetHost));
        var formattedHost = validatedHost.Contains(':') ? $"[{validatedHost}]" : validatedHost;
        var baseUrl = $"http://{formattedHost}:{agentPort}";
        const string path = "/agent/v1/info";

        try
        {
            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            reqCts.CancelAfter(RequestTimeout);
            using var req = BuildSignedRequest(HttpMethod.Get, baseUrl, path, [], agentAuthKey);
            using var resp = await _httpClient.SendAsync(req, reqCts.Token).ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync(reqCts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (false, null, ExtractErrorMessage(raw, (int)resp.StatusCode));

            var info = JsonSerializer.Deserialize<AgentPeerInfoDto>(raw, JsonOptions);
            return info is not null
                ? (true, info, null)
                : (false, null, "目标节点返回了无效的状态响应。");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (false, null, $"无法连接目标节点控制服务（{ex.Message}）。");
        }
    }

    public async Task<AgentPairResponseDto> SubmitPairPinAsync(
        string targetHost,
        int agentPort,
        string agentAuthKey,
        string pin,
        string clientName,
        string? securityPin = null,
        CancellationToken ct = default)
    {
        var baseUrl = BuildBaseUrl(targetHost, agentPort);
        const string path = "/agent/v1/pair";

        var payload = new AgentPairRequestDto(pin, clientName, securityPin);
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);

        try
        {
            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            reqCts.CancelAfter(TimeSpan.FromSeconds(22));
            using var req = BuildSignedRequest(HttpMethod.Post, baseUrl, path, bodyBytes, agentAuthKey);
            using var resp = await _httpClient.SendAsync(req, reqCts.Token).ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync(reqCts.Token).ConfigureAwait(false);

            var parsed = TryDeserializePairResponse(raw);
            if (parsed is not null)
                return parsed;

            return resp.IsSuccessStatusCode
                ? new AgentPairResponseDto(true, null, "配对请求已完成。")
                : new AgentPairResponseDto(false, $"HTTP_{(int)resp.StatusCode}", $"目标节点返回错误（HTTP {(int)resp.StatusCode}）。");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new AgentPairResponseDto(false, "NETWORK_ERROR", $"无法连接到目标节点的自动配对服务：{ex.Message}");
        }
    }

    public async Task<(bool Ok, AgentClipboardDto? Clipboard, string? Error)> GetClipboardAsync(
        string targetHost,
        int agentPort,
        string agentAuthKey,
        CancellationToken ct = default)
    {
        var (ok, data, err) = await SendSignedJsonAsync<AgentClipboardDto>(
            HttpMethod.Get,
            targetHost,
            agentPort,
            "/agent/v1/clipboard",
            null,
            agentAuthKey,
            RequestTimeout,
            ct).ConfigureAwait(false);
        return (ok, data, err);
    }

    public async Task<AgentClipboardSetResponseDto> SetClipboardAsync(
        string targetHost,
        int agentPort,
        string agentAuthKey,
        AgentClipboardSetRequestDto payload,
        CancellationToken ct = default)
    {
        var (ok, data, err) = await SendSignedJsonAsync<AgentClipboardSetResponseDto>(
            HttpMethod.Post,
            targetHost,
            agentPort,
            "/agent/v1/clipboard",
            payload,
            agentAuthKey,
            TimeSpan.FromSeconds(15),
            ct).ConfigureAwait(false);
        return data ?? new AgentClipboardSetResponseDto(ok, null, ok ? null : "NETWORK_ERROR", err);
    }

    public async Task<AgentFileUploadResultDto> UploadFileAsync(
        string targetHost,
        int agentPort,
        string agentAuthKey,
        string localFilePath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath))
            return new AgentFileUploadResultDto(false, Error: "FILE_NOT_FOUND", Message: "找不到要发送的本地文件。");

        var fileInfo = new FileInfo(localFilePath);
        if (fileInfo.Length > MaxUploadBytes)
            return new AgentFileUploadResultDto(false, Error: "FILE_TOO_LARGE", Message: "单次发送文件不能超过 200 MB。");

        var baseUrl = BuildBaseUrl(targetHost, agentPort);
        const string path = "/agent/v1/files/upload";

        try
        {
            var sanitizedLeaf = FileTransferPathHelper.SanitizeFileName(fileInfo.Name);
            var encodedName = Convert.ToBase64String(Encoding.UTF8.GetBytes(sanitizedLeaf));

            byte[] hashBytes;
            await using (var hashStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true))
            {
                hashBytes = await SHA256.HashDataAsync(hashStream, ct).ConfigureAwait(false);
            }
            var sha256Hex = Convert.ToHexString(hashBytes).ToLowerInvariant();

            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            var sig = AgentHmacAuth.ComputeSignatureFromBodyHash(agentAuthKey, "POST", path, ts, nonce, sha256Hex);

            using var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            uploadCts.CancelAfter(UploadTimeout);

            await using var readStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            using var content = new ProgressFileStreamContent(readStream, fileInfo.Length, progress);
            content.Headers.ContentLength = fileInfo.Length;
            content.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");

            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + path) { Content = content };
            req.Headers.TryAddWithoutValidation(AgentHmacAuth.HeaderTimestamp, ts.ToString(CultureInfo.InvariantCulture));
            req.Headers.TryAddWithoutValidation(AgentHmacAuth.HeaderNonce, nonce);
            req.Headers.TryAddWithoutValidation(AgentHmacAuth.HeaderSignature, sig);
            req.Headers.TryAddWithoutValidation(AgentHmacAuth.HeaderFileName, encodedName);
            req.Headers.TryAddWithoutValidation(AgentHmacAuth.HeaderFileSha256, sha256Hex);

            using var resp = await _httpClient.SendAsync(req, uploadCts.Token).ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync(uploadCts.Token).ConfigureAwait(false);
            var parsed = TryDeserialize<AgentFileUploadResultDto>(raw);
            if (parsed is not null)
                return parsed;

            return resp.IsSuccessStatusCode
                ? new AgentFileUploadResultDto(true, sanitizedLeaf, SizeBytes: fileInfo.Length, Sha256: sha256Hex)
                : new AgentFileUploadResultDto(false, Error: $"HTTP_{(int)resp.StatusCode}", Message: ExtractErrorMessage(raw, (int)resp.StatusCode));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException or JsonException or ArgumentException)
        {
            return new AgentFileUploadResultDto(false, Error: "UPLOAD_FAILED", Message: $"发送文件失败：{ex.Message}");
        }
    }

    public async Task<(bool Ok, AgentDisplayStateDto? Display, string? Error)> GetDisplayStateAsync(
        string targetHost,
        int agentPort,
        string agentAuthKey,
        CancellationToken ct = default) =>
        await SendSignedJsonAsync<AgentDisplayStateDto>(
            HttpMethod.Get,
            targetHost,
            agentPort,
            "/agent/v1/display",
            null,
            agentAuthKey,
            RequestTimeout,
            ct).ConfigureAwait(false);

    public async Task<AgentDisplayApplyResultDto> ApplyDisplayModeAsync(
        string targetHost,
        int agentPort,
        string agentAuthKey,
        int width,
        int height,
        int refreshRate,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        var (ok, data, err) = await SendSignedJsonAsync<AgentDisplayApplyResultDto>(
            HttpMethod.Post,
            targetHost,
            agentPort,
            "/agent/v1/display/apply",
            new AgentDisplayApplyRequestDto(width, height, refreshRate, sessionId),
            agentAuthKey,
            RequestTimeout,
            ct).ConfigureAwait(false);
        return data ?? new AgentDisplayApplyResultDto(ok, false, Error: ok ? null : "NETWORK_ERROR", Message: err);
    }

    public async Task<AgentDisplayApplyResultDto> RestoreDisplayModeAsync(
        string targetHost,
        int agentPort,
        string agentAuthKey,
        CancellationToken ct = default)
    {
        var (ok, data, err) = await SendSignedJsonAsync<AgentDisplayApplyResultDto>(
            HttpMethod.Post,
            targetHost,
            agentPort,
            "/agent/v1/display/restore",
            new { },
            agentAuthKey,
            RequestTimeout,
            ct).ConfigureAwait(false);
        return data ?? new AgentDisplayApplyResultDto(ok, false, Error: ok ? null : "NETWORK_ERROR", Message: err);
    }

    public async Task<AgentPrivacyResultDto> SetPrivacyScreenAsync(
        string targetHost,
        int agentPort,
        string agentAuthKey,
        bool enabled,
        CancellationToken ct = default)
    {
        var (ok, data, err) = await SendSignedJsonAsync<AgentPrivacyResultDto>(
            HttpMethod.Post,
            targetHost,
            agentPort,
            "/agent/v1/privacy",
            new AgentPrivacyRequestDto(enabled),
            agentAuthKey,
            RequestTimeout,
            ct).ConfigureAwait(false);
        return data ?? new AgentPrivacyResultDto(ok, enabled, ok ? null : "NETWORK_ERROR", err);
    }

    public async Task<AgentPowerResultDto> ExecutePowerActionAsync(
        string targetHost,
        int agentPort,
        string agentAuthKey,
        string action,
        CancellationToken ct = default)
    {
        var (ok, data, err) = await SendSignedJsonAsync<AgentPowerResultDto>(
            HttpMethod.Post,
            targetHost,
            agentPort,
            "/agent/v1/power",
            new AgentPowerRequestDto(action),
            agentAuthKey,
            RequestTimeout,
            ct).ConfigureAwait(false);
        return data ?? new AgentPowerResultDto(ok, action, ok ? null : "NETWORK_ERROR", err);
    }

    private async Task<(bool Ok, T? Data, string? Error)> SendSignedJsonAsync<T>(
        HttpMethod method,
        string targetHost,
        int agentPort,
        string path,
        object? payload,
        string agentAuthKey,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var baseUrl = BuildBaseUrl(targetHost, agentPort);
        var bodyBytes = payload is null ? [] : JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);

        try
        {
            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            reqCts.CancelAfter(timeout);
            using var req = BuildSignedRequest(method, baseUrl, path, bodyBytes, agentAuthKey);
            using var resp = await _httpClient.SendAsync(req, reqCts.Token).ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync(reqCts.Token).ConfigureAwait(false);
            var parsed = TryDeserialize<T>(raw);
            if (!resp.IsSuccessStatusCode)
                return (false, parsed, ExtractErrorMessage(raw, (int)resp.StatusCode));

            return parsed is not null
                ? (true, parsed, null)
                : (false, default, "目标节点返回了无效的响应数据。");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (false, default, $"请求节点接口失败：{ex.Message}");
        }
    }

    private static string BuildBaseUrl(string targetHost, int agentPort)
    {
        var validatedHost = DeviceProfile.ValidateHostAddress(targetHost, nameof(targetHost));
        var formattedHost = validatedHost.Contains(':') ? $"[{validatedHost}]" : validatedHost;
        return $"http://{formattedHost}:{agentPort}";
    }

    private sealed class ProgressFileStreamContent(Stream source, long totalBytes, IProgress<double>? progress) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buffer = new byte[64 * 1024];
            long sent = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                    break;
                await stream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                sent += read;
                if (totalBytes > 0)
                    progress?.Report(Math.Clamp(sent * 100.0 / totalBytes, 0, 100));
            }
            if (totalBytes == 0)
                progress?.Report(100);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = totalBytes;
            return true;
        }
    }

    private static T? TryDeserialize<T>(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return default;
        try
        {
            return JsonSerializer.Deserialize<T>(raw, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static HttpRequestMessage BuildSignedRequest(
        HttpMethod method,
        string baseUrl,
        string path,
        byte[] bodyBytes,
        string authKey)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var sig = AgentHmacAuth.ComputeSignature(authKey, method.Method, path, ts, nonce, bodyBytes);

        var req = new HttpRequestMessage(method, baseUrl + path);
        req.Headers.TryAddWithoutValidation(AgentHmacAuth.HeaderTimestamp, ts.ToString(CultureInfo.InvariantCulture));
        req.Headers.TryAddWithoutValidation(AgentHmacAuth.HeaderNonce, nonce);
        req.Headers.TryAddWithoutValidation(AgentHmacAuth.HeaderSignature, sig);

        if (bodyBytes.Length > 0)
        {
            req.Content = new ByteArrayContent(bodyBytes);
            req.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        }

        return req;
    }

    private static AgentPairResponseDto? TryDeserializePairResponse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            return JsonSerializer.Deserialize<AgentPairResponseDto>(raw, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ExtractErrorMessage(string raw, int statusCode)
    {
        var parsed = TryDeserializePairResponse(raw);
        return parsed?.Message ?? $"目标节点返回错误（HTTP {statusCode}）。";
    }
}
