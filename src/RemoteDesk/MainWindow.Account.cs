using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using RemoteDesk.Streaming;
using RemoteDesk.UI;
using Path = System.IO.Path;

namespace RemoteDesk;

public partial class MainWindow
{
    private readonly SyncApiClient syncApiClient = new();
    private readonly AutoPairOrchestrator autoPairOrchestrator = new();
    private readonly DispatcherTimer accountSyncTimer = new() { Interval = TimeSpan.FromSeconds(20) };

    private IReadOnlyList<SyncedDeviceInfo> syncedDevices = [];
    private bool isRegisterMode;
    private bool isAccountBusy;
    private bool isSilentBootstrapRunning;
    private bool wasAccountLoggedIn;
    private DeviceListItem? pendingSecurityPinItem;

    private void InitializeAccount()
    {
        EnsureLocalDeviceKeyPersisted();
        accountSyncTimer.Tick += async (_, _) => await SafeBackgroundSyncTickAsync();

        var savedAccount = settings.Account;
        if (!string.IsNullOrWhiteSpace(savedAccount?.ServerUrl))
        {
            SyncServerUrlInput.Text = savedAccount.ServerUrl;
        }
        else if (!string.IsNullOrWhiteSpace(settings.EasyTier?.PeerUrl) &&
                 Uri.TryCreate(settings.EasyTier.PeerUrl, UriKind.Absolute, out var peerUri) &&
                 !string.IsNullOrWhiteSpace(peerUri.Host))
        {
            SyncServerUrlInput.Text = $"https://{peerUri.Host}:11020";
        }

        if (!string.IsNullOrWhiteSpace(savedAccount?.Username))
            SyncUsernameInput.Text = savedAccount.Username;

        UpdateSyncAuthPlaceholders();
        UpdateAccountModeIndicator(animate: false);
        RefreshAccountUi();
        ReconcileLocalPeerHost();
    }

    private void ReconcileLocalPeerHost()
    {
        if (!OperatingSystem.IsWindows() || localHostPeerHost is null)
            return;

        if (!IsAccountLoggedIn() || !settings.HostModeEnabled || WindowsServiceInstaller.GetServiceStatus().Running)
        {
            localHostPeerHost.Stop();
            return;
        }

        string? authKey = null;
        if (!string.IsNullOrEmpty(settings.Account?.MeshConfig?.AgentAuthKey))
            SecretProtector.TryUnprotect(settings.Account.MeshConfig.AgentAuthKey, out authKey);

        var net = HostServiceManager.GetLocalNetworkInfo();
        localHostPeerHost.Reconcile(
            shouldRun: !string.IsNullOrEmpty(authKey),
            deviceKey: EnsureLocalDeviceKeyPersisted(),
            deviceName: net.HostName,
            authKey: authKey,
            sunshineExe: ResolveSunshineExecutable(),
            securityPinRequired: settings.SecurityPinRequired,
            securityPinSalt: settings.SecurityPinSalt,
            securityPinHash: settings.SecurityPinHash);
    }

    private async Task ResumeDaemonAfterUpdateIfNeededAsync()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var marker = WindowsServiceInstaller.GetRestartDaemonMarkerPath();
        if (!File.Exists(marker))
            return;

        try { File.Delete(marker); } catch { }

        var (installed, running) = WindowsServiceInstaller.GetServiceStatus();
        if (running || (!installed && !settings.HostModeEnabled && !IsAccountLoggedIn()))
            return;

        localHostPeerHost?.Stop();
        ShowStatus("软件已更新完成，正在自动恢复后台守护服务…");

        var ok = false;
        if (WindowsServiceInstaller.IsCurrentProcessElevated())
        {
            var exe = Environment.ProcessPath ?? "";
            ok = !string.IsNullOrEmpty(exe) && WindowsServiceInstaller.InstallOrUpdateAndStart(exe) == 0;
        }
        else
        {
            var res = await WindowsServiceInstaller.RunElevatedServiceCommandAsync("--install-service");
            ok = res.Ok;
        }

        if (ok)
        {
            await Task.Delay(500);
            await PushConfigToDaemonIfRunningAsync();
            ShowStatus("后台守护服务已随更新自动恢复运行。");
        }
        else
        {
            ReconcileLocalPeerHost();
            ShowStatus("未能自动恢复后台守护服务，如需系统级托管可在「本机被控」手动启用。", warning: true);
        }

