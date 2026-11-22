<p align="center">
  <img src="src/RemoteDesk/Assets/remotedesk.png" alt="RemoteDesk 项目标志" width="80" height="80">
</p>

<h1 align="center">RemoteDesk 远程桌面</h1>

<p align="center">在 Windows 上保存家里电脑的 RustDesk ID，下次点一下「连接」就能发起远程会话。</p>

<p align="center">
  <a href="https://github.com/zhengbaiyang/remote-desk/releases/latest"><strong>下载 Windows x64 版</strong></a> ·
  <a href="#首次连接">首次连接</a> ·
  <a href="#从源码构建">从源码构建</a>
</p>

![RemoteDesk 实际运行界面。左侧是已保存的电脑，右侧设备卡提供编辑、连接和客户端设置入口。](docs/images/desktop.png)

## 首次连接

准备两台电脑。**操作端**是你手边的 Windows 电脑，**目标端**是你想远程使用的那台电脑。

| 在哪台电脑上 | 做什么 |
| --- | --- |
| 目标端 | 安装并打开 RustDesk，记下首页显示的设备 ID，保持电脑唤醒。需要无人值守时，可在 RustDesk 安全设置中设置固定密码。[RustDesk 官方下载](https://github.com/rustdesk/rustdesk/releases/latest) |
| 操作端 | 安装 RustDesk，然后从发布页的 **Assets** 下载文件名以 `win-x64.zip` 结尾的压缩包。解压并运行 `RemoteDesk.exe`，包内已包含 .NET 运行时。[RemoteDesk 下载](https://github.com/zhengbaiyang/remote-desk/releases/latest) |

在 RemoteDesk 的「我的电脑」里点 **添加电脑**，输入名称和目标端的设备 ID，保存后点 **连接**。首次验证在 RustDesk 会话窗口完成。以后打开 RemoteDesk，首页会直接显示这台电脑。

## 设备与连接

- **设备管理**　当前版本保存一台常用电脑。点「编辑」可以改名、更换设备 ID，或移除这条记录。
- **找不到 RustDesk**　在「RustDesk 设置」里点「选择程序」，选中本机的 RustDesk 可执行文件。便携版也可以这样配置。
- **目标端连不上**　首页的「已找到 RustDesk」表示操作端已找到客户端。请在会话窗口查看连接结果，并确认目标端已唤醒、RustDesk 正在运行。
- **使用自建服务器**　在两端的 RustDesk 中填写同一套服务配置，再回到 RemoteDesk 点击连接。[RustDesk 客户端配置](https://rustdesk.com/docs/en/self-host/client-configuration/)

设备名、设备 ID 和手动选择的程序路径保存在 `%LOCALAPPDATA%\RemoteDesk\settings.json`。密码由 RustDesk 管理。升级 RemoteDesk 时，解压新版运行即可沿用已保存的电脑。

## 从源码构建

在 Windows 上安装 .NET 8 SDK，克隆仓库后，在仓库根目录打开 PowerShell。

```powershell
# 运行设备校验、配置存储和客户端启动参数检查
dotnet run --project tests/RemoteDesk.Checks/RemoteDesk.Checks.csproj -c Release

# 启动桌面程序
dotnet run --project src/RemoteDesk/RemoteDesk.csproj

# 发布包含运行时的 Windows x64 单文件程序
dotnet publish src/RemoteDesk/RemoteDesk.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o artifacts/win-x64
```

构建产物位于 `artifacts/win-x64/RemoteDesk.exe`。要从开发版本发起实际会话，也需要单独安装或选择 RustDesk。

## 项目来源

RemoteDesk 的设备管理界面使用 C# 和 WPF 实现。点击连接时，它调用本机的 `rustdesk --connect <设备ID>`。身份验证、画面传输、键鼠控制，以及 P2P 直连和中继选择，均由 RustDesk（AGPL-3.0）负责。[RustDesk 源码](https://github.com/rustdesk/rustdesk)

项目早期对 P2P 打洞与低延迟传输的调研参考了 uu-remote-study。当前发行版使用 RustDesk 建立会话。[uu-remote-study 研究项目](https://github.com/numb747/uu-remote-study)
