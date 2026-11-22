using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace RemoteDesk;

public partial class MainWindow : Window
{
    private readonly SettingsStore store = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteDesk", "settings.json"));
    private AppSettings settings = new();

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            settings = store.Load();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            ShowStatus("无法读取之前的设置。你可以重新添加电脑。", warning: true);
        }

        ShowDevice();
        RefreshClientStatus();
        Loaded += (_, _) => FocusDeviceAction();
        Activated += (_, _) => RefreshClientStatus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        int darkTitleBar = 0;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref darkTitleBar, sizeof(int));
    }

    private void ShowDevicesPage(object sender, RoutedEventArgs e)
    {
        SetCurrentPage(showSettings: false);
        if (EditorView.Visibility == Visibility.Visible)
            IdInput.Focus();
        else
            FocusDeviceAction();
    }

    private void ShowSettingsPage(object sender, RoutedEventArgs e)
    {
        SetCurrentPage(showSettings: true);
        RefreshClientStatus();
        SelectClientButton.Focus();
    }

    private void SetCurrentPage(bool showSettings)
    {
        DevicePage.Visibility = showSettings ? Visibility.Collapsed : Visibility.Visible;
        SettingsPage.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;
        CurrentPage.Text = showSettings ? "RustDesk 设置" : "我的电脑";
        DevicesNav.Tag = SidebarDevice.Tag = showSettings ? null : "Selected";
        SettingsNav.Tag = showSettings ? "Selected" : null;
        ShowStatus("");
    }

    private void EditDevice(object sender, RoutedEventArgs e)
    {
        EditorTitle.Text = settings.Device is null ? "添加电脑" : "编辑电脑";
        NameInput.Text = settings.Device?.Name ?? "家里的电脑";
        IdInput.Text = settings.Device?.Id ?? "";
        ShowEditorMessage("在目标电脑的 RustDesk 首页找到设备 ID。");
        ShowStatus("");

        EmptyView.Visibility = Visibility.Collapsed;
        DeviceView.Visibility = Visibility.Collapsed;
        ConnectionTip.Visibility = Visibility.Collapsed;
        EditorView.Visibility = Visibility.Visible;
        IdInput.Focus();
        IdInput.SelectAll();
    }

    private void CancelEdit(object sender, RoutedEventArgs e)
    {
        ShowDevice();
        FocusDeviceAction();
    }

    private void SaveDevice(object sender, RoutedEventArgs e)
    {
        try
        {
            var device = DeviceProfile.Create(NameInput.Text, IdInput.Text);
            SaveSettings(settings with { Device = device });
            ShowDevice();
            FocusDeviceAction();
        }
        catch (ArgumentException error)
        {
            var invalidName = error.ParamName == "name";
            ShowEditorMessage(invalidName
                ? "请输入 1 到 40 个字符的电脑名称。"
                : "请输入目标电脑在 RustDesk 中显示的设备 ID。", warning: true);
            (invalidName ? NameInput : IdInput).Focus();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowEditorMessage("无法保存设置。请检查本机用户目录的写入权限。", warning: true);
        }
    }

    private void ConnectDevice(object sender, RoutedEventArgs e)
    {
        if (settings.Device is not { } device)
            return;

        var executable = RefreshClientStatus();
        if (executable is null)
        {
            ShowStatus("请下载 RustDesk，或选择你已有的 RustDesk 程序，再点击连接。", warning: true);
            return;
        }

        try
        {
            using var process = Process.Start(RustDeskClient.CreateConnection(executable, device));
            ShowStatus("已向 RustDesk 发起连接，请在会话窗口查看结果。");
        }
        catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException)
        {
            RefreshClientStatus();
            ShowStatus("无法启动 RustDesk。请重新选择程序，或检查 RustDesk 能否正常打开。", warning: true);
        }
    }

    private void SelectRustDesk(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 RustDesk 程序",
            Filter = "RustDesk 程序 (rustdesk*.exe)|rustdesk*.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true)
            return;

        if (!Path.GetFileName(dialog.FileName).StartsWith("rustdesk", StringComparison.OrdinalIgnoreCase) ||
            RustDeskClient.FindExecutable(dialog.FileName) != dialog.FileName)
        {
            ShowStatus("请选择 RustDesk 的 .exe 文件。", warning: true);
            return;
        }

        try
        {
            SaveSettings(settings with { ClientPath = dialog.FileName });
            RefreshClientStatus();
            ShowStatus("");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowStatus("无法保存 RustDesk 程序的位置。", warning: true);
        }
    }

    private void OpenRustDeskDownload(object sender, RoutedEventArgs e)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("https://github.com/rustdesk/rustdesk/releases/latest")
            {
                UseShellExecute = true,
            });
        }
        catch (Win32Exception)
        {
            ShowStatus("无法打开浏览器，请访问 RustDesk 官方 GitHub 发布页下载。", warning: true);
        }
    }

    private void ShowDevice()
    {
        var device = settings.Device;
        EmptyView.Visibility = device is null ? Visibility.Visible : Visibility.Collapsed;
        DeviceView.Visibility = device is null ? Visibility.Collapsed : Visibility.Visible;
        EditorView.Visibility = Visibility.Collapsed;
        ConnectionTip.Visibility = DeviceView.Visibility;
        SidebarEmpty.Visibility = EmptyView.Visibility;
        SidebarDevice.Visibility = DeviceView.Visibility;
        SidebarName.Text = device?.Name ?? "";
        AutomationProperties.SetName(SidebarDevice, SidebarName.Text);
        DeviceNameText.Text = device?.Name ?? "";
        DeviceIdText.Text = device is null ? "" : $"设备 ID  {device.Id}";
    }

    private void FocusDeviceAction() => (settings.Device is null ? AddDeviceButton : ConnectButton).Focus();

    private string? RefreshClientStatus()
    {
        var executable = RustDeskClient.FindExecutable(settings.ClientPath);
        ClientDot.Fill = (Brush)FindResource(executable is null ? "Warning" : "Ready");
        ClientStatus.Text = executable is null ? "未找到 RustDesk" : "已找到 RustDesk";
        ClientStatus.ToolTip = executable;
        ClientPathText.Text = executable ?? "请先下载 RustDesk，或选择已有的 RustDesk 程序。";
        return executable;
    }

    private void SaveSettings(AppSettings updated)
    {
        store.Save(updated);
        settings = updated;
    }

    private void ShowStatus(string message, bool warning = false)
    {
        StatusMessage.Text = message;
        StatusMessage.Foreground = (Brush)FindResource(warning ? "Warning" : "Muted");
        StatusMessage.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowEditorMessage(string message, bool warning = false)
    {
        EditorMessage.Text = message;
        EditorMessage.Foreground = (Brush)FindResource(warning ? "Warning" : "Muted");
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, uint attribute, ref int value, uint size);
}
