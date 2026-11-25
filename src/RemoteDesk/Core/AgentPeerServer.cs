using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RemoteDesk;

internal sealed record AgentPeerServerState(
    string DeviceKey,
    string Hostname,
    bool SunshineReady,
    int SunshinePort,
    string? AgentAuthKey,
    bool SecurityPinRequired,
    string? SecurityPinSalt,
    string? SecurityPinHash);

internal sealed record AgentPeerFeatureHandlers(
    Func<CancellationToken, Task<AgentClipboardDto>>? GetClipboardAsync = null,
    Func<AgentClipboardSetRequestDto, CancellationToken, Task<AgentClipboardSetResponseDto>>? SetClipboardAsync = null,
    Func<CancellationToken, Task<string>>? ResolveUploadDirectoryAsync = null,
    Func<CancellationToken, Task<AgentDisplayStateDto>>? GetDisplayStateAsync = null,
    Func<AgentDisplayApplyRequestDto, CancellationToken, Task<AgentDisplayApplyResultDto>>? ApplyDisplayModeAsync = null,
    Func<CancellationToken, Task<AgentDisplayApplyResultDto>>? RestoreDisplayModeAsync = null,
    Func<bool, CancellationToken, Task<AgentPrivacyResultDto>>? SetPrivacyScreenAsync = null,
    Func<string, CancellationToken, Task<AgentPowerResultDto>>? ExecutePowerActionAsync = null);

internal sealed class AgentPeerServer : IDisposable
{
    private const int MaxHeaderBytes = 8192;
    private const int MaxBodyBytes = 32 * 1024;
    private const int MaxClipboardBodyBytes = 12 * 1024 * 1024;
    private const long MaxUploadBytes = 200L * 1024 * 1024;
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(120);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Func<AgentPeerServerState> _getState;
    private readonly Func<string, string, CancellationToken, Task<(bool Success, string Message)>> _submitPinCallback;
    private readonly AgentPeerFeatureHandlers _featureHandlers;
    private readonly AgentHmacAuth _hmacAuth = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;

    public AgentPeerServer(
        Func<AgentPeerServerState> getState,
        Func<string, string, CancellationToken, Task<(bool Success, string Message)>> submitPinCallback,
        AgentPeerFeatureHandlers? featureHandlers = null)
    {
        _getState = getState ?? throw new ArgumentNullException(nameof(getState));
        _submitPinCallback = submitPinCallback ?? throw new ArgumentNullException(nameof(submitPinCallback));
        _featureHandlers = featureHandlers ?? new AgentPeerFeatureHandlers();
    }

    public int BoundPort { get; private set; }

    public void Start(IPAddress bindAddress, int port)
    {
        if (_listener is not null)
            return;

        var listener = new TcpListener(bindAddress, port);
        listener.Start();
        BoundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        _listener = listener;
        _cts = new CancellationTokenSource();
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
        }

