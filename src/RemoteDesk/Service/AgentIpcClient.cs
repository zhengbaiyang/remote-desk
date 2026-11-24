using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace RemoteDesk;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class AgentIpcClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(1200);

    public static async Task<AgentRuntimeStatus?> TryGetStatusAsync(CancellationToken ct = default)
    {
        var resp = await SendAsync(new AgentIpcRequest("get_status"), ct).ConfigureAwait(false);
        if (!resp.Ok || string.IsNullOrWhiteSpace(resp.PayloadJson))
            return null;

        return JsonSerializer.Deserialize<AgentRuntimeStatus>(resp.PayloadJson, JsonOptions);
    }

    public static Task<(bool Ok, AgentRuntimeStatus? Status, string? Error)> ApplyConfigAsync(
        AgentApplyConfigPayload payload,
        CancellationToken ct = default) =>
        SendAndParseStatusAsync("apply_config", JsonSerializer.Serialize(payload, JsonOptions), ct);

    public static Task<(bool Ok, AgentRuntimeStatus? Status, string? Error)> StartEasyTierAsync(CancellationToken ct = default) =>
        SendAndParseStatusAsync("start_easytier", null, ct);

    public static Task<(bool Ok, AgentRuntimeStatus? Status, string? Error)> StopEasyTierAsync(CancellationToken ct = default) =>
        SendAndParseStatusAsync("stop_easytier", null, ct);

    public static Task<(bool Ok, AgentRuntimeStatus? Status, string? Error)> StartSunshineAsync(CancellationToken ct = default) =>
        SendAndParseStatusAsync("start_sunshine", null, ct);

    public static Task<(bool Ok, AgentRuntimeStatus? Status, string? Error)> StopSunshineAsync(CancellationToken ct = default) =>
        SendAndParseStatusAsync("stop_sunshine", null, ct);

    public static Task<(bool Ok, AgentRuntimeStatus? Status, string? Error)> SetSecurityPinAsync(
        bool required,
        string? newPin,
        CancellationToken ct = default) =>
        SendAndParseStatusAsync("set_security_pin", JsonSerializer.Serialize(new AgentSetSecurityPinPayload(required, newPin), JsonOptions), ct);

    private static async Task<(bool Ok, AgentRuntimeStatus? Status, string? Error)> SendAndParseStatusAsync(
        string command,
        string? payloadJson,
        CancellationToken ct)
    {
        var resp = await SendAsync(new AgentIpcRequest(command, payloadJson), ct).ConfigureAwait(false);
        if (!resp.Ok)
            return (false, null, resp.Error ?? "守护服务操作失败。");

        var status = string.IsNullOrWhiteSpace(resp.PayloadJson)
            ? null
            : JsonSerializer.Deserialize<AgentRuntimeStatus>(resp.PayloadJson, JsonOptions);
        return (true, status, null);
    }

    private static async Task<AgentIpcResponse> SendAsync(AgentIpcRequest request, CancellationToken ct)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", AgentIpcConstants.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);

            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

            await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions)).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
                return new AgentIpcResponse(false, null, "守护服务未返回有效响应。");

            return JsonSerializer.Deserialize<AgentIpcResponse>(line, JsonOptions)
                ?? new AgentIpcResponse(false, null, "无法解析守护服务响应。");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AgentIpcResponse(false, null, "后台守护服务未运行或连接超时。");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return new AgentIpcResponse(false, null, $"无法连接后台守护服务：{ex.Message}");
        }
    }
}
