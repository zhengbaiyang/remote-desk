using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using Microsoft.Win32;
using RemoteDesk.Streaming;
using Path = System.IO.Path;

namespace RemoteDesk;

internal sealed class EngineCardViewModel : INotifyPropertyChanged
{
    private EngineInstallState _state;
    private string? _executablePath;
    private string? _installedVersion;
    private string _detailPathText = "";
    private bool _isInstalling;
    private double _progressValue;
    private bool _isProgressIndeterminate;
    private string _progressStageText = "";
    private string _progressPercentText = "";

    public EngineCardViewModel(EngineDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public EngineDescriptor Descriptor { get; }
    public EngineKind Kind => Descriptor.Kind;
    public string DisplayName => Descriptor.DisplayName;
    public string RoleTag => Descriptor.RoleTag;
    public string Description => Descriptor.Description;
    public string License => Descriptor.License;
    public bool IsPhase2Placeholder => Descriptor.IsPhase2Placeholder;
    public bool IsPhase1Engine => !Descriptor.IsPhase2Placeholder;

    public EngineInstallState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StateKey));
            OnPropertyChanged(nameof(StateLabel));
            OnPropertyChanged(nameof(CanUninstall));
        }
    }

    public string StateKey => State.ToString();

    public string StateLabel => State switch
    {
        EngineInstallState.ManagedInstalled => "托管已就绪",
        EngineInstallState.SystemInstalled => "系统/手动就绪",
        EngineInstallState.Installing => "安装中…",
        EngineInstallState.PlannedPhase2 => "阶段 2 即将上线",
        _ => "尚未安装",
    };

    public string? ExecutablePath
    {
        get => _executablePath;
        private set
        {
            if (_executablePath == value) return;
            _executablePath = value;
            OnPropertyChanged();
        }
    }

    public string VersionLabel => string.IsNullOrWhiteSpace(_installedVersion) ? "" : $"版本 {_installedVersion}";
    public bool HasVersion => !string.IsNullOrWhiteSpace(_installedVersion);

    public string DetailPathText
    {
        get => _detailPathText;
        private set
        {
            if (_detailPathText == value) return;
            _detailPathText = value;
            OnPropertyChanged();
        }
    }

    public bool IsInstalling
    {
        get => _isInstalling;
        private set
        {
            if (_isInstalling == value) return;
            _isInstalling = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanStartInstall));
            OnPropertyChanged(nameof(CanUninstall));
        }
    }

    public bool CanStartInstall => !IsPhase2Placeholder && !IsInstalling;
    public bool CanUninstall => !IsPhase2Placeholder && !IsInstalling && State == EngineInstallState.ManagedInstalled;

    public double ProgressValue
    {
        get => _progressValue;
        private set
        {
            if (Math.Abs(_progressValue - value) < 0.01) return;
            _progressValue = value;
            OnPropertyChanged();
        }
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set
        {
            if (_isProgressIndeterminate == value) return;
            _isProgressIndeterminate = value;
            OnPropertyChanged();
        }
    }

    public string ProgressStageText
    {
        get => _progressStageText;
        private set
        {
            if (_progressStageText == value) return;
            _progressStageText = value;
            OnPropertyChanged();
        }
    }

    public string ProgressPercentText
    {
        get => _progressPercentText;
        private set
        {
            if (_progressPercentText == value) return;
            _progressPercentText = value;
            OnPropertyChanged();
        }
    }

    public void ApplyStatus(EngineStatus status, string? overridePath = null)
    {
        if (IsInstalling)
            return;

        State = status.State;
        ExecutablePath = overridePath ?? status.ExecutablePath;
        _installedVersion = status.InstalledVersion;
        OnPropertyChanged(nameof(VersionLabel));
        OnPropertyChanged(nameof(HasVersion));

        DetailPathText = status.State switch
        {
            EngineInstallState.PlannedPhase2 => "阶段 2 计划集成：将支持一键部署 EasyTier P2P 虚拟组网与自建中继节点。",
            EngineInstallState.NotInstalled => "尚未检测到可执行文件，点击「一键安装」或「导入本地包」完成部署。",
            _ => overridePath is not null && !string.Equals(overridePath, status.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                ? $"当前指定路径：{overridePath}"
                : (status.ExecutablePath ?? status.DetailMessage ?? ""),
        };
    }

    public void BeginInstall(string initialStage)
    {
        IsInstalling = true;
        State = EngineInstallState.Installing;
        ProgressStageText = initialStage;
        ProgressValue = 0;
        IsProgressIndeterminate = true;
        ProgressPercentText = "";
    }

    public void ReportProgress(EngineInstallProgress progress)
    {
        ProgressStageText = progress.Stage;
        if (progress.Percentage is { } pct)
        {
            IsProgressIndeterminate = false;
            ProgressValue = Math.Clamp(pct, 0, 100);
            ProgressPercentText = $"{pct:0}%";
        }
        else
        {
            IsProgressIndeterminate = true;
            ProgressPercentText = "";
        }
    }

    public void EndInstall()
    {
        IsInstalling = false;
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        ProgressStageText = "";
        ProgressPercentText = "";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public partial class MainWindow
{
    private readonly ObservableCollection<EngineCardViewModel> engineCards = [];
    private readonly Dictionary<EngineKind, EngineCardViewModel> engineCardMap = [];

    private void InitializeEngineHub()
    {
        engineCards.Clear();
        engineCardMap.Clear();

        foreach (var descriptor in EngineCatalog.All)
        {
            var card = new EngineCardViewModel(descriptor);
            engineCards.Add(card);
            engineCardMap[descriptor.Kind] = card;
        }

        EngineCardsList.ItemsSource = engineCards;
    }

    private string? ResolveSunshineExecutable() =>
        HostServiceManager.FindSunshineExecutable(settings.SunshinePath, engineManager.FindManagedExecutable(EngineKind.Sunshine));

    private string? ResolveEasyTierExecutable() =>
        EasyTierManager.FindCoreExecutable(settings.EasyTierPath, engineManager.FindManagedExecutable(EngineKind.EasyTier));

    private void RefreshAllEngines()
    {
        var sunshineFallback = HostServiceManager.FindSunshineExecutable(settings.SunshinePath, null);
        var easyTierFallback = EasyTierManager.FindCoreExecutable(settings.EasyTierPath, null);

        var ssStatus = engineManager.Inspect(EngineKind.Sunshine, sunshineFallback);
        var etStatus = engineManager.Inspect(EngineKind.EasyTier, easyTierFallback);

        engineCardMap[EngineKind.Sunshine].ApplyStatus(ssStatus, ResolveSunshineExecutable());
        engineCardMap[EngineKind.EasyTier].ApplyStatus(etStatus, ResolveEasyTierExecutable());

        bool coreReady = NativeStreamBridge.IsAvailable();
        bool ssReady = ResolveSunshineExecutable() is not null;
        bool etReady = ResolveEasyTierExecutable() is not null;
        int readyCount = (coreReady ? 1 : 0) + (ssReady ? 1 : 0) + (etReady ? 1 : 0);

        RailEngineCount.Text = $"{readyCount}/3";
        TitleStatusText.Text = $"核心就绪 {readyCount}/3";
        TitleStatusDot.SetResourceReference(Shape.FillProperty, readyCount == 3 ? "Ready" : (readyCount > 0 ? "Accent" : "Warning"));

        RailNativeCoreDot.SetResourceReference(Shape.FillProperty, coreReady ? "Ready" : "Danger");
        RailSunshineDot.SetResourceReference(Shape.FillProperty, ssReady ? "Ready" : "Warning");
        RailEasyTierDot.SetResourceReference(Shape.FillProperty, etReady ? "Ready" : "Warning");

        UpdateDeviceActionEngineReadiness();
    }

    private void RescanAllEnginesClicked(object sender, RoutedEventArgs e)
    {
        RefreshAllEngines();
        RefreshHostStatus();
        _ = RefreshEasyTierStatusAsync();
        ShowStatus("已完成全部引擎状态重新检测。");
    }

    private async void InstallEngineFromGitHubClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EngineKind kind } || !engineCardMap.TryGetValue(kind, out var card))
            return;
        if (!card.CanStartInstall)
            return;

        card.BeginInstall("正在连接 GitHub 发布源…");
        ShowStatus($"正在一键安装 {card.DisplayName}，请稍候…");

        var progress = new Progress<EngineInstallProgress>(p => card.ReportProgress(p));
        try
        {
            var result = await engineManager.InstallFromGitHubAsync(kind, settings.DownloadMirrorPrefix, progress);
            card.EndInstall();
            ClearManualOverrideIfEmptyOrMissing(kind);
            RefreshAllEngines();
            RefreshHostStatus();
            if (kind == EngineKind.EasyTier)
                await RefreshEasyTierStatusAsync();
            ShowStatus($"{card.DisplayName} 安装完成（{result.InstalledVersion ?? "最新版本"}）。");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or NotSupportedException or TaskCanceledException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            card.EndInstall();
            RefreshAllEngines();
            ShowStatus($"安装 {card.DisplayName} 失败：{error.Message}（可尝试配置国内镜像加速或使用「导入本地包」）。", warning: true);
        }
    }

    private async void ImportEngineLocalClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EngineKind kind } || !engineCardMap.TryGetValue(kind, out var card))
            return;
        if (!card.CanStartInstall)
            return;

        var descriptor = EngineCatalog.Get(kind);
        var dialog = new OpenFileDialog
        {
            Title = $"导入 {descriptor.DisplayName} 本地安装包",
            Filter = "便携安装包 (*.zip;*.exe)|*.zip;*.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true)
            return;

        card.BeginInstall("正在校验并部署本地安装包…");
        ShowStatus($"正在导入 {descriptor.DisplayName} 本地包…");

        var progress = new Progress<EngineInstallProgress>(p => card.ReportProgress(p));
        try
        {
            await engineManager.InstallFromLocalFileAsync(kind, dialog.FileName, progress);
            card.EndInstall();
            ClearManualOverrideIfEmptyOrMissing(kind);
            RefreshAllEngines();
            RefreshHostStatus();
            if (kind == EngineKind.EasyTier)
                await RefreshEasyTierStatusAsync();
            ShowStatus($"已成功导入托管版 {descriptor.DisplayName}。");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            card.EndInstall();
            RefreshAllEngines();
            ShowStatus($"导入 {descriptor.DisplayName} 失败：{error.Message}", warning: true);
        }
    }

    private void SelectEnginePathClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EngineKind kind })
            return;

        var descriptor = EngineCatalog.Get(kind);
        var dialog = new OpenFileDialog
        {
            Title = $"选择 {descriptor.DisplayName} 可执行文件",
            Filter = $"{descriptor.DisplayName} 程序 ({descriptor.ExecutableName};*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true)
            return;

        if (!string.Equals(Path.GetExtension(dialog.FileName), ".exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(dialog.FileName))
        {
            ShowStatus($"请选择有效的 {descriptor.DisplayName} .exe 可执行文件。", warning: true);
            return;
        }

        try
        {
            var updated = kind switch
            {
                EngineKind.Sunshine => settings with { SunshinePath = dialog.FileName },
                EngineKind.EasyTier => settings with { EasyTierPath = dialog.FileName },
                _ => settings,
            };

            SaveSettings(updated);
            RefreshAllEngines();
            RefreshHostStatus();
            if (kind == EngineKind.EasyTier)
                _ = RefreshEasyTierStatusAsync();
            ShowStatus($"已指定 {descriptor.DisplayName} 路径：{dialog.FileName}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowStatus($"无法保存 {descriptor.DisplayName} 路径设置。", warning: true);
        }
    }

    private void UninstallEngineClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EngineKind kind } || !engineCardMap.TryGetValue(kind, out var card))
            return;

        try
        {
            if (kind == EngineKind.EasyTier && EasyTierManager.IsMeshRunning())
                EasyTierManager.StopMesh();

            engineManager.UninstallManaged(kind);
            RefreshAllEngines();
            RefreshHostStatus();
            if (kind == EngineKind.EasyTier)
                _ = RefreshEasyTierStatusAsync();
            ShowStatus($"已卸载托管版 {card.DisplayName}。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowStatus($"卸载 {card.DisplayName} 失败：程序可能正在运行，请先关闭后再试。", warning: true);
        }
    }

    private void OpenEngineReleasesClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EngineKind kind })
            return;

        var descriptor = EngineCatalog.Get(kind);
        try
        {
            using var process = Process.Start(new ProcessStartInfo(descriptor.ReleasesPageUrl)
            {
                UseShellExecute = true,
            });
        }
        catch (Win32Exception)
        {
            ShowStatus($"无法打开浏览器，请手动访问 {descriptor.ReleasesPageUrl}。", warning: true);
        }
    }

    private void ClearManualOverrideIfEmptyOrMissing(EngineKind kind)
    {
        try
        {
            var updated = kind switch
            {
                EngineKind.Sunshine when !string.IsNullOrEmpty(settings.SunshinePath) && !File.Exists(settings.SunshinePath)
                    => settings with { SunshinePath = null },
                EngineKind.EasyTier when !string.IsNullOrEmpty(settings.EasyTierPath) && !File.Exists(settings.EasyTierPath)
                    => settings with { EasyTierPath = null },
                _ => settings,
            };

            if (!ReferenceEquals(updated, settings))
                SaveSettings(updated);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Ignore cleanup failure for stale override path.
        }
    }

    // ==================== DOWNLOAD MIRROR SETTINGS ====================

    private void SyncMirrorInputFromSettings()
    {
        MirrorPrefixInput.Text = settings.DownloadMirrorPrefix ?? "";
        MirrorPlaceholder.Visibility = string.IsNullOrEmpty(MirrorPrefixInput.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MirrorPrefixInputChanged(object sender, TextChangedEventArgs e)
    {
        if (MirrorPlaceholder is not null)
            MirrorPlaceholder.Visibility = string.IsNullOrEmpty(MirrorPrefixInput.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveMirrorClicked(object sender, RoutedEventArgs e)
    {
        var raw = MirrorPrefixInput.Text.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            ClearMirrorClicked(sender, e);
            return;
        }

        const string sampleUrl = "https://github.com/moonlight-stream/moonlight-qt/releases/download/v6.1.0/test.zip";
        var rewritten = EngineCatalog.ApplyMirror(sampleUrl, raw);
        if (string.Equals(rewritten, sampleUrl, StringComparison.Ordinal))
        {
            ShowMirrorFeedback("镜像前缀格式无效：请填写有效的 HTTPS 域名地址（例如 https://gh-proxy.com/）。", warning: true);
            return;
        }

        try
        {
            SaveSettings(settings with { DownloadMirrorPrefix = raw });
            ShowMirrorFeedback($"已保存镜像加速前缀：{raw}", warning: false);
            ShowStatus("已更新国内镜像加速设置。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowMirrorFeedback("无法保存镜像设置，请检查用户目录写入权限。", warning: true);
        }
    }

    private void ClearMirrorClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            MirrorPrefixInput.Text = "";
            SaveSettings(settings with { DownloadMirrorPrefix = null });
            ShowMirrorFeedback("已清空镜像设置，将直接从 GitHub 官方源下载。", warning: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowMirrorFeedback("无法清空镜像设置，请检查用户目录写入权限。", warning: true);
        }
    }

    private void ShowMirrorFeedback(string message, bool warning)
    {
        MirrorFeedbackText.Text = message;
        MirrorFeedbackText.SetResourceReference(TextBlock.ForegroundProperty, warning ? "Warning" : "Ready");
        MirrorFeedbackText.Visibility = Visibility.Visible;
    }
}