        _listener = null;
        BoundPort = 0;
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        client.ReceiveTimeout = (int)UploadTimeout.TotalMilliseconds;
                        client.SendTimeout = (int)UploadTimeout.TotalMilliseconds;
                        await using var stream = client.GetStream();
                        await HandleConnectionStreamAsync(stream, ct).ConfigureAwait(false);
                    }
                }, ct);
            }
            catch (OperationCanceledException)
            {
                client?.Dispose();
                break;
            }
            catch (ObjectDisposedException)
            {
                client?.Dispose();
                break;
            }
            catch (SocketException)
            {
                client?.Dispose();
                if (ct.IsCancellationRequested)
                    break;
            }
        }
    }

    internal async Task HandleConnectionStreamAsync(Stream stream, CancellationToken ct)
    {
        using var headerTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerTimeoutCts.CancelAfter(IoTimeout);
        var headerCt = headerTimeoutCts.Token;

        var headerBytes = await ReadHeaderBytesAsync(stream, headerCt).ConfigureAwait(false);
        if (headerBytes is null || headerBytes.Length == 0)
            return;

        var headerText = Encoding.ASCII.GetString(headerBytes);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0)
        {
            await WriteJsonResponseAsync(stream, 400, "Bad Request", new { error = "BAD_REQUEST", message = "无效的 HTTP 请求。" }, headerCt).ConfigureAwait(false);
            return;
        }

        var requestParts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestParts.Length < 2)
        {
            await WriteJsonResponseAsync(stream, 400, "Bad Request", new { error = "BAD_REQUEST", message = "请求行格式无效。" }, headerCt).ConfigureAwait(false);
            return;
        }

        var method = requestParts[0].ToUpperInvariant();
        var rawTarget = requestParts[1];
        var queryIndex = rawTarget.IndexOf('?');
        var path = queryIndex >= 0 ? rawTarget[..queryIndex] : rawTarget;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrEmpty(line))
                continue;
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var state = _getState();
        if (string.IsNullOrEmpty(state.AgentAuthKey))
        {
            await WriteJsonResponseAsync(stream, 401, "Unauthorized", new { error = "AGENT_UNCONFIGURED", message = "被控节点尚未绑定同账号认证密钥。" }, headerCt).ConfigureAwait(false);
            return;
        }

        if (method == "POST" && path == "/agent/v1/files/upload")
        {
            await HandleFileUploadAsync(stream, headers, state, path, ct).ConfigureAwait(false);
            return;
        }

        var maxAllowedBody = (method == "POST" && path == "/agent/v1/clipboard")
            ? MaxClipboardBodyBytes
            : MaxBodyBytes;

        var contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var rawLength))
        {
            if (!int.TryParse(rawLength, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength) ||
                contentLength < 0 ||
                contentLength > maxAllowedBody)
            {
                await WriteJsonResponseAsync(stream, 413, "Payload Too Large", new { error = "PAYLOAD_TOO_LARGE", message = "请求体过大或长度无效。" }, headerCt).ConfigureAwait(false);
                return;
            }
        }

        using var ioTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ioTimeout = (method == "POST" && path == "/agent/v1/pair")
            ? TimeSpan.FromSeconds(24)
            : (method == "POST" && path == "/agent/v1/clipboard" ? TimeSpan.FromSeconds(20) : IoTimeout);
        ioTimeoutCts.CancelAfter(ioTimeout);
        var effectiveCt = ioTimeoutCts.Token;

        var body = contentLength > 0 ? await ReadExactAsync(stream, contentLength, effectiveCt).ConfigureAwait(false) : [];
        if (body is null)
        {
            await WriteJsonResponseAsync(stream, 400, "Bad Request", new { error = "INCOMPLETE_BODY", message = "请求体读取不完整。" }, effectiveCt).ConfigureAwait(false);
            return;
        }

        if (!TryVerifySignature(headers, state.AgentAuthKey, method, path, bodyHashHex: null, body))
        {
            await WriteJsonResponseAsync(stream, 401, "Unauthorized", new { error = "INVALID_SIGNATURE", message = "同账号签名校验失败或已过期。" }, effectiveCt).ConfigureAwait(false);
            return;
        }

        if (method == "GET" && path == "/agent/v1/info")
        {
            var info = new AgentPeerInfoDto(
                DeviceKey: state.DeviceKey,
                Hostname: state.Hostname,
                SunshineReady: state.SunshineReady,
                SunshinePort: state.SunshinePort,
                SecurityPinRequired: state.SecurityPinRequired);
            await WriteJsonResponseAsync(stream, 200, "OK", info, effectiveCt).ConfigureAwait(false);
            return;
        }

        if (method == "POST" && path == "/agent/v1/pair")
        {
            AgentPairRequestDto? pairReq = TryDeserialize<AgentPairRequestDto>(body);
            if (pairReq is null ||
                string.IsNullOrWhiteSpace(pairReq.Pin) ||
                pairReq.Pin.Trim().Length != 4 ||
                !pairReq.Pin.Trim().All(char.IsAsciiDigit))
            {
                await WriteJsonResponseAsync(stream, 400, "Bad Request", new AgentPairResponseDto(false, "INVALID_PIN", "PIN 码必须为 4 位数字。"), effectiveCt).ConfigureAwait(false);
                return;
            }

            if (state.SecurityPinRequired)
            {
                if (string.IsNullOrEmpty(pairReq.SecurityPin))
                {
                    await WriteJsonResponseAsync(stream, 403, "Forbidden", new AgentPairResponseDto(false, "SECURITY_PIN_REQUIRED", "目标电脑已开启独立安全密码验证，请输入安全密码。"), effectiveCt).ConfigureAwait(false);
                    return;
                }

                if (!SecurityPinHasher.VerifyPin(pairReq.SecurityPin, state.SecurityPinSalt, state.SecurityPinHash))
                {
                    await WriteJsonResponseAsync(stream, 403, "Forbidden", new AgentPairResponseDto(false, "INVALID_SECURITY_PIN", "独立安全密码错误，请核对后重试。"), effectiveCt).ConfigureAwait(false);
                    return;
                }
            }

            var (ok, msg) = await _submitPinCallback(pairReq.Pin.Trim(), pairReq.ClientName, effectiveCt).ConfigureAwait(false);
            if (ok)
                await WriteJsonResponseAsync(stream, 200, "OK", new AgentPairResponseDto(true, null, msg), effectiveCt).ConfigureAwait(false);
            else
                await WriteJsonResponseAsync(stream, 502, "Bad Gateway", new AgentPairResponseDto(false, "SUNSHINE_PAIR_FAILED", msg), effectiveCt).ConfigureAwait(false);
            return;
        }

        if (method == "GET" && path == "/agent/v1/clipboard")
        {
            if (_featureHandlers.GetClipboardAsync is null)
            {
                await WriteJsonResponseAsync(stream, 501, "Not Implemented", new { error = "NOT_IMPLEMENTED", message = "当前节点未启用剪贴板协同。" }, effectiveCt).ConfigureAwait(false);
                return;
            }

            var clip = await _featureHandlers.GetClipboardAsync(effectiveCt).ConfigureAwait(false);
            await WriteJsonResponseAsync(stream, 200, "OK", clip, effectiveCt).ConfigureAwait(false);
            return;
        }

        if (method == "POST" && path == "/agent/v1/clipboard")
        {
            if (_featureHandlers.SetClipboardAsync is null)
            {
                await WriteJsonResponseAsync(stream, 501, "Not Implemented", new AgentClipboardSetResponseDto(false, null, "NOT_IMPLEMENTED", "当前节点未启用剪贴板协同。"), effectiveCt).ConfigureAwait(false);
                return;
            }

            var clipReq = TryDeserialize<AgentClipboardSetRequestDto>(body);
            if (clipReq is null || (clipReq.Kind != "text" && clipReq.Kind != "image"))
            {
                await WriteJsonResponseAsync(stream, 400, "Bad Request", new AgentClipboardSetResponseDto(false, null, "INVALID_CLIPBOARD", "无效的剪贴板数据类型。"), effectiveCt).ConfigureAwait(false);
                return;
            }

            var result = await _featureHandlers.SetClipboardAsync(clipReq, effectiveCt).ConfigureAwait(false);
            await WriteJsonResponseAsync(stream, result.Ok ? 200 : 400, result.Ok ? "OK" : "Bad Request", result, effectiveCt).ConfigureAwait(false);
            return;
        }

        if (method == "GET" && path == "/agent/v1/display")
        {
            if (_featureHandlers.GetDisplayStateAsync is null)
            {
                await WriteJsonResponseAsync(stream, 501, "Not Implemented", new { error = "NOT_IMPLEMENTED", message = "当前节点不支持显示器控制。" }, effectiveCt).ConfigureAwait(false);
                return;
            }

            var display = await _featureHandlers.GetDisplayStateAsync(effectiveCt).ConfigureAwait(false);
            await WriteJsonResponseAsync(stream, 200, "OK", display, effectiveCt).ConfigureAwait(false);
            return;
        }

        if (method == "POST" && path == "/agent/v1/display/apply")
        {
            if (_featureHandlers.ApplyDisplayModeAsync is null)
            {
                await WriteJsonResponseAsync(stream, 501, "Not Implemented", new AgentDisplayApplyResultDto(false, false, Error: "NOT_IMPLEMENTED", Message: "当前节点不支持分辨率自动匹配。"), effectiveCt).ConfigureAwait(false);
                return;
            }

            var req = TryDeserialize<AgentDisplayApplyRequestDto>(body);
            if (req is null || req.Width <= 0 || req.Height <= 0 || req.RefreshRate <= 0)
            {
                await WriteJsonResponseAsync(stream, 400, "Bad Request", new AgentDisplayApplyResultDto(false, false, Error: "INVALID_DISPLAY_MODE", Message: "分辨率或刷新率参数无效。"), effectiveCt).ConfigureAwait(false);
                return;
            }

            var res = await _featureHandlers.ApplyDisplayModeAsync(req, effectiveCt).ConfigureAwait(false);
            await WriteJsonResponseAsync(stream, res.Ok ? 200 : 500, res.Ok ? "OK" : "Internal Server Error", res, effectiveCt).ConfigureAwait(false);
            return;
        }

        if (method == "POST" && path == "/agent/v1/display/restore")
        {
            if (_featureHandlers.RestoreDisplayModeAsync is null)
            {
                await WriteJsonResponseAsync(stream, 501, "Not Implemented", new AgentDisplayApplyResultDto(false, false, Error: "NOT_IMPLEMENTED", Message: "当前节点不支持分辨率还原。"), effectiveCt).ConfigureAwait(false);
                return;
            }

            var res = await _featureHandlers.RestoreDisplayModeAsync(effectiveCt).ConfigureAwait(false);
            await WriteJsonResponseAsync(stream, res.Ok ? 200 : 500, res.Ok ? "OK" : "Internal Server Error", res, effectiveCt).ConfigureAwait(false);
            return;
        }

        if (method == "POST" && path == "/agent/v1/privacy")
        {
            if (_featureHandlers.SetPrivacyScreenAsync is null)
            {
                await WriteJsonResponseAsync(stream, 501, "Not Implemented", new AgentPrivacyResultDto(false, false, "NOT_IMPLEMENTED", "当前节点不支持防窥黑屏。"), effectiveCt).ConfigureAwait(false);
                return;
            }

            var req = TryDeserialize<AgentPrivacyRequestDto>(body);
            if (req is null)
            {
                await WriteJsonResponseAsync(stream, 400, "Bad Request", new AgentPrivacyResultDto(false, false, "INVALID_REQUEST", "防窥黑屏参数无效。"), effectiveCt).ConfigureAwait(false);
                return;
            }

            var res = await _featureHandlers.SetPrivacyScreenAsync(req.Enabled, effectiveCt).ConfigureAwait(false);
            await WriteJsonResponseAsync(stream, res.Ok ? 200 : 400, res.Ok ? "OK" : "Bad Request", res, effectiveCt).ConfigureAwait(false);
            return;
        }

        if (method == "POST" && path == "/agent/v1/power")
        {
            var req = TryDeserialize<AgentPowerRequestDto>(body);
            var action = req?.Action?.Trim().ToLowerInvariant();
            if (action is not ("lock" or "restart" or "shutdown" or "cancel_shutdown"))
            {
                await WriteJsonResponseAsync(stream, 400, "Bad Request", new AgentPowerResultDto(false, req?.Action ?? "", "INVALID_POWER_ACTION", "不支持的电源操作类型。"), effectiveCt).ConfigureAwait(false);
                return;
            }

            if (_featureHandlers.ExecutePowerActionAsync is null)
            {
                await WriteJsonResponseAsync(stream, 501, "Not Implemented", new AgentPowerResultDto(false, action, "NOT_IMPLEMENTED", "当前节点未启用远程电源控制。"), effectiveCt).ConfigureAwait(false);
                return;
            }

            var res = await _featureHandlers.ExecutePowerActionAsync(action, effectiveCt).ConfigureAwait(false);
            await WriteJsonResponseAsync(stream, res.Ok ? 200 : 500, res.Ok ? "OK" : "Internal Server Error", res, effectiveCt).ConfigureAwait(false);
            return;
        }

        await WriteJsonResponseAsync(stream, 404, "Not Found", new { error = "NOT_FOUND", message = "未知的节点控制接口。" }, effectiveCt).ConfigureAwait(false);
    }

    private async Task HandleFileUploadAsync(
        Stream stream,
        Dictionary<string, string> headers,
        AgentPeerServerState state,
        string path,
        CancellationToken ct)
    {
        using var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        uploadCts.CancelAfter(UploadTimeout);
        var uploadCt = uploadCts.Token;

        if (!headers.TryGetValue("Content-Length", out var rawLen) ||
            !long.TryParse(rawLen, NumberStyles.Integer, CultureInfo.InvariantCulture, out var contentLength) ||
            contentLength < 0)
        {
            await WriteJsonResponseAsync(stream, 400, "Bad Request", new AgentFileUploadResultDto(false, Error: "INVALID_LENGTH", Message: "缺少有效的 Content-Length 请求头。"), uploadCt).ConfigureAwait(false);
            return;
        }

        if (contentLength > MaxUploadBytes)
        {
            await WriteJsonResponseAsync(stream, 413, "Payload Too Large", new AgentFileUploadResultDto(false, Error: "FILE_TOO_LARGE", Message: "单文件大小不能超过 200 MB。"), uploadCt).ConfigureAwait(false);
            return;
        }

        if (!headers.TryGetValue(AgentHmacAuth.HeaderFileName, out var encodedName) ||
            !headers.TryGetValue(AgentHmacAuth.HeaderFileSha256, out var rawDeclaredSha256))
        {
            await WriteJsonResponseAsync(stream, 400, "Bad Request", new AgentFileUploadResultDto(false, Error: "MISSING_FILE_HEADERS", Message: "缺少文件名或 SHA-256 校验头。"), uploadCt).ConfigureAwait(false);
            return;
        }

        string sanitizedName;
        try
        {
            var decodedName = Encoding.UTF8.GetString(Convert.FromBase64String(encodedName.Trim()));
            sanitizedName = FileTransferPathHelper.SanitizeFileName(decodedName);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            await WriteJsonResponseAsync(stream, 400, "Bad Request", new AgentFileUploadResultDto(false, Error: "INVALID_FILENAME", Message: "文件名编码或路径格式无效。"), uploadCt).ConfigureAwait(false);
            return;
        }

        var declaredSha256 = rawDeclaredSha256.Trim().ToLowerInvariant();
        if (declaredSha256.Length != 64 || !declaredSha256.All(char.IsAsciiHexDigitLower))
        {
            await WriteJsonResponseAsync(stream, 400, "Bad Request", new AgentFileUploadResultDto(false, Error: "INVALID_SHA256", Message: "SHA-256 摘要格式无效。"), uploadCt).ConfigureAwait(false);
            return;
        }

        // Authenticate against declared body SHA-256 before writing any bytes to disk.
        if (!TryVerifySignature(headers, state.AgentAuthKey, "POST", path, declaredSha256, body: null))
        {
            await WriteJsonResponseAsync(stream, 401, "Unauthorized", new AgentFileUploadResultDto(false, Error: "INVALID_SIGNATURE", Message: "文件上传签名校验失败。"), uploadCt).ConfigureAwait(false);
            return;
        }

        if (_featureHandlers.ResolveUploadDirectoryAsync is null)
        {
            await WriteJsonResponseAsync(stream, 501, "Not Implemented", new AgentFileUploadResultDto(false, Error: "NOT_IMPLEMENTED", Message: "当前节点未配置文件接收目录。"), uploadCt).ConfigureAwait(false);
            return;
        }

        var uploadDir = await _featureHandlers.ResolveUploadDirectoryAsync(uploadCt).ConfigureAwait(false);
        Directory.CreateDirectory(uploadDir);

        var tempPath = Path.Combine(uploadDir, $".rd-upload-{Guid.NewGuid():N}.tmp");
        try
        {
            using var hasher = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long remaining = contentLength;
            var incomplete = false;

            await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(buffer.Length, remaining);
                    var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), uploadCt).ConfigureAwait(false);
                    if (read == 0)
                    {
                        incomplete = true;
                        break;
                    }

                    hasher.AppendData(buffer, 0, read);
                    await fs.WriteAsync(buffer.AsMemory(0, read), uploadCt).ConfigureAwait(false);
                    remaining -= read;
                }
                await fs.FlushAsync(uploadCt).ConfigureAwait(false);
            }

            if (incomplete)
            {
                await WriteJsonResponseAsync(stream, 400, "Bad Request", new AgentFileUploadResultDto(false, Error: "INCOMPLETE_BODY", Message: "文件传输中断，接收字节数不足。"), uploadCt).ConfigureAwait(false);
                return;
            }

            var actualShaBytes = hasher.GetHashAndReset();
            var expectedShaBytes = Convert.FromHexString(declaredSha256);
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actualShaBytes, expectedShaBytes))
            {
                await WriteJsonResponseAsync(stream, 400, "Bad Request", new AgentFileUploadResultDto(false, Error: "CHECKSUM_MISMATCH", Message: "文件 SHA-256 校验不一致，已丢弃损坏文件。"), uploadCt).ConfigureAwait(false);
                return;
            }

            var finalPath = FileTransferPathHelper.ResolveUniqueTargetPath(uploadDir, sanitizedName);
            File.Move(tempPath, finalPath, overwrite: false);

            var actualShaHex = Convert.ToHexString(actualShaBytes).ToLowerInvariant();
            var finalName = Path.GetFileName(finalPath);
            await WriteJsonResponseAsync(
                stream,
                200,
                "OK",
                new AgentFileUploadResultDto(true, finalName, finalPath, contentLength, actualShaHex, null, $"文件已保存至 {finalPath}"),
                uploadCt).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private bool TryVerifySignature(
        Dictionary<string, string> headers,
        string? authKey,
        string method,
        string path,
        string? bodyHashHex,
        byte[]? body)
    {
        if (!headers.TryGetValue(AgentHmacAuth.HeaderTimestamp, out var rawTs) ||
            !long.TryParse(rawTs, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp) ||
            !headers.TryGetValue(AgentHmacAuth.HeaderNonce, out var nonce) ||
            !headers.TryGetValue(AgentHmacAuth.HeaderSignature, out var signature))
        {
            return false;
        }

        return bodyHashHex is not null
            ? _hmacAuth.VerifySignatureFromBodyHash(authKey, method, path, timestamp, nonce, bodyHashHex, signature)
            : _hmacAuth.VerifySignature(authKey, method, path, timestamp, nonce, body ?? [], signature);
    }

    private static T? TryDeserialize<T>(byte[] body)
    {
        if (body.Length == 0)
            return default;
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static async Task<byte[]?> ReadHeaderBytesAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new List<byte>(512);
        var single = new byte[1];
        while (buffer.Count < MaxHeaderBytes)
        {
            var read = await stream.ReadAsync(single.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (read == 0)
                return null;

            buffer.Add(single[0]);
            var count = buffer.Count;
            if (count >= 4 &&
                buffer[count - 4] == '\r' &&
                buffer[count - 3] == '\n' &&
                buffer[count - 2] == '\r' &&
                buffer[count - 1] == '\n')
            {
                return buffer.GetRange(0, count - 4).ToArray();
            }
        }

        return null;
    }

    private static async Task<byte[]?> ReadExactAsync(Stream stream, int length, CancellationToken ct)
    {
        var result = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(result.AsMemory(offset, length - offset), ct).ConfigureAwait(false);
            if (read == 0)
                return null;
            offset += read;
        }
        return result;
    }

    private static async Task WriteJsonResponseAsync(Stream stream, int statusCode, string reasonPhrase, object payload, CancellationToken ct)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {reasonPhrase}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {jsonBytes.Length}\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(jsonBytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
}
