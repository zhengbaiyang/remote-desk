<p align="center">
  <img src="src/RemoteDesk/Assets/remotedesk.png" alt="RemoteDesk 项目标志" width="80" height="80">
</p>

<h1 align="center">RemoteDesk 远程桌面（校园网高刷 &amp; 跨网组网版）</h1>

<p align="center">面向 Windows 的双引擎远程桌面工作台：同校园网 / 局域网 120fps 高刷串流直连，跨外网通过 EasyTier P2P 虚拟组网与阿里云自建中继无缝穿透。</p>

<p align="center">
  <a href="https://github.com/zhengbaiyang/remote-desk/releases/latest/download/RemoteDesk.Windows-win-Setup.exe"><strong>下载 Windows x64 安装包</strong></a> ·
  <a href="#核心能力">核心能力</a> ·
  <a href="docs/aliyun-server-guide.md">阿里云跨网节点指南</a> ·
  <a href="#从源码构建">从源码构建</a>
</p>

![RemoteDesk 实际运行界面。](docs/images/desktop.png)

## 核心能力

- **双引擎无缝切换（远程控制）**：
  - **高刷串流（Moonlight + Sunshine）**：支持 1080p 120fps / 2K 60fps / 省流与自定义分辨率、帧率、码率（2–150 Mbps）、HEVC / AV1 / H.264 硬件编解码。
  - **办公模式（RustDesk）**：通过设备 ID 或直连地址快速接入，适合跨网协作、剪贴板共享与文件传输。
- **托管式引擎中心（引擎与设置）**：
  - 统一管理 **Moonlight**、**Sunshine**、**RustDesk** 与 **EasyTier** 四大开源引擎。
  - 支持从 GitHub Release **一键安装**便携版至 `%LOCALAPPDATA%\RemoteDesk\engines\`、**导入本地压缩包**、**手动指定路径**及**卸载托管**。
  - 内置 **国内 HTTPS 镜像加速**（失败时自动回退直连 GitHub），同时作用于引擎拉取与软件更新。
- **本机被控中心（本机被控）**：
  - 一键启停本机 Sunshine 高刷被控端与 RustDesk 办公被控端，快捷打开 Sunshine 控制台（`https://localhost:47990`）。
  - 自动展示本机校园网 / 局域网 IPv4、IPv6 地址与主机名（支持一键复制），并识别 NVIDIA NVENC / Intel QSV / AMD AMF 硬件编码器。
- **EasyTier P2P 跨网组网（网络节点）**：
  - 保留校园网 / 局域网零服务器直连概览，同时提供 EasyTier P2P 虚拟组网控制面板（节点地址、网络名称、可掩码密钥、自动分配虚拟 IP、随软件自动启动）。
  - 实时展示在线组网节点、虚拟 IPv4、`直连 (P2P)` / `中继 (Relay)` 状态与延迟，支持**一键添加到设备**并发起 Moonlight 高刷串流。
  - 配套阿里云轻量服务器 3 分钟部署套件：[`server/docker-compose.yml`](server/docker-compose.yml)、[`server/deploy.sh`](server/deploy.sh)、[`server/.env.example`](server/.env.example) 与完整中文教程 [`docs/aliyun-server-guide.md`](docs/aliyun-server-guide.md)。
- **GitHub Release 自动更新**：
  - 支持自定义更新仓库源（默认 `zhengbaiyang/remote-desk`，可输入 `owner/repo` 或完整 GitHub HTTPS 链接）。
  - **Velopack 安装版**：启动时及每 6 小时自动检测并后台下载，支持「立即重启更新」与国内镜像加速回退。
  - **便携版 / 开发版**：同样自动检测仓库最新 Release，展示版本号与更新说明，支持一键「下载并安装最新版」或跳转发布页。

## 首次使用

1. **安装引擎**：打开 RemoteDesk，进入「引擎与设置」，按需一键安装 **Moonlight**（控制端）、**Sunshine**（被控端）、**RustDesk**（办公端）或 **EasyTier**（跨网组网）。如访问 GitHub 较慢，可先填写国内 HTTPS 镜像前缀。
2. **校园网 / 局域网直连**：在被控电脑打开「本机被控」启动 Sunshine 并复制本机 IPv4 / IPv6；在操作端「远程控制」添加电脑、发起首次配对后即可 120fps 直连。
3. **跨外网 / 回家远控宿舍电脑**：按 [`docs/aliyun-server-guide.md`](docs/aliyun-server-guide.md) 在 2C2G 阿里云轻量服务器运行 `./server/deploy.sh`，随后在两端 RemoteDesk 的「网络节点」填入相同的网络名称、密钥与节点地址（`tcp://<公网IP>:11010`）并启动组网，在节点列表点击「添加到设备」即可跨网高刷连接。

所有设备配置、引擎路径、EasyTier 参数、镜像前缀与更新仓库源保存在 `%LOCALAPPDATA%\RemoteDesk\settings.json`，与程序安装目录分离。

## 从源码构建

在 Windows 上安装 .NET 8 SDK，克隆仓库后在根目录打开 PowerShell：

```powershell
# 编译 Release
dotnet build src/RemoteDesk/RemoteDesk.csproj -c Release

# 运行全部核心、引擎、EasyTier 与自动更新单元测试
dotnet run --project tests/RemoteDesk.Checks/RemoteDesk.Checks.csproj -c Release

# 启动桌面程序
dotnet run --project src/RemoteDesk/RemoteDesk.csproj

# 生成 Windows x64 安装包与 Velopack 更新文件
.\packaging\build.ps1
```

构建产物位于 `artifacts/releases/<版本>/`。发布新版时递增 `src/RemoteDesk/RemoteDesk.csproj` 的 `Version` 并重新打包，将 `RemoteDesk.Windows-win-Setup.exe`、`releases.win.json` 和对应的 `*-full.nupkg` 一同上传至 GitHub Release 并设为 Latest。

## 开源许可

RemoteDesk 自有代码、部署脚本、文档和图标采用 [MIT 许可证](LICENSE)。安装与热更新由 Velopack（MIT）驱动；Moonlight（GPL-3.0）、Sunshine（GPL-3.0）、RustDesk（AGPL-3.0）与 EasyTier（Apache-2.0）作为独立托管外部进程运行，各自遵循其上游开源协议。