        RefreshDaemonServiceUi();
        RefreshHostStatus();
    }

    private string EnsureLocalDeviceKeyPersisted()
    {
        if (!string.IsNullOrWhiteSpace(settings.LocalDeviceKey))
            return settings.LocalDeviceKey;

        var key = $"dev-{Guid.NewGuid().ToString("N")[..16]}";
        try
        {
            SaveSettings(settings with { LocalDeviceKey = key });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Keep generated key in memory if settings file cannot be written right now.
        }
        return key;
    }

    private async Task TryAutoSyncOnLaunchAsync()
    {
        if (!IsAccountLoggedIn())
        {
            await RefreshDaemonAndEasyTierAsync();
            return;
        }

        accountSyncTimer.Start();
        await SyncWithServerAndApplyAsync(userInitiated: false);
        _ = EnsureRequiredEnginesSilentlyAsync();
    }

    private bool IsAccountLoggedIn() =>
        settings.Account is { ServerUrl.Length: > 0, ProtectedSessionToken.Length: > 0, UserId.Length: > 0 };

    private void SelectAccountLoginModeClicked(object sender, RoutedEventArgs e)
    {
        isRegisterMode = false;
        AccountModeLoginBtn.Tag = "Selected";
        AccountModeRegisterBtn.Tag = null;
        SyncInviteCodePanel.Visibility = Visibility.Collapsed;
        SyncAuthSubmitButton.Content = "登录并自动组网";
        SyncAccountFeedback.Visibility = Visibility.Collapsed;
        UpdateAccountModeIndicator(animate: IsLoaded);
    }

    private void SelectAccountRegisterModeClicked(object sender, RoutedEventArgs e)
    {
        isRegisterMode = true;
        AccountModeLoginBtn.Tag = null;
        AccountModeRegisterBtn.Tag = "Selected";
        SyncInviteCodePanel.Visibility = Visibility.Visible;
        SyncAuthSubmitButton.Content = "注册并自动组网";
        SyncAccountFeedback.Visibility = Visibility.Collapsed;
        UpdateAccountModeIndicator(animate: IsLoaded);
    }

    private void AccountModeTrackGridSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateAccountModeIndicator(animate: false);

    private void UpdateAccountModeIndicator(bool animate)
    {
        if (AccountModeTrackGrid is null || AccountModeIndicatorTransform is null)
            return;

        var segmentWidth = AccountModeTrackGrid.ActualWidth / 2.0;
        if (segmentWidth <= 0)
            return;

        var targetX = isRegisterMode ? segmentWidth : 0.0;
        MotionBehaviors.AnimateHorizontalIndicator(AccountModeIndicatorTransform, targetX, animate);
    }

    private void SyncAuthFieldChanged(object sender, TextChangedEventArgs e) => UpdateSyncAuthPlaceholders();

    private void SyncPasswordBoxChanged(object sender, RoutedEventArgs e) => UpdateSyncAuthPlaceholders();

    private void UpdateSyncAuthPlaceholders()
    {
        if (SyncServerUrlPlaceholder is not null)
            SyncServerUrlPlaceholder.Visibility = string.IsNullOrEmpty(SyncServerUrlInput.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (SyncUsernamePlaceholder is not null)
            SyncUsernamePlaceholder.Visibility = string.IsNullOrEmpty(SyncUsernameInput.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (SyncPasswordPlaceholder is not null)
            SyncPasswordPlaceholder.Visibility = string.IsNullOrEmpty(SyncPasswordBox.Password) ? Visibility.Visible : Visibility.Collapsed;
        if (SyncInviteCodePlaceholder is not null)
            SyncInviteCodePlaceholder.Visibility = string.IsNullOrEmpty(SyncInviteCodeInput.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void SyncAuthSubmitClicked(object sender, RoutedEventArgs e)
    {
        if (isAccountBusy)
            return;

        var rawServerUrl = SyncServerUrlInput.Text.Trim();
        var username = SyncUsernameInput.Text.Trim();
        var password = SyncPasswordBox.Password;
        var inviteCode = SyncInviteCodeInput.Text.Trim();

        if (string.IsNullOrWhiteSpace(username))
        {
            ShowSyncFeedback("请输入账号用户名。", warning: true);
            SyncUsernameInput.Focus();
            return;
        }
        if (string.IsNullOrEmpty(password))
        {
            ShowSyncFeedback("请输入账号密码。", warning: true);
            SyncPasswordBox.Focus();
            return;
        }
        if (isRegisterMode && string.IsNullOrWhiteSpace(inviteCode))
        {
            ShowSyncFeedback("邀请码注册模式下必须填写服务器邀请码。", warning: true);
            SyncInviteCodeInput.Focus();
            return;
        }

        isAccountBusy = true;
        SyncAuthSubmitButton.IsEnabled = false;
        ResetTlsTrustButton.Visibility = Visibility.Collapsed;

        try
        {
            var normalizedUrl = SyncApiClient.NormalizeServerUrl(rawServerUrl);
            var existingThumb = string.Equals(settings.Account?.ServerUrl, normalizedUrl, StringComparison.OrdinalIgnoreCase)
                ? settings.Account?.ServerCertThumbprint
                : null;
            var allowTofu = string.IsNullOrEmpty(existingThumb);

            ShowSyncFeedback(isRegisterMode ? "正在注册账号并分配隔离虚拟网络…" : "正在登录并拉取虚拟组网配置…", warning: false);

            var auth = isRegisterMode
                ? await syncApiClient.RegisterAsync(normalizedUrl, username, password, inviteCode, existingThumb, allowTofu)
                : await syncApiClient.LoginAsync(normalizedUrl, username, password, existingThumb, allowTofu);

            var protectedMesh = auth.MeshConfig with
            {
                NetworkSecret = SecretProtector.ProtectCurrentUser(auth.MeshConfig.NetworkSecret),
                AgentAuthKey = SecretProtector.ProtectCurrentUser(auth.MeshConfig.AgentAuthKey),
            };

            var account = new AccountSettings
            {
                ServerUrl = normalizedUrl,
                ServerCertThumbprint = auth.ObservedCertThumbprint ?? existingThumb,
                UserId = auth.User.Id,
                Username = auth.User.Username,
                ProtectedSessionToken = SecretProtector.ProtectCurrentUser(auth.Token),
                MeshConfig = protectedMesh,
                AssignedVirtualIpv4 = null,
                AutoSyncEnabled = true,
            };

            var autoMesh = new EasyTierConfig
            {
                Enabled = true,
                PeerUrl = auth.MeshConfig.PeerUrl,
                FallbackPeerUrl = string.IsNullOrWhiteSpace(auth.MeshConfig.FallbackWsUrl) ? null : auth.MeshConfig.FallbackWsUrl.Trim(),
                NetworkName = auth.MeshConfig.NetworkName,
                NetworkSecret = auth.MeshConfig.NetworkSecret,
                AutoAssignIpv4 = true,
                VirtualIpv4 = null,
                EnableRelayFallback = true,
                AutoStartWithApp = true,
            };

            SaveSettings(settings with
            {
                Account = account,
                EasyTier = autoMesh,
                HostModeEnabled = true,
            });

            SyncPasswordBox.Password = "";
            SyncInviteCodeInput.Text = "";

            accountSyncTimer.Start();
            await SyncWithServerAndApplyAsync(userInitiated: true);
            RefreshAccountUi();
            SyncEasyTierFormFromSettings();
            RefreshHostStatus();
            ShowSyncFeedback($"已登录账号 {auth.User.Username}，已自动同步虚拟网络与设备列表。", warning: false);
            ShowStatus($"已登录 {auth.User.Username} 并自动完成跨网组网配置。");

            _ = EnsureRequiredEnginesSilentlyAsync();
        }
        catch (SyncCertificateMismatchException certErr)
        {
            ResetTlsTrustButton.Visibility = Visibility.Visible;
            ShowSyncFeedback(certErr.Message, warning: true);
        }
        catch (SyncApiException apiErr)
        {
            ShowSyncFeedback(apiErr.Message, warning: true);
        }
        catch (Exception error) when (error is ArgumentException or HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            ShowSyncFeedback($"连接同步服务器失败：{error.Message}", warning: true);
        }
        finally
        {
            isAccountBusy = false;
            SyncAuthSubmitButton.IsEnabled = true;
        }
    }

    private void ResetTlsTrustClicked(object sender, RoutedEventArgs e)
    {
        if (settings.Account is { } acc)
        {
            SaveSettings(settings with { Account = acc with { ServerCertThumbprint = null } });
        }
        ResetTlsTrustButton.Visibility = Visibility.Collapsed;
        ShowSyncFeedback("已重置旧服务器 TLS 指纹信任，请重新点击登录。", warning: false);
    }

    private async void SyncNowClicked(object sender, RoutedEventArgs e)
    {
        await SyncWithServerAndApplyAsync(userInitiated: true);
    }

    private async void LogoutAccountClicked(object sender, RoutedEventArgs e)
    {
        accountSyncTimer.Stop();
        var acc = settings.Account;
        if (acc is not null && SecretProtector.TryUnprotect(acc.ProtectedSessionToken, out var token) && !string.IsNullOrEmpty(token))
        {
            try
            {
                await syncApiClient.LogoutAsync(acc.ServerUrl, token, acc.ServerCertThumbprint);
            }
            catch
            {
                // Best-effort server logout.
            }
        }

        syncedDevices = [];
        SaveSettings(settings with
        {
            Account = acc is null
                ? null
                : new AccountSettings
                {
                    ServerUrl = acc.ServerUrl,
                    ServerCertThumbprint = acc.ServerCertThumbprint,
                    Username = acc.Username,
                },
        });

        await PushConfigToDaemonIfRunningAsync();
        ReconcileLocalPeerHost();
        RefreshAccountUi();
        RefreshDevices(0);
        ShowStatus("已退出当前同步账号。");
    }

    private async Task SafeBackgroundSyncTickAsync()
    {
        if (!IsAccountLoggedIn() || isAccountBusy)
            return;

        try
        {
            await SyncWithServerAndApplyAsync(userInitiated: false);
        }
        catch
        {
            // Prevent background sync timer ticks from terminating the UI process.
        }
    }

    private async Task SyncWithServerAndApplyAsync(bool userInitiated)
    {
        if (!IsAccountLoggedIn() || settings.Account is not { } acc)
            return;

        if (!SecretProtector.TryUnprotect(acc.ProtectedSessionToken, out var token) || string.IsNullOrEmpty(token))
            return;

        try
        {
            var profile = await syncApiClient.GetProfileAsync(acc.ServerUrl, token, acc.ServerCertThumbprint);
            var net = HostServiceManager.GetLocalNetworkInfo();
            var gpus = HostServiceManager.DetectGpuEncoders();
            var sunshineExe = ResolveSunshineExecutable();
            var hostStatus = HostServiceManager.GetHostStatus(sunshineExe);
            var deviceKey = EnsureLocalDeviceKeyPersisted();

            var checkinReq = new SyncCheckinRequest(
                DeviceKey: deviceKey,
                Name: net.HostName,
                Hostname: net.HostName,
                LanIpv4: net.IPv4Addresses.FirstOrDefault(),
                LanIpv6: net.IPv6Addresses.FirstOrDefault(),
                SunshineReady: hostStatus.SunshineInstalled && hostStatus.SunshineRunning,
                SunshinePort: SunshineManager.DefaultStreamPort,
                AgentPort: AgentIpcConstants.DefaultAgentPort,
                SecurityPinRequired: settings.SecurityPinRequired,
                GpuEncoder: gpus.FirstOrDefault()?.EncoderName,
                OsVersion: Environment.OSVersion.VersionString);

            var checkin = await syncApiClient.CheckinDeviceAsync(
                acc.ServerUrl,
                token,
                checkinReq,
                acc.ServerCertThumbprint);

            var protectedMesh = profile.MeshConfig with
            {
                NetworkSecret = SecretProtector.ProtectCurrentUser(profile.MeshConfig.NetworkSecret),
                AgentAuthKey = SecretProtector.ProtectCurrentUser(profile.MeshConfig.AgentAuthKey),
            };

            var assignedIp = checkin.Device.VirtualIpv4;
            var updatedAccount = acc with
            {
                Username = profile.User.Username,
                MeshConfig = protectedMesh,
                AssignedVirtualIpv4 = assignedIp,
            };

            var updatedEasyTier = new EasyTierConfig
            {
                Enabled = true,
                PeerUrl = profile.MeshConfig.PeerUrl,
                FallbackPeerUrl = string.IsNullOrWhiteSpace(profile.MeshConfig.FallbackWsUrl) ? null : profile.MeshConfig.FallbackWsUrl.Trim(),
                NetworkName = profile.MeshConfig.NetworkName,
                NetworkSecret = profile.MeshConfig.NetworkSecret,
                AutoAssignIpv4 = string.IsNullOrWhiteSpace(assignedIp),
                VirtualIpv4 = string.IsNullOrWhiteSpace(assignedIp) ? null : $"{assignedIp}/24",
                EnableRelayFallback = true,
                AutoStartWithApp = true,
            };

            syncedDevices = checkin.Devices;
            SaveSettings(settings with
            {
                Account = updatedAccount,
                EasyTier = updatedEasyTier,
            });

            await PushConfigToDaemonOrApplyLocallyAsync(autoStartMesh: true);
            ReconcileLocalPeerHost();
            RefreshAccountUi();
            SyncEasyTierFormFromSettings();

            var selectedKey = (DeviceList.SelectedItem as DeviceListItem)?.UniqueKey;
            RefreshDevices(DeviceList.SelectedIndex, selectedKey, preserveTransientUi: !userInitiated);
            await RefreshEasyTierStatusAsync();

            if (userInitiated)
            {
                ShowSyncFeedback($"同步成功：本机已分配固定虚拟 IP {assignedIp}，共发现 {syncedDevices.Count} 台同账号设备。", warning: false);
                ShowStatus($"账号设备已同步（虚拟 IP：{assignedIp}）。");
            }
        }
        catch (SyncApiException apiErr) when (apiErr.StatusCode == 401)
        {
            accountSyncTimer.Stop();
            ShowSyncFeedback("账号会话已过期，请重新登录。", warning: true);
            if (userInitiated)
                ShowStatus("账号登录状态已过期，请在「网络节点」重新登录。", warning: true);
        }
        catch (Exception ex) when (ex is SyncApiException or SyncCertificateMismatchException or HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
        {
            if (userInitiated)
                ShowSyncFeedback($"同步失败：{ex.Message}", warning: true);
        }
        catch (Exception ex)
        {
            if (userInitiated)
                ShowSyncFeedback($"同步异常：{ex.Message}", warning: true);
        }
    }

    private async Task EnsureRequiredEnginesSilentlyAsync()
    {
        if (isSilentBootstrapRunning)
            return;

        isSilentBootstrapRunning = true;
        try
        {
            var needEasyTier = ResolveEasyTierExecutable() is null;
            var needSunshine = settings.HostModeEnabled && ResolveSunshineExecutable() is null;

            if (!needEasyTier && !needSunshine)
                return;

            var installedAny = false;
            if (needEasyTier)
            {
                ShowStatus("正在后台自动配置 EasyTier 跨网组网组件…");
                try
                {
                    await engineManager.InstallFromGitHubAsync(EngineKind.EasyTier, settings.DownloadMirrorPrefix);
                    installedAny = true;
                }
                catch
                {
                    // Non-fatal: user can still trigger or import locally in Engine Hub.
                }
            }

            if (needSunshine)
            {
                ShowStatus("正在后台自动配置 Sunshine 高刷被控端组件…");
                try
                {
                    await engineManager.InstallFromGitHubAsync(EngineKind.Sunshine, settings.DownloadMirrorPrefix);
                    installedAny = true;
                }
                catch
                {
                }
            }

            if (installedAny)
            {
                RefreshAllEngines();
                await PushConfigToDaemonOrApplyLocallyAsync(autoStartMesh: true);
                if (settings.HostModeEnabled &&
                    OperatingSystem.IsWindows() &&
                    !WindowsServiceInstaller.GetServiceStatus().Running &&
                    localHostPeerHost is not null &&
                    ResolveSunshineExecutable() is { } ssExe)
                {
                    await localHostPeerHost.EnsureProvisionedAndStartedAsync(ssExe);
                }
                RefreshHostStatus();
                await RefreshEasyTierStatusAsync();
                ShowStatus("所需核心引擎已在后台自动就绪。");
            }
        }
        finally
        {
            isSilentBootstrapRunning = false;
        }
    }

    private AgentApplyConfigPayload BuildAgentConfigPayload()
    {
        var acc = settings.Account;
        string? authKey = null;
        if (!string.IsNullOrEmpty(acc?.MeshConfig?.AgentAuthKey))
            SecretProtector.TryUnprotect(acc.MeshConfig.AgentAuthKey, out authKey);

        string? sessionToken = null;
        if (!string.IsNullOrEmpty(acc?.ProtectedSessionToken))
            SecretProtector.TryUnprotect(acc.ProtectedSessionToken, out sessionToken);

        var net = HostServiceManager.GetLocalNetworkInfo();
        return new AgentApplyConfigPayload(
            EasyTierExecutablePath: ResolveEasyTierExecutable(),
            SunshineExecutablePath: ResolveSunshineExecutable(),
            EasyTier: settings.EasyTier,
            HostModeEnabled: settings.HostModeEnabled,
            DeviceKey: EnsureLocalDeviceKeyPersisted(),
            DeviceName: net.HostName,
            AccountId: IsAccountLoggedIn() ? acc?.UserId : null,
            AgentAuthKey: authKey,
            AgentPort: AgentIpcConstants.DefaultAgentPort,
            SecurityPinRequired: settings.SecurityPinRequired,
            SecurityPinSalt: settings.SecurityPinSalt,
            SecurityPinHash: settings.SecurityPinHash,
            SyncServerUrl: IsAccountLoggedIn() ? acc?.ServerUrl : null,
            SyncServerCertThumbprint: acc?.ServerCertThumbprint,
            SyncSessionToken: sessionToken);
    }

    private async Task<bool> PushConfigToDaemonIfRunningAsync()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            var (_, running) = WindowsServiceInstaller.GetServiceStatus();
            if (!running)
                return false;

            var (ok, _, _) = await AgentIpcClient.ApplyConfigAsync(BuildAgentConfigPayload());
            return ok;
        }
        catch
        {
            return false;
        }
    }

    private async Task PushConfigToDaemonOrApplyLocallyAsync(bool autoStartMesh)
    {
        if (await PushConfigToDaemonIfRunningAsync())
            return;

        if (!autoStartMesh || settings.EasyTier is not { Enabled: true } etCfg)
            return;

        var coreExe = ResolveEasyTierExecutable();
        if (coreExe is null)
            return;

        try
        {
            var validated = EasyTierConfig.Validate(etCfg);
            if (!EasyTierManager.IsMeshRunning())
            {
                if (EasyTierManager.IsCurrentProcessElevated())
                    EasyTierManager.StartMesh(coreExe, validated);
                else
                    EasyTierManager.StartMesh(coreExe, validated);
            }
        }
        catch
        {
        }
    }

    private void RefreshAccountUi()
    {
        var loggedIn = IsAccountLoggedIn();
        var acc = settings.Account;

        AccountLoggedOutPanel.Visibility = loggedIn ? Visibility.Collapsed : Visibility.Visible;
        AccountLoggedInPanel.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;

        if (loggedIn && acc is not null)
        {
            SetHostBadge(AccountStatusBadge, AccountStatusBadgeText, "已登录同步", "ReadySoft", "Ready");
            AccountProfileNameText.Text = acc.Username;
            AccountVirtualIpText.Text = string.IsNullOrWhiteSpace(acc.AssignedVirtualIpv4) ? "等待分配…" : acc.AssignedVirtualIpv4;
            AccountNetworkNameText.Text = acc.MeshConfig?.NetworkName ?? "--";
            AccountServerText.Text = acc.ServerUrl;

            RailAccountTitle.Text = acc.Username;
            RailAccountSubtitle.Text = string.IsNullOrWhiteSpace(acc.AssignedVirtualIpv4)
                ? "已开启云同步"
                : $"虚拟 IP {acc.AssignedVirtualIpv4}";
            RailAccountDot.SetResourceReference(Shape.FillProperty, "Ready");
            RailAccountDot.Opacity = 1.0;
            MotionBehaviors.SetEnablePulse(RailAccountDot, true);

            if (loggedIn && !wasAccountLoggedIn)
                ManualEasyTierExpander.IsExpanded = false;
        }
        else
        {
            SetHostBadge(AccountStatusBadge, AccountStatusBadgeText, "未登录", "SurfaceSubtle", "Muted");
            RailAccountTitle.Text = "登录同步账号";
            RailAccountSubtitle.Text = "免配置自动组网";
            MotionBehaviors.SetEnablePulse(RailAccountDot, false);
            RailAccountDot.SetResourceReference(Shape.FillProperty, "Muted");
            RailAccountDot.Opacity = 0.5;
        }

        wasAccountLoggedIn = loggedIn;
        RefreshDaemonServiceUi();
    }

    private void RefreshDaemonServiceUi()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var (installed, running) = WindowsServiceInstaller.GetServiceStatus();
        if (running)
        {
            SetHostBadge(HostDaemonBadge, HostDaemonBadgeText, "运行中 (System)", "ReadySoft", "Ready");
            ToggleDaemonServiceButton.Content = "卸载守护服务";
            HostDaemonDetailText.Text = "后台守护服务正在以系统权限运行，自动维护 EasyTier 虚拟网卡、Sunshine 被控端与账号云心跳。";
            AccountEnableDaemonButton.Visibility = Visibility.Collapsed;
        }
        else if (installed)
        {
            SetHostBadge(HostDaemonBadge, HostDaemonBadgeText, "已安装 · 未运行", "WarningSoft", "Warning");
            ToggleDaemonServiceButton.Content = "启动/修复守护服务";
            HostDaemonDetailText.Text = "守护服务已注册但当前未运行，点击按钮可恢复后台服务。";
            AccountEnableDaemonButton.Visibility = Visibility.Visible;
        }
        else
        {
            SetHostBadge(HostDaemonBadge, HostDaemonBadgeText, "未安装", "SurfaceSubtle", "Muted");
            ToggleDaemonServiceButton.Content = "启用后台守护服务";
            HostDaemonDetailText.Text = "以系统权限后台常驻运行 EasyTier 与 Sunshine，彻底解决虚拟 IP 分配失败、免 UAC 弹窗且支持开机锁屏直连。";
            AccountEnableDaemonButton.Visibility = Visibility.Visible;
        }
    }

    private async void ToggleDaemonServiceClicked(object sender, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var (installed, running) = WindowsServiceInstaller.GetServiceStatus();
        var isUninstall = installed && running && ReferenceEquals(sender, ToggleDaemonServiceButton);
        var flag = isUninstall ? "--uninstall-service" : "--install-service";

        ToggleDaemonServiceButton.IsEnabled = false;
        FixEasyTierElevationButton.IsEnabled = false;
        AccountEnableDaemonButton.IsEnabled = false;

        try
        {
            if (!isUninstall)
                localHostPeerHost?.Stop();

            ShowStatus(isUninstall ? "正在请求管理员权限以卸载后台守护服务…" : "正在请求管理员权限以安装并启动后台守护服务…");
            var (ok, cancelled, message) = await WindowsServiceInstaller.RunElevatedServiceCommandAsync(flag);
            if (!ok)
            {
                ReconcileLocalPeerHost();
                ShowStatus(message, warning: !cancelled);
                return;
            }

            if (!isUninstall)
            {
                await Task.Delay(500);
                await PushConfigToDaemonIfRunningAsync();
            }

            ReconcileLocalPeerHost();
            RefreshDaemonServiceUi();
            RefreshHostStatus();
            await RefreshEasyTierStatusAsync();
            ShowStatus(isUninstall ? "已停止并卸载后台守护服务。" : "后台守护服务已启动！EasyTier 将自动以系统权限分配虚拟 IP。");
        }
        finally
        {
            ReconcileLocalPeerHost();
            ToggleDaemonServiceButton.IsEnabled = true;
            FixEasyTierElevationButton.IsEnabled = true;
            AccountEnableDaemonButton.IsEnabled = true;
        }
    }

    private readonly Dictionary<string, RemoteSessionWindow> activeStreamWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> activeStreamDeviceKeys = new(StringComparer.OrdinalIgnoreCase);

    private bool IsHostControlledByLocal(string? hostAddress, string? deviceKey) =>
        (!string.IsNullOrWhiteSpace(hostAddress) && activeStreamWindows.ContainsKey(hostAddress)) ||
        (!string.IsNullOrWhiteSpace(deviceKey) && activeStreamDeviceKeys.Contains(deviceKey));

    private void UpdateLocalControllingState(bool isControlling)
    {
        if (!OperatingSystem.IsWindows())
            return;

        localHostPeerHost?.SetControllingState(isControlling);
        _ = AgentIpcClient.TrySetControllingStateAsync(isControlling);
    }

    private RemoteSessionLockContext BuildSessionLockContext(SyncedDeviceInfo? synced)
    {
        var localKey = EnsureLocalDeviceKeyPersisted();
        var localName = HostServiceManager.GetLocalNetworkInfo().HostName;
        var acc = settings.Account;
        string? token = null;
        if (!string.IsNullOrEmpty(acc?.ProtectedSessionToken))
            SecretProtector.TryUnprotect(acc.ProtectedSessionToken, out token);

        return new RemoteSessionLockContext(
            LocalDeviceKey: localKey,
            LocalDeviceName: localName,
            TargetDeviceKey: synced?.DeviceKey,
            SyncServerUrl: IsAccountLoggedIn() ? acc?.ServerUrl : null,
            SyncSessionToken: token,
            SyncServerCertThumbprint: acc?.ServerCertThumbprint,
            OnSyncedDevicesUpdated: updated => Dispatcher.BeginInvoke(() =>
            {
                syncedDevices = updated;
                var selectedKey = (DeviceList.SelectedItem as DeviceListItem)?.UniqueKey;
                RefreshDevices(DeviceList.SelectedIndex, selectedKey, preserveTransientUi: true);
            }));
    }

    private string ResolveLinkTypeHint(string targetHost, SyncedDeviceInfo? synced)
    {
        var peer = easyTierPeers.FirstOrDefault(p =>
            string.Equals(p.Peer.VirtualIpv4, targetHost, StringComparison.OrdinalIgnoreCase));
        if (peer is not null)
        {
            if (peer.ConnectionType == EasyTierManager.ConnectionTypeP2P)
                return "P2P 直连";
            if (peer.ConnectionType == EasyTierManager.ConnectionTypeRelay)
                return "中继转发";
        }

        if (synced is not null && string.Equals(synced.VirtualIpv4, targetHost, StringComparison.OrdinalIgnoreCase))
            return "虚拟内网";

        return "局域网/直连";
    }

    private AgentPeerSessionContext? BuildPeerSessionContext(SyncedDeviceInfo? synced)
    {
        if (synced is null || !IsAccountLoggedIn())
            return null;

        if (string.IsNullOrEmpty(settings.Account?.MeshConfig?.AgentAuthKey) ||
            !SecretProtector.TryUnprotect(settings.Account.MeshConfig.AgentAuthKey, out var authKey) ||
            string.IsNullOrEmpty(authKey))
        {
            return null;
        }

        var port = synced.AgentPort > 0 ? synced.AgentPort : AgentIpcConstants.DefaultAgentPort;
        return new AgentPeerSessionContext(port, authKey);
    }

    private void OpenNativeSessionWindow(DeviceProfile device, string targetHost, DeviceListItem? listItem)
    {
        if (activeStreamWindows.TryGetValue(targetHost, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var synced = listItem?.SyncedDevice;
        var targetDeviceKey = synced?.DeviceKey;
        var linkHint = ResolveLinkTypeHint(targetHost, synced);
        var peerContext = BuildPeerSessionContext(synced);
        var lockContext = BuildSessionLockContext(synced);
        var sessionWindow = new RemoteSessionWindow(
            device,
            targetHost,
            linkHint,
            nextOptions => Dispatcher.InvokeAsync(() => PersistStreamOptionsFromSession(device, listItem, nextOptions)).Task,
            peerContext,
            settings.AutoMatchResolution,
            settings.AutoSyncClipboard,
            (autoRes, autoClip) => Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    SaveSettings(settings with { AutoMatchResolution = autoRes, AutoSyncClipboard = autoClip });
                }
                catch
                {
                }
            }),
            lockContext);

        activeStreamWindows[targetHost] = sessionWindow;
        if (!string.IsNullOrWhiteSpace(targetDeviceKey))
            activeStreamDeviceKeys.Add(targetDeviceKey);
        UpdateLocalControllingState(isControlling: true);
        RefreshDevices(DeviceList.SelectedIndex, SelectedListItem?.UniqueKey, preserveTransientUi: true);

        sessionWindow.Closed += (_, _) =>
        {
            activeStreamWindows.Remove(targetHost);
            if (!string.IsNullOrWhiteSpace(targetDeviceKey))
                activeStreamDeviceKeys.Remove(targetDeviceKey);
            UpdateLocalControllingState(isControlling: activeStreamWindows.Count > 0);
            RefreshDevices(DeviceList.SelectedIndex, SelectedListItem?.UniqueKey, preserveTransientUi: true);
            if (IsAccountLoggedIn())
                _ = SafeBackgroundSyncTickAsync();
        };
        sessionWindow.Show();
        sessionWindow.Activate();
        ShowStatus($"已打开内置高刷控制窗口 -> {device.Name} ({targetHost})（窗口化 · 按 F11 全屏）。");
    }

    private void PersistStreamOptionsFromSession(DeviceProfile originalDevice, DeviceListItem? listItem, StreamOptions nextOptions)
    {
        try
        {
            if (listItem is { IsCloudSynced: true } || SelectedListItem?.IsCloudSynced == true)
            {
                SaveSettings(settings with { DefaultStream = nextStreamWithFallback(nextOptions) });
            }
            else
            {
                var devices = settings.Devices.ToList();
                var idx = devices.FindIndex(d =>
                    string.Equals(d.Name, originalDevice.Name, StringComparison.Ordinal) &&
                    string.Equals(d.HostAddress, originalDevice.HostAddress, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    devices[idx] = DeviceProfile.Create(
                        devices[idx].Name,
                        devices[idx].HostAddress,
                        nextOptions,
                        devices[idx].StreamAppName);
                }
                SaveSettings(settings with { Devices = devices, DefaultStream = nextOptions });
            }

            RefreshDevices(DeviceList.SelectedIndex, SelectedListItem?.UniqueKey, preserveTransientUi: true);
        }
        catch
        {
            // Ignore settings write contention during active streaming.
        }

        static StreamOptions nextStreamWithFallback(StreamOptions opts) => opts;
    }

    private async Task ConnectSyncedOrAutoPairDeviceAsync(DeviceListItem item, string? securityPin, bool forcePair)
    {
        var lockStatus = item.IsBeingControlled
            ? DeviceRemoteControlStatus.BeingControlled
            : (item.IsControllingOther ? DeviceRemoteControlStatus.Controlling : DeviceRemoteControlStatus.Idle);
        if (!RemoteControlLockPolicy.CanInitiateConnection(lockStatus, item.IsControlledByLocal, out var blockReason))
        {
            ShowStatus(blockReason!, warning: true);
            return;
        }

        if (!NativeStreamBridge.IsAvailable())
        {
            ShowStatus("未检测到内置原生控屏引擎 RemoteDesk.StreamCore.dll，请检查客户端安装。", warning: true);
            return;
        }

        var synced = item.SyncedDevice;
        var targetHost = synced is not null
            ? (!string.IsNullOrWhiteSpace(synced.VirtualIpv4) ? synced.VirtualIpv4 : (synced.LanIpv4 ?? item.HostAddress ?? ""))
            : (item.HostAddress ?? "");

        if (string.IsNullOrWhiteSpace(targetHost))
        {
            ShowStatus("该设备尚未分配有效地址，请等待目标设备在线同步。", warning: true);
            return;
        }

        if (synced is not null)
        {
            var primaryHost = targetHost;
            var pairedIds = settings.PairedDeviceIds ?? [];
            var alreadyPaired = pairedIds.Contains(synced.Id, StringComparer.OrdinalIgnoreCase);
            var needsPair = !alreadyPaired || forcePair;
            var agentPort = synced.AgentPort > 0 ? synced.AgentPort : AgentIpcConstants.DefaultAgentPort;

            string? plainAuthKey = null;
            if (!string.IsNullOrEmpty(settings.Account?.MeshConfig?.AgentAuthKey))
                SecretProtector.TryUnprotect(settings.Account.MeshConfig.AgentAuthKey, out plainAuthKey);

            ConnectButton.IsEnabled = false;
            PairButton.IsEnabled = false;
            try
            {
                TargetHostResolution? resolution = null;
                if (!string.IsNullOrEmpty(plainAuthKey))
                {
                    ShowStatus($"正在探测「{synced.Name}」最佳连接链路…");
                    using var probePeerClient = new AgentPeerClient();
                    resolution = await TargetHostResolver.ResolveAsync(
                        synced,
                        easyTierPeers.Select(p => p.Peer).ToList(),
                        agentPort,
                        plainAuthKey,
                        probePeerClient);

                    if (resolution.Succeeded && !string.IsNullOrWhiteSpace(resolution.ResolvedHost))
                    {
                        targetHost = resolution.ResolvedHost;
                        if (resolution.UsedLanFallback)
                            ShowStatus($"虚拟内网暂不可达，已自动切换同网段直连 ({targetHost})…");
                    }
                    else if (needsPair)
                    {
                        ShowStatus(resolution.FailureMessage ?? "无法连接目标节点控制服务。", warning: true);
                        return;
                    }
                    else
                    {
                        targetHost = primaryHost;
                    }
                }

                if (!needsPair)
                {
                    try
                    {
                        using var probeIdentity = GameStreamIdentity.LoadOrCreate();
                        var probeClient = new GameStreamClient(probeIdentity);
                        var probeInfo = await probeClient.GetServerInfoAsync(targetHost);
                        if (probeInfo.PairStatus != 1)
                            needsPair = true;
                    }
                    catch
                    {
                        // Let RemoteSessionWindow surface detailed connectivity or pairing diagnostics.
                    }
                }

                if (needsPair)
                {
                    if (string.IsNullOrEmpty(plainAuthKey))
                    {
                        ShowStatus("缺少同账号安全认证密钥，请尝试在「网络节点」重新同步账号。", warning: true);
                        return;
                    }

                    if (resolution is { Succeeded: false })
                    {
                        ShowStatus(resolution.FailureMessage ?? "无法连接目标节点控制服务。", warning: true);
                        return;
                    }

                    ShowStatus($"正在为「{synced.Name}」执行静默安全配对…");
                    var localHostName = HostServiceManager.GetLocalNetworkInfo().HostName;

                    using var identity = GameStreamIdentity.LoadOrCreate();
                    var gsClient = new GameStreamClient(identity);
                    var result = await autoPairOrchestrator.NativeAutoPairAsync(
                        gsClient,
                        targetHost,
                        agentPort,
                        plainAuthKey,
                        localHostName,
                        securityPin);

                    if (result.Status == AutoPairStatus.NeedSecurityPin || result.Status == AutoPairStatus.InvalidSecurityPin)
                    {
                        pendingSecurityPinItem = item;
                        SecurityPinPromptHint.Text = result.Message;
                        SecurityPinPromptCard.Visibility = Visibility.Visible;
                        ConnectSecurityPinBox.Password = "";
                        ConnectSecurityPinBox.Focus();
                        ShowStatus(result.Message, warning: true);
                        return;
                    }

                    if (!result.IsSuccess)
                    {
                        ShowStatus(result.Message, warning: true);
                        return;
                    }

                    SecurityPinPromptCard.Visibility = Visibility.Collapsed;
                    pendingSecurityPinItem = null;
                    if (!alreadyPaired)
                    {
                        var updatedPaired = pairedIds.Append(synced.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        SaveSettings(settings with { PairedDeviceIds = updatedPaired });
                    }

                    if (forcePair)
                    {
                        ShowStatus($"已完成「{synced.Name}」静默配对，可直接点击「发起高刷连接」。");
                        return;
                    }
                }
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException or InvalidDataException or IOException)
            {
                ShowStatus($"自动配对失败：{error.Message}", warning: true);
                return;
            }
            finally
            {
                ConnectButton.IsEnabled = true;
                PairButton.IsEnabled = true;
            }
        }

        OpenNativeSessionWindow(item.ToProfile(targetHost), targetHost, item);
    }

    private void CancelSecurityPinPromptClicked(object sender, RoutedEventArgs e)
    {
        pendingSecurityPinItem = null;
        ConnectSecurityPinBox.Password = "";
        SecurityPinPromptCard.Visibility = Visibility.Collapsed;
    }

    private async void ConfirmSecurityPinAndConnectClicked(object sender, RoutedEventArgs e)
    {
        var item = pendingSecurityPinItem ?? SelectedListItem;
        if (item is null)
            return;

        var pin = ConnectSecurityPinBox.Password;
        if (string.IsNullOrWhiteSpace(pin))
        {
            ShowStatus("请输入目标电脑设置的独立安全密码。", warning: true);
            ConnectSecurityPinBox.Focus();
            return;
        }

        await ConnectSyncedOrAutoPairDeviceAsync(item, pin, forcePair: false);
    }

    private void ShowSyncFeedback(string message, bool warning)
    {
        SyncAccountFeedback.Text = message;
        SyncAccountFeedback.SetResourceReference(TextBlock.ForegroundProperty, warning ? "Warning" : "Ready");
        SyncAccountFeedback.Visibility = Visibility.Visible;
    }
}
