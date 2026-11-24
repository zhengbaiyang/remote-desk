# RemoteDesk v2.0 架构设计（类 UU 远程一体化平台）

本文档记录 RemoteDesk 从「双引擎启动器」演进为 **一体化零配置高刷远程桌面平台（类 UU 远程）** 的三阶段完整架构设计。

---

## 里程碑 1：账号同步 + 组网修复 + 无人值守托管（已完成）

### 1. 彻底解决 EasyTier「等待分配 IP」与代理网卡污染
- **根因**：
  1. `RemoteDesk.exe` 以普通用户权限运行并拉起 `easytier-core.exe -d` 时，Windows 拒绝非管理员进程创建 Wintun 虚拟网卡，导致即便已打洞连上阿里云节点（显示 `直连 (P2P)`），虚拟 IPv4 仍为空。
  2. `HostServiceManager.GetLocalNetworkInfo()` 未过滤 Clash/Mihomo Fake-IP（`198.18.0.0/15`）及虚拟/代理网卡，导致首选 IPv4 误展示 `198.18.0.1`。
- **方案**：
  - 过滤 `198.18.0.0/15` 及常见代理/虚拟网卡（Clash、Mihomo、Wintun、EasyTier、WireGuard、Hyper-V、VMware 等），物理网卡优先排序。
  - 新增轻量 **Windows 后台守护服务 `RemoteDeskAgent`（`LocalSystem`）**，仅在首次启用时请求一次 UAC；此后随系统自启、后台常驻运行 `easytier-core.exe` 与 `sunshine.exe`，彻底消除 UAC 弹窗并支持开机未登录桌面/锁屏界面直连及远程点击 UAC 弹窗。
  - 登录同步账号后，由阿里云同步服务为每台设备在账号专属虚拟子网（`10.126.126.0/24`）内分配唯一、持久的静态虚拟 IP（`10.126.126.2` ~ `.254`），`RemoteDeskAgent` 直接以 `-i <虚拟IP>/24` 启动 EasyTier，避免 `-d` DHCP 冲突与分配失败。
  - 在线节点列表中对无 TUN 网卡的纯中继服务端（如 `aliyun-relay`）明确标注 `中继节点（无虚拟 IP）`。

### 2. 阿里云轻量账号与设备同步服务（`server/sync/`）
- **技术栈**：零外部依赖 Python 3.12 标准库（`ThreadingHTTPServer` + `sqlite3` WAL + `hashlib.scrypt` + `ssl`），Docker 容器内存上限仅 128MB（常驻约 20MB）。
- **防蹭网与账号隔离**：
  - 采用 **邀请码注册模式**（`deploy.sh` 自动生成高强度注册邀请码 `SYNC_INVITE_CODE`），彻底防止陌生人扫描公网 IP 后注册蹭中继。
  - 每个账号拥有独立隔离的 EasyTier 虚拟网络（`networkName = {PREFIX}-{12位随机hex}`、`32位随机 networkSecret`、`64位随机 agentAuthKey`）。
  - `server/docker-compose.yml` 中的 EasyTier 中继白名单设置为 `${EASYTIER_NETWORK_PREFIX}-*`，仅放行本平台合法账号的虚拟网络。
  - `server/deploy.sh` 自动生成带 SAN（IP/DNS）的 10 年期自签名 TLS 证书并输出 SHA-256 指纹，客户端采用 TOFU（首次信任并固定证书指纹）校验通信安全。
- **设备云同步**：
  - 节点每 20~30 秒向 `/api/v1/devices/checkin` 心跳上报（包含服务内后台上报，保证锁屏/未打开 GUI 时仍在线），云端同步设备名称、固定虚拟 IP、局域网地址、GPU 编码器、Sunshine 就绪状态及是否开启独立安全密码。

### 3. 零手动配置被控与静默自动配对
- `RemoteDeskAgent` 自动维护本地 Sunshine 安全配置（`origin_web_ui_allowed = pc`、`upnp = off`），生成随机高强度管理密码并经 `--creds` 注入，密码密文使用 Windows DPAPI（`LocalMachine` 作用域）本地加密持久化，用户无需打开浏览器配置 `https://localhost:47990`。
- 同账号设备间通信通过 `TCP 47999` 节点控制端口（`AgentPeerServer`），所有请求均强制使用账号级 `agentAuthKey` 进行 **HMAC-SHA256 + ±60s 时间戳 + 防重放 Nonce** 签名鉴权。
- 发起连接时，控制端通过 `AutoPairOrchestrator` 自动拉起配对并向被控端 `POST /agent/v1/pair` 代填 PIN，被控端验证签名（及可选的「独立安全密码」）后在本地静默提交给 Sunshine `POST https://127.0.0.1:47990/api/pin`，实现全自动无感配对。

---

## 里程碑 2：原生内嵌高刷控屏核心 + UU 远程风格操控 UI（已完成）

- **内置原生控屏核心（`RemoteDesk.StreamCore.dll`）**：以 `moonlight-common-c` + Windows CNG（`bcrypt`）+ Opus + Media Foundation D3D11VA 硬解 + DXGI Flip-Discard SwapChain 与 WASAPI 音频在主软件内完成零依赖高刷音视频渲染，彻底消除外部 `Moonlight.exe` 弹窗与“启动器感”。
- **UU 远程风格交互**：
  - 默认以独立可缩放窗口（`1280×760`）启动，`F11` 一键切换无边框全屏并启用顶部自动隐藏控制栏；
  - 办公自由指针 / 游戏相对锁定双鼠标模式一键切换（`Ctrl+Alt` 释放），内置系统热键菜单、静音、实时 HUD 与会话内在线画质热切重连。

---

## 里程碑 3：办公/隐私进阶能力 + 精简去 RustDesk + GPL-3.0 开源（已完成）

- **双向剪贴板同步与文件快传**：基于同账号 EasyTier 内网、HMAC-SHA256 签名通道及桌面交互会话桥接（`RemoteDeskSessionBridgePipe`），实现文本/PNG 截图双向自动同步及拖拽/按钮单文件流式发送（带 SHA-256 完整性校验与防覆盖重命名）。
- **自动分辨率匹配与断开还原**：发起连接前按受控端显示器支持列表自动选择最匹配的分辨率/刷新率，断开连接或停止守护服务时自动恢复原桌面设置。
- **隐私防窥与电源控制**：被控端通过 `WDA_EXCLUDEFROMCAPTURE` 实现物理显示器熄黑防窥（被控端本地按 `Ctrl+Alt+End` 可紧急解除），并支持远程锁屏、二次确认重启与关机。
- **架构收敛与协议升级**：彻底移除 `RustDeskClient`、外部 `MoonlightClient`、`ConnectionMode` 双模式割裂与 RustDesk 设备 ID 体系，保留 Sunshine 与 EasyTier 两大外部托管组件，并将仓库开源协议升级为 **GPL-3.0**。
