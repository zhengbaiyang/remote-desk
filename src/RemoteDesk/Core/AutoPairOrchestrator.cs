using System.Security.Cryptography;

namespace RemoteDesk;

internal enum AutoPairStatus
{
    Succeeded,
    NeedSecurityPin,
    InvalidSecurityPin,
    Failed,
}

internal sealed record AutoPairResult(
    AutoPairStatus Status,
    string Message)
{
    public bool IsSuccess => Status == AutoPairStatus.Succeeded;
}

internal sealed class AutoPairOrchestrator
{
    private readonly AgentPeerClient _peerClient;

    public AutoPairOrchestrator(AgentPeerClient? peerClient = null)
    {
        _peerClient = peerClient ?? new AgentPeerClient();
    }

    public async Task<AutoPairResult> NativeAutoPairAsync(
        GameStreamClient gsClient,
        string targetHost,
        int agentPort,
        string agentAuthKey,
        string clientName,
        string? securityPin,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(gsClient);

        var (infoOk, info, infoErr) = await _peerClient
            .GetInfoAsync(targetHost, agentPort, agentAuthKey, ct)
            .ConfigureAwait(false);

        if (!infoOk || info is null)
        {
            return new AutoPairResult(
                AutoPairStatus.Failed,
                infoErr ?? "无法访问目标电脑的守护服务，请确认目标电脑已开启「后台守护服务」和「允许本机被控」。");
        }

        if (!info.SunshineReady)
        {
            return new AutoPairResult(
                AutoPairStatus.Failed,
                "目标电脑的高刷被控端（Sunshine）尚未就绪，请等待目标电脑后台自动配置完成。");
        }

        if (info.SecurityPinRequired && string.IsNullOrEmpty(securityPin))
        {
            return new AutoPairResult(
                AutoPairStatus.NeedSecurityPin,
                "目标电脑已开启独立安全密码验证，请输入安全密码后继续连接。");
        }

        try
        {
            var serverInfo = await gsClient.GetServerInfoAsync(targetHost, ct).ConfigureAwait(false);
            if (serverInfo.PairStatus == 1)
                return new AutoPairResult(AutoPairStatus.Succeeded, "目标主机已完成配对。");
        }
        catch
        {
            // Continue into pairing flow.
        }

        var pin = RandomNumberGenerator.GetInt32(0, 10000).ToString("D4");
        AutoPairResult? pinAuthFailure = null;

        var (paired, message) = await gsClient.PairWithPinAsync(
            targetHost,
            pin,
            onPinReadyAsync: async token =>
            {
                for (var attempt = 0; attempt < 6; attempt++)
                {
                    var resp = await _peerClient
                        .SubmitPairPinAsync(targetHost, agentPort, agentAuthKey, pin, clientName, securityPin, token)
                        .ConfigureAwait(false);

                    if (resp.Ok)
                        return;

                    if (string.Equals(resp.Error, "SECURITY_PIN_REQUIRED", StringComparison.Ordinal))
                    {
                        pinAuthFailure = new AutoPairResult(AutoPairStatus.NeedSecurityPin, resp.Message ?? "需要输入独立安全密码。");
                        throw new OperationCanceledException(pinAuthFailure.Message);
                    }

                    if (string.Equals(resp.Error, "INVALID_SECURITY_PIN", StringComparison.Ordinal))
                    {
                        pinAuthFailure = new AutoPairResult(AutoPairStatus.InvalidSecurityPin, resp.Message ?? "独立安全密码错误。");
                        throw new OperationCanceledException(pinAuthFailure.Message);
                    }

                    await Task.Delay(450, token).ConfigureAwait(false);
                }

                throw new InvalidOperationException("向目标节点自动提交 PIN 超时，请稍后重试。");
            },
            ct).ConfigureAwait(false);

        if (pinAuthFailure is not null)
            return pinAuthFailure;

        return paired
            ? new AutoPairResult(AutoPairStatus.Succeeded, message)
            : new AutoPairResult(AutoPairStatus.Failed, message);
    }
}
