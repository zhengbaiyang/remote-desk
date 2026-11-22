# RemoteDesk 远程桌面

RemoteDesk 是一个 Windows 桌面启动器。保存自己的电脑后，从首页点一次「连接」即可打开 RustDesk 远程会话。

![RemoteDesk 首页，显示示例设备](docs/images/desktop.png)

## 功能

- 保存设备名称和 RustDesk ID，重启后可继续使用。在「编辑」中可以更新资料或移除设备。
- 页面顶部支持拖动、双击最大化和窗口控制。首页显示本机客户端准备状态，缺少客户端时点「连接」会打开设置。
- 自动查找本机 RustDesk，也支持手动选择便携版程序。
- 在首页发起会话，连接状态以 RustDesk 的会话窗口为准。
- 支持两端使用同一套 RustDesk 自托管服务。直连与中继由 RustDesk 选择。

## 下载与安装

1. 在控制端下载 `RemoteDesk-v0.3.0-win-x64.zip`，解压后运行 `RemoteDesk.exe`。压缩包内已包含 .NET 运行时。 [GitHub Releases](https://github.com/zhengbaiyang/remote-desk/releases/latest)
2. 在控制端和被控端准备 RustDesk。安装版通常会被自动找到。使用便携版时，在 RemoteDesk 左侧打开「RustDesk 设置」，点击「选择程序」并选择 RustDesk 的 `.exe`。 [RustDesk 官方下载](https://github.com/rustdesk/rustdesk/releases/latest)
3. 在被控端的 RustDesk 首页查看设备 ID。需要无人值守时，在被控端的 RustDesk 安全设置中设置固定密码，并保持 RustDesk 运行。
4. 在 RemoteDesk 的「我的电脑」页点击「添加电脑」，保存名称和设备 ID。点击「连接」，在 RustDesk 的远程会话中完成首次验证。使用固定密码时可以勾选「记住密码」，下次直接点击「连接」。

如果使用自托管服务，请在两端的 RustDesk 客户端填写同一套服务配置，再用 RemoteDesk 发起连接。 [RustDesk 自托管说明](https://rustdesk.com/docs/en/self-host/)

## 本地构建与测试

使用 Windows 和 .NET 8 SDK。在仓库根目录执行以下命令。

```powershell
dotnet run --project tests/RemoteDesk.Checks/RemoteDesk.Checks.csproj -c Release
dotnet run --project src/RemoteDesk/RemoteDesk.csproj
dotnet publish src/RemoteDesk/RemoteDesk.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o artifacts/win-x64
```

发布命令生成 `artifacts/win-x64/RemoteDesk.exe`。

## 技术来源与本地数据

RemoteDesk 使用 C# 和 WPF 实现界面，通过 RustDesk 官方命令 `rustdesk --connect <设备ID>` 发起桌面会话。认证、屏幕传输、键鼠输入和 P2P 建连全部由单独安装的 RustDesk（AGPL-3.0）处理。 [RustDesk 源码](https://github.com/rustdesk/rustdesk) · [RustDesk 命令行说明](https://github.com/rustdesk/rustdesk/wiki/FAQ#command-line)

P2P 调研参考了 `uu-remote-study` 的打洞和传输实验。 [参考项目](https://github.com/numb747/uu-remote-study)

设备名称、设备 ID 和手动选择的客户端路径保存在 `%LOCALAPPDATA%\RemoteDesk\settings.json`。勾选「记住密码」时，密码由 RustDesk 管理。
