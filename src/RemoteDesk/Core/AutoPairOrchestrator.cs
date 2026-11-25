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
            await Task.Delay(2000, ct).ConfigureAwait(false);
            var (retryOk, retryInfo, _) = await _peerClient
                .GetInfoAsync(targetHost, agentPort, agentAuthKey, ct)
                .ConfigureAwait(false);
            if (retryOk && retryInfo is not null)
                info = retryInfo;

            if (!info.SunshineReady)
            {
                return new AutoPairResult(
                    AutoPairStatus.Failed,
                    "目标电脑的高刷被控端（Sunshine）尚未启动或安装，请在目标电脑确认「允许本机被控」并等待自动配置完成。");
            }
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

        for (var round = 0; round < 2; round++)
        {
            var pin = RandomNumberGenerator.GetInt32(0, 10000).ToString("D4");
            AutoPairResult? pinAuthFailure = null;
            var shouldRetryHandshake = false;
            string? lastErrorMessage = null;

            bool paired;
            string message;
            try
            {
                (paired, message) = await gsClient.PairWithPinAsync(
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

                            lastErrorMessage = resp.Message;

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

                            if (string.Equals(resp.Error, AgentIpcConstants.ErrorSunshineReprovisionedRetry, StringComparison.Ordinal))
                            {
                                shouldRetryHandshake = true;
                                throw new OperationCanceledException(resp.Message ?? "Sunshine 已自动修复并重启，正在重新发起配对。");
                            }

                            if (string.Equals(resp.Error, "HOST_MODE_DISABLED", StringComparison.Ordinal) ||
                                (!string.IsNullOrEmpty(resp.Message) && resp.Message.Contains("未开启「允许本机被控」", StringComparison.Ordinal)))
                            {
                                throw new InvalidOperationException(resp.Message ?? "目标电脑未开启「允许本机被控」。");
                            }

                            if (!string.IsNullOrEmpty(resp.Message) &&
                                resp.Message.Contains("HTTP 400", StringComparison.Ordinal) &&
                                !resp.Message.Contains("CSRF", StringComparison.OrdinalIgnoreCase) &&
                                string.IsNullOrEmpty(info.AgentVersion))
                            {
                                throw new InvalidOperationException("目标电脑的 RemoteDesk 版本过旧（暂不支持 Sunshine 2026 配对接口），请先将目标电脑的 RemoteDesk 更新到最新版本后重试。");
                            }

                            await Task.Delay(450, token).ConfigureAwait(false);
                        }

                        throw new InvalidOperationException(lastErrorMessage ?? "向目标节点自动提交 PIN 超时，请稍后重试。");
                    },
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (pinAuthFailure is not null && !ct.IsCancellationRequested)
            {
                return pinAuthFailure;
            }
            catch (OperationCanceledException) when (shouldRetryHandshake && round == 0 && !ct.IsCancellationRequested)
            {
                await Task.Delay(300, ct).ConfigureAwait(false);
                continue;
            }
            catch (OperationCanceledException) when (shouldRetryHandshake && !ct.IsCancellationRequested)
            {
                return new AutoPairResult(AutoPairStatus.Failed, lastErrorMessage ?? "Sunshine 重启后自动配对重试失败，请再试一次。");
            }

            if (pinAuthFailure is not null)
                return pinAuthFailure;

            return paired
                ? new AutoPairResult(AutoPairStatus.Succeeded, message)
                : new AutoPairResult(AutoPairStatus.Failed, message);
        }

        return new AutoPairResult(AutoPairStatus.Failed, "自动配对未能完成，请稍后重试。");
    }
}
