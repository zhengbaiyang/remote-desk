<p align="center">
  <img src="src/RemoteDesk/Assets/remotedesk.png" alt="RemoteDesk 项目标志" width="80" height="80">
</p>

<h1 align="center">RemoteDesk 2.0（一体化高刷远程桌面平台）</h1>

<p align="center">面向 Windows 的一体化零配置高刷远程桌面软件：内置基于 moonlight-common-c + D3D11 硬件解码 + Opus/WASAPI 的原生控屏核心，配套 Sunshine 被控端、阿里云邀请码账号云同步与 EasyTier P2P 跨网虚拟组网，像 UU 远程一样打开即连。</p>

<p align="center">
  <a href="https://github.com/zhengbaiyang/remote-desk/releases/latest/download/RemoteDesk.Windows-win-Setup.exe"><strong>下载 Windows x64 安装包</strong></a> ·
  <a href="#核心能力">核心能力</a> ·
  <a href="docs/v2-architecture.md">v2.0 架构说明</a> ·
  <a href="docs/aliyun-server-guide.md">阿里云跨网节点指南</a> ·
  <a href="#从源码构建">从源码构建</a>
</p>

![RemoteDesk 实际运行界面。](docs/images/desktop.png)

## 核心能力

- **内置原生高刷控屏引擎（`RemoteDesk.StreamCore.dll`）**：
  - 无需再额外下载或弹出外部 `Moonlight.exe` / `RustDesk.exe` 窗口，从连接发起、硬件解码到键鼠音频均在主软件内置窗口中原生完成。
  - 基于 `moonlight-common-c` + Windows CNG（`bcrypt`）+ Media Foundation D3D11VA 硬解（H.264 / HEVC / AV1）+ Opus / WASAPI 低延迟音频渲染。
- **UU 远程风格操控体验**：
  - 默认以可缩放窗口（`1280×760`）打开，一键或按 `F11` 切换无边框全屏，全屏顶部悬浮控制条支持鼠标悬停呼出、移开自动收起。
  - **办公自由指针**（默认不锁鼠、高精度绝对坐标映射）与 **游戏相对锁定**（`WM_INPUT` 原始鼠标与系统键透传，`Ctrl+Alt` 随时释放）一键切换。
  - 支持会话内在线调节分辨率 / 帧率 / 码率 / 编码器并平滑重连，集成常用系统组合键（`Ctrl+Alt+Del`、`Win`、`Win+D`、`Alt+Tab`、`TaskMgr`、`Win+L`）、一键静音及可穿透点击的性能仪表盘 HUD。
- **协同办公与隐私防窥进阶能力**：
  - **双向剪贴板同步**：支持文本与截图实时双向同步。
  - **文件快传**：支持向控屏窗口直接拖拽文件或点击「传文件」高速发送至被控端 `下载\RemoteDesk`（基于同账号 HMAC-SHA256 鉴权与 SHA-256 落盘校验）。
  - **自动分辨率匹配与断开还原**：发起连接前自动适配被控端显示器分辨率，断开会话后自动恢复原桌面设置。
  - **防窥黑屏与远程电源控制**：利用 `WDA_EXCLUDEFROMCAPTURE` 让被控端物理显示器熄黑保护隐私而远控画面保持清晰（被控端按 `Ctrl+Alt+End` 可紧急解除）；支持远程锁屏、重启与关机。
- **账号云同步 · 零配置自动组网与配对**：
  - 邀请码注册/登录同账号，阿里云服务端（[`server/sync/`](server/sync/)）为每台设备自动分配独立虚拟子网（`10.126.126.2..254`）与隔离认证密钥，配合后台守护服务（`RemoteDeskAgent`）实现开机/锁屏在线与零 PIN 自动配对。
- **精简托管引擎中心与热更新**：
  - 统一管理 **Sunshine**（高刷被控端）与 **EasyTier**（P2P 跨网组网）两大开源引擎，支持国内 HTTPS 镜像加速、离线压缩包导入以及 GitHub Release / Velopack 自动更新。

## 从源码构建

在 Windows 上安装 Visual Studio 2022 Build Tools（C++ 桌面工作负载，含 CMake/Ninja）与 .NET 8 SDK：

```powershell
# 1. 拉取第三方原生依赖（third_party/moonlight-common-c 与 opus）
.\native\fetch_deps.ps1

# 2. 编译内置原生串流内核 RemoteDesk.StreamCore.dll
.\native\build_streamcore.ps1

# 3. 编译 Release 客户端
dotnet build src/RemoteDesk/RemoteDesk.csproj -c Release

# 4. 运行全部核心、网络、同步、原生协议与协同单元测试
dotnet run --project tests/RemoteDesk.Checks/RemoteDesk.Checks.csproj -c Release

# 5. 生成 Windows x64 安装包与 Velopack 更新文件
.\packaging\build.ps1
```

## 开源许可

本项目以 **GNU General Public License v3.0 (GPL-3.0)** 开源协议发布，详见 [LICENSE](LICENSE)（与内置的 `moonlight-common-c` 兼容；音频编解码器 Opus 采用 3-Clause BSD，EasyTier 采用 Apache-2.0，Velopack 采用 MIT）。
