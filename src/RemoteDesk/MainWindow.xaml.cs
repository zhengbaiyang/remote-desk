using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;

namespace RemoteDesk;

public partial class MainWindow : Window
{
    private readonly SettingsStore store = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteDesk", "settings.json"));
    private AppSettings settings = new();
    private DeviceProfile? editingDevice;
    private DeviceProfile? SelectedDevice => DeviceList.SelectedItem as DeviceProfile;

    public MainWindow()
    {
        InitializeComponent();
        InitializeUpdates();
        try
        {
            settings = store.Load();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            ShowStatus("无法读取之前的设置。你可以重新添加电脑。", warning: true);
        }

        ApplyTheme(settings.UseDarkTheme);
        RefreshDevices(0);
        RefreshClientStatus();
        Loaded += (_, _) => FocusDeviceAction();
        Activated += (_, _) => RefreshClientStatus();
    }

    private void MinimizeWindow(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void CloseWindow(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void ToggleMaximized(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "还原" : "最大化";
        AutomationProperties.SetName(MaximizeButton, (string)MaximizeButton.ToolTip);
    }

    private void ShowDevicesPage(object sender, RoutedEventArgs e)
    {
        SetCurrentPage(showSettings: false);
        if (RemovalView.IsVisible)
            KeepDeviceButton.Focus();
        else if (EditorView.Visibility == Visibility.Visible)
            IdInput.Focus();
        else
            FocusDeviceAction();
    }

    private void ShowSettingsPage(object sender, RoutedEventArgs e)
    {
        SetCurrentPage(showSettings: true);
        RefreshClientStatus();
        RescanButton.Focus();
    }

    private void SetCurrentPage(bool showSettings)
    {
        DevicePage.Visibility = showSettings ? Visibility.Collapsed : Visibility.Visible;
        SettingsPage.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;
        CurrentPage.Text = showSettings ? "设置" : "设备详情";
        DevicesNav.Tag = showSettings ? null : "Selected";
        SettingsNav.Tag = showSettings ? "Selected" : null;
        ShowStatus("");
    }

    private void AddDevice(object sender, RoutedEventArgs e)
    {
        SetCurrentPage(showSettings: false);
        OpenEditor(null);
    }

    private void EditDevice(object sender, RoutedEventArgs e) => OpenEditor(SelectedDevice);

    private void OpenEditor(DeviceProfile? device)
    {
        editingDevice = device;
        RemoveButton.Visibility = device is null ? Visibility.Collapsed : Visibility.Visible;
        EditorTitle.Text = device is null ? "添加电脑" : "编辑电脑";
        NameInput.Text = device?.Name ?? "";
        IdInput.Text = device?.Id ?? "";
        ShowEditorMessage("在目标电脑的 RustDesk 首页找到设备 ID。");
        ShowStatus("");

        EmptyView.Visibility = Visibility.Collapsed;
        DeviceView.Visibility = Visibility.Collapsed;
        SetDeviceListEnabled(false);
        EditorView.Visibility = Visibility.Visible;
        (device is null ? NameInput : IdInput).Focus();
    }

    private void CancelEdit(object sender, RoutedEventArgs e)
    {
        ShowDevice();
        FocusDeviceAction();
    }

    private void RequestRemoval(object sender, RoutedEventArgs e)
    {
        if (editingDevice is not { } device)
            return;

        RemovalMessage.Text = $"将从本机列表中移除「{device.Name}」。之后可以用设备 ID 重新添加。";
        EditorView.Visibility = Visibility.Collapsed;
        RemovalView.Visibility = Visibility.Visible;
        KeepDeviceButton.Focus();
    }

    private void KeepDevice(object sender, RoutedEventArgs e)
    {
        RemovalView.Visibility = Visibility.Collapsed;
        EditorView.Visibility = Visibility.Visible;
        RemoveButton.Focus();
    }

    private void RemoveDevice(object sender, RoutedEventArgs e)
    {
        try
        {
            var index = DeviceList.SelectedIndex;
            if (editingDevice is null || index < 0 || !ReferenceEquals(editingDevice, SelectedDevice))
                return;
            var devices = settings.Devices.ToList();
            devices.RemoveAt(index);
            SaveSettings(settings with { Devices = devices });
            RefreshDevices(Math.Min(index, devices.Count - 1));
            ShowStatus("已从本机列表移除电脑。");
            FocusDeviceAction();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            KeepDevice(sender, e);
            ShowEditorMessage("无法保存更改，请检查本机用户目录的写入权限。", warning: true);
        }
    }

    private void SaveDevice(object sender, RoutedEventArgs e)
    {
        try
        {
            var device = DeviceProfile.Create(NameInput.Text, IdInput.Text);
            var devices = settings.Devices.ToList();
            if (devices.Any(existing => !ReferenceEquals(existing, editingDevice) &&
                string.Equals(existing.Id, device.Id, StringComparison.OrdinalIgnoreCase)))
            {
                ShowEditorMessage("这个设备 ID 已在列表中。", warning: true);
                IdInput.Focus();
                return;
            }

            var index = devices.Count;
            if (editingDevice is null)
                devices.Add(device);
            else
            {
                index = DeviceList.SelectedIndex;
                devices[index] = device;
            }
            SaveSettings(settings with { Devices = devices });
            RefreshDevices(index);
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
        if (SelectedDevice is not { } device)
            return;

        var executable = RefreshClientStatus();
        if (executable is null)
        {
            ShowSettingsPage(sender, e);
            ShowStatus("请先安装 RustDesk，返回此窗口后会自动检测。", warning: true);
            return;
        }

        try
        {
            using var process = Process.Start(RustDeskClient.CreateConnection(executable, device));
            ShowStatus("已向 RustDesk 发起连接，请在会话窗口查看结果。");
        }
        catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException)
        {
            ShowSettingsPage(sender, e);
            ShowStatus("无法启动 RustDesk。请检查 RustDesk 能否正常打开，再点「重新检测」。", warning: true);
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

    private void RefreshDevices(int index)
    {
        DeviceList.ItemsSource = settings.Devices;
        DeviceList.SelectedIndex = settings.Devices.Count == 0 ? -1 : index;
        DeviceCount.Text = $"{settings.Devices.Count} 台";
        DeviceListEmpty.Visibility = settings.Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (SelectedDevice is { } device)
            DeviceList.ScrollIntoView(device);
        ShowDevice();
    }

    private void DeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedDevice != null && SettingsPage.IsVisible)
            SetCurrentPage(showSettings: false);
        ShowDevice();
        ShowStatus("");
    }

    private void SetDeviceListEnabled(bool enabled)
    {
        DeviceList.IsEnabled = enabled;
        AddFromListButton.IsEnabled = enabled;
    }

    private void ShowDevice()
    {
        var device = SelectedDevice;
        editingDevice = null;
        SetDeviceListEnabled(true);
        EmptyView.Visibility = device is null ? Visibility.Visible : Visibility.Collapsed;
        DeviceView.Visibility = device is null ? Visibility.Collapsed : Visibility.Visible;
        EditorView.Visibility = Visibility.Collapsed;
        RemovalView.Visibility = Visibility.Collapsed;
        DeviceNameText.Text = device?.Name ?? "";
        DeviceIdText.Text = device is null ? "" : $"设备 ID  {device.Id}";
    }

    private void FocusDeviceAction() => (SelectedDevice is null ? AddDeviceButton : ConnectButton).Focus();

    private void RescanClient(object sender, RoutedEventArgs e) => RefreshClientStatus();

    private string? RefreshClientStatus()
    {
        var executable = RustDeskClient.FindExecutable(settings.ClientPath);
        ClientDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, executable is null ? "Warning" : "Ready");
        ClientStatus.Text = executable is null ? "未找到 RustDesk" : "已找到 RustDesk";
        ClientStatus.ToolTip = executable;
        ClientPathText.Text = executable ?? "尚未检测到 RustDesk。安装后回到此窗口，或点击「重新检测」。";
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
        StatusMessage.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, warning ? "Warning" : "Muted");
        StatusMessage.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowEditorMessage(string message, bool warning = false)
    {
        EditorMessage.Text = message;
        EditorMessage.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, warning ? "Warning" : "Muted");
    }

    private void ToggleTheme(object sender, RoutedEventArgs e)
    {
        var updated = settings with { UseDarkTheme = !settings.UseDarkTheme };
        try
        {
            SaveSettings(updated);
            ApplyTheme(updated.UseDarkTheme);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowStatus("无法保存外观设置。请检查本机用户目录的写入权限。", warning: true);
        }
    }

    private void ApplyTheme(bool dark)
    {
        var dictionaries = Resources.MergedDictionaries;
        var palette = dark ? "DarkTheme.xaml" : "LightTheme.xaml";
        if (dictionaries[0].Source.OriginalString != palette)
            dictionaries[0] = new ResourceDictionary { Source = new Uri(palette, UriKind.Relative) };

        ThemeButton.Content = dark ? "\uE706" : "\uE708";
        ThemeButton.ToolTip = dark ? "切换到浅色外观" : "切换到深色外观";
        AutomationProperties.SetName(ThemeButton, (string)ThemeButton.ToolTip);
    }
}
