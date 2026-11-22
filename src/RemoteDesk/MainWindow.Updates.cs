using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Velopack;

namespace RemoteDesk;

public partial class MainWindow
{
    private readonly UpdateManager updates = new("https://github.com/zhengbaiyang/remote-desk/releases/latest/download/");
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private VelopackAsset? readyToInstall;
    private bool checkingUpdates;

    private void InitializeUpdates()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        AppVersion.Text = $"RemoteDesk  {version?.ToString(3)}";
        if (!updates.IsInstalled)
        {
            UpdateStatus.Text = "安装 GitHub 发布页提供的安装版后，即可自动更新。";
            UpdateButton.IsEnabled = false;
            return;
        }

        Loaded += async (_, _) => await CheckUpdatesAsync();
        updateTimer.Tick += async (_, _) => await CheckUpdatesAsync();
        updateTimer.Start();
        Closed += (_, _) => updateTimer.Stop();
    }

    private async void UpdateClicked(object sender, RoutedEventArgs e)
    {
        if (readyToInstall is null)
        {
            await CheckUpdatesAsync();
            return;
        }

        try
        {
            updates.ApplyUpdatesAndRestart(readyToInstall);
        }
        catch (Exception)
        {
            UpdateStatus.Text = "暂时无法重启更新。请关闭 RemoteDesk 后重新打开。";
        }
    }

    private async Task CheckUpdatesAsync()
    {
        if (checkingUpdates || readyToInstall != null)
            return;
        checkingUpdates = true;
        UpdateButton.IsEnabled = false;
        try
        {
            UpdateStatus.Text = "正在检查新版本…";
            var update = await updates.CheckForUpdatesAsync();
            if (update is null)
            {
                UpdateStatus.Text = "当前已是最新版本。";
                return;
            }

            UpdateStatus.Text = $"正在下载 {update.TargetFullRelease.Version}…";
            await updates.DownloadUpdatesAsync(update);
            readyToInstall = update.TargetFullRelease;
            UpdateStatus.Text = $"已准备好 {readyToInstall.Version}。下次启动时将自动更新。";
            UpdateButton.Content = "立即重启更新";
        }
        catch (Exception)
        {
            UpdateStatus.Text = "检查或下载更新失败。你可以稍后重试，或等待下次自动检查。";
        }
        finally
        {
            checkingUpdates = false;
            UpdateButton.IsEnabled = true;
        }
    }
}
