# RemoteDesk 阿里云自建节点部署与 300 元券选型指南

本指南面向持有 **300 元阿里云高校学生 / 个人代金券** 的用户，帮助你用最低成本搭建一套属于自己的 **EasyTier P2P 引导与私有中继节点**（可选同步部署 **RustDesk 自建中继 `hbbs/hbbr`**）。

部署完成后：
- **同校园网 / 同局域网 / NAT 可打洞时**：设备间直接建立 **P2P 加密直连**，流量不经阿里云，支持 Moonlight 1080p120 / 2K60 高刷串流，几乎零流量费；
- **跨网严格对称 NAT 无法打洞时**：自动回退到你的阿里云高带宽节点进行私有中继兜底，且通过白名单机制彻底杜绝陌生人蹭带宽。

---

## 一、300 元阿里云券选型与防坑指南

### 1. 两种最优规格对比（2C2G 足矣）

EasyTier 仅运行在无虚拟网卡模式（`--no-tun`），内存占用通常只有 **20~50 MB**；即使加上可选的 RustDesk（`hbbs` + `hbbr`），总内存也在 **200 MB** 以内。**CPU 与内存选 2C2G 完全绰绰有余，真正决定体验的是「公网峰值带宽」与「地域延迟」。**

| 对比维度 | 方案 A：轻量应用服务器（2C2G 高峰值带宽） | 方案 B：ECS 经济型 e（2C2G + 按使用流量计费） |
| --- | --- | --- |
| **典型网络配置** | 峰值带宽高（常见 200Mbps 峰值，按套餐含大额月流量包） | 公网带宽计费选 **按使用流量**，**带宽峰值直接拉到 50~100Mbps** |
| **适合人群** | 追求省心、不担心偶发大流量中继、想一站式包月/包年 | 经常 P2P 直连成功，想把 300 元券摊成超长续航（按实际流出流量计费，约 ¥0.8/GB） |
| **偶发中继画质** | 高峰值带宽，中继办公与 1080p 串流都很流畅 | 50~100Mbps 峰值足以支撑高清办公与临时串流 |
| **运维便利度** | 控制台自带直观防火墙面板，新手最友好 | 安全组规则更精细，支持弹性切换配置 |
| **系统镜像建议** | **Ubuntu 22.04 / 24.04 LTS** 或 **Alibaba Cloud Linux 3** | **Ubuntu 22.04 / 24.04 LTS** 或 **Alibaba Cloud Linux 3** |

> **一句话选型建议**：
> - 如果 300 元券支持且当前有 **2C2G 高峰值带宽轻量应用服务器**，直接选它，省心不怕超流量；
> - 如果券更适合买 **ECS 经济型 e（2C2G）**，公网计费务必选 **「按使用流量」** 并把 **「峰值带宽」拉高到 50~100Mbps**。

### 2. 为什么 P2P 打洞成功后几乎零流量费？

很多同学担心「按流量计费 ¥0.8/GB 会不会看一小时屏幕就把券烧光」。实际情况是：
1. **EasyTier 是去中心化 P2P 组网**：阿里云节点只负责「让两台电脑互相发现」与「辅助 NAT 打洞握手」，这一步只消耗几 KB 的信令流量。
2. **打洞成功后流量完全不经过阿里云**：画面、声音和键鼠数据直接在你的电脑与宿舍/家里电脑之间加密直传（在 RemoteDesk 中显示为绿色 **直连 (P2P)**）。正常每天远程使用，阿里云侧月流量往往不足几百 MB，费用几乎为零。
3. **只有无法打洞时才走中继**：当遇到极严格对称型 NAT 时，才会回退到阿里云中继（橙色 **中继 (Relay)**）。此时你的 50~200Mbps 高峰值带宽能让远程桌面依然顺畅。

### 3. 地域选择与关键防坑清单

- **选离学校和家里最近的国内地域**：例如华东优先上海/杭州、华北优先北京/张家口、华南优先广州/深圳、西南优先成都。距离越近，握手越快，万一走中继延迟也越低（通常 15~35ms）。
- **严禁选择 1~3Mbps 固定小水管！**
  - 1~3Mbps 固定带宽看似「不限流量」，但一旦跨网走中继，实际传输速度上限只有 `128~375 KB/s`：RustDesk 办公拖动窗口都会严重掉帧糊屏，Moonlight 串流（1080p60 最低建议 12Mbps、高刷 35~45Mbps）更是完全不可用。
- **结算页防坑**：
  - 确认勾选了 **300 元代金券**，留意代金券有效期与可抵扣产品范围（ECS / 轻量应用服务器）；
  - 关闭不必要的「自动续费」，避免券用完后扣余额；
  - 若使用 ECS 按流量计费，强烈建议在阿里云控制台「费用与成本 -> 预算管理」创建一个 **月度 ¥10~¥20 流量告警**，用得更踏实。

---

## 二、安全组 / 防火墙放行端口表

请在阿里云控制台的 **安全组（ECS）** 或 **防火墙（轻量应用服务器）** 中添加以下 **入方向（Ingress）** 放行规则：

| 服务模块 | 端口 | 协议 | 是否必开 | 用途说明 |
| --- | --- | --- | --- | --- |
| **EasyTier 主节点** | `11010` | **TCP + UDP** | **必开** | P2P 节点发现、NAT 打洞辅助与私有中继主端口 |
| **EasyTier 备用 WS** | `11011` | **TCP** | **强烈建议** | WebSocket 通道（`ws://`），校园网封锁 UDP/常规端口时一键切换 |
| RustDesk `hbbs` | `21115` | TCP | 可选（仅开启 RustDesk 时） | NAT 类型探测 |
| RustDesk `hbbs` | `21116` | **TCP + UDP** | 可选（仅开启 RustDesk 时） | ID 注册、心跳与打洞服务（**务必同时放行 TCP 和 UDP**） |
| RustDesk `hbbr` | `21117` | TCP | 可选（仅开启 RustDesk 时） | RustDesk 加密中继转发端口 |
| RustDesk Web | `21118`、`21119` | TCP | 可选（仅开启 RustDesk 时） | RustDesk Web 客户端支持（纯桌面端可不放行） |

> **系统内部防火墙提醒**：阿里云默认镜像通常未启用系统防火墙；如果你自行启用了 Ubuntu `ufw` 或 Alibaba Cloud Linux `firewalld`，也需要同步放行上述端口。例如 Ubuntu `ufw`：
> ```bash
> sudo ufw allow 11010/tcp
> sudo ufw allow 11010/udp
> sudo ufw allow 11011/tcp
> ```

---

## 三、3 分钟一键开服

### 第 1 步：SSH 登录服务器并获取部署脚本

通过终端 SSH 或阿里云控制台网页版 **Workbench** 登录服务器，克隆仓库（或将本仓库的 `server/` 目录上传到服务器）：

```bash
git clone https://github.com/zhengbaiyang/remote-desk.git
cd remote-desk
```

### 第 2 步：执行一键部署脚本

**模式 A：仅部署 EasyTier（推荐，最轻量省内存）**

```bash
bash server/deploy.sh
```

**模式 B：同时部署 EasyTier + RustDesk 自建中继（双引擎全自建）**

```bash
bash server/deploy.sh --with-rustdesk
```

脚本会自动完成以下工作：
1. 检查本机是否已安装 `docker` 与 `docker compose`（未安装时会直接给出适配 Ubuntu 与 Alibaba Cloud Linux 的安装命令）；
2. 首次运行时自动从 `server/.env.example` 生成 `server/.env`（权限自动设为 `600`），并使用 `openssl` 自动生成 32 位高强度随机 `EASYTIER_NETWORK_SECRET`；
3. 自动探测当前阿里云公网 IPv4 并让你回车确认（也支持手动输入已解析的域名）；
4. 拉取镜像并启动容器，同时自动注入 `--relay-network-whitelist`（EasyTier）与 `-k _`（RustDesk）防盗刷参数；
5. 部署完成后，在终端直接打印**开服结果卡片**（含安全组端口、RemoteDesk 可直接复制的三项参数，以及开启 RustDesk 时自动生成的 `id_ed25519.pub` 公钥）。

> **常用运维命令**（在仓库根目录执行）：
> ```bash
> # 查看容器运行状态
> docker compose -f server/docker-compose.yml ps
>
> # 查看实时日志
> docker compose -f server/docker-compose.yml logs -f --tail=50
>
> # 修改 server/.env 后平滑生效（不重复拉取镜像）
> bash server/deploy.sh --no-pull
>
> # 查看 RustDesk 公钥（仅开启 --with-rustdesk 时）
> cat server/data/rustdesk/id_ed25519.pub
> ```

---

## 四、在 RemoteDesk 客户端接入

在你的 **操作端电脑** 与 **目标端电脑**（如宿舍台式机、实验室工作站、家里电脑）上分别执行以下步骤：

1. **安装 EasyTier 引擎**
   - 打开 RemoteDesk，进入左侧 **「引擎与设置」**；
   - 找到 **EasyTier（跨网 P2P 组网）** 卡片，点击 **安装**，等待自动下载并解压完成（包含 `easytier-core.exe` 与 `easytier-cli.exe`）。

2. **填写阿里云节点信息**
   - 切换到左侧 **「网络节点」** 页面；
   - 将服务器部署脚本输出的卡片内容对应粘贴进去：
     - **节点地址**：`tcp://<你的阿里云公网IP>:11010`
     - **网络名称**：例如 `remotedesk-campus`
     - **网络密钥**：脚本自动生成的随机密钥（两端必须完全一致）
   - 虚拟 IP 保持默认的 **自动分配** 即可；如需开机/打开软件后自动连入私有组网，可勾选自动启动选项。

3. **启动组网并查看在线节点**
   - 点击 **「启动组网」**，等待 3~5 秒；
   - 在下方 **「在线节点列表」** 中即可看到所有已接入同一私有网络的设备及其虚拟内网 IP（例如 `10.126.126.2`）：
     - 🟢 **直连 (P2P)**：表示已打洞成功，流量点对点直连，延迟最低且几乎不耗阿里云流量；
     - 🟠 **中继 (Relay)**：表示当前网络 NAT 较严格，正通过你的阿里云节点加密中继转发。

4. **一键添加为设备并发起连接**
   - 在在线节点列表中点击 **一键添加为设备**，RemoteDesk 会自动把该设备的虚拟 IP 保存为目标主机地址；
   - 回到设备页即可按需选择模式发起连接：
     - **Moonlight 高刷串流**：目标端运行 Sunshine，即可通过虚拟 IP 享受 1080p120 / 2K60 硬件解码低延迟串流；
     - **RustDesk 办公模式**：通过 RustDesk ID（或虚拟 IP）稳定远程办公与文件传输。若同时部署了 `--with-rustdesk`，别忘了在两端 RustDesk 客户端的「设置 -> 网络」中填入你的服务器 IP 与脚本输出的 `Key (公钥)`。

---

## 五、未来跨平台扩展（Ubuntu / 手机 / 平板）

这套自建 EasyTier 节点并非 Windows 专属，未来只要使用**同一套节点地址、网络名称与网络密钥**，所有设备都会自动进入同一个私有虚拟局域网并获得 `10.x.x.x` 虚拟 IP：

- **Ubuntu / Linux 台式机与笔记本**
  - 下载 [EasyTier Linux 发行包](https://github.com/EasyTier/EasyTier/releases) 并启动核心进程（或注册为 systemd 服务）：
    ```bash
    sudo ./easytier-core -d \
      --network-name remotedesk-campus \
      --network-secret <你的网络密钥> \
      -p tcp://<你的阿里云公网IP>:11010
    ```
  - 接入后即可直接用 Linux 版 **Moonlight**（连向目标电脑虚拟 IP）或 **RustDesk** 远程办公，也可用 SSH 直接连接实验室 Ubuntu 主机。

- **Android / iOS 手机与平板**
  - 安装 EasyTier 官方移动端 App，新建网络并填入相同的 **节点地址 / 网络名称 / 网络密钥** 后开启组网开关；
  - 打开移动端 **Moonlight**（添加目标电脑的 `10.x.x.x` 虚拟 IP）或 **RustDesk** App，外出用 4G/5G 或咖啡厅 Wi-Fi 也能直连宿舍或家里电脑。

---

## 六、常见问题排查

### 1. 连接一直显示「中继 (Relay)」，无法变成「直连 (P2P)」怎么办？

这通常是因为通信双方至少有一端位于**严格对称型 NAT（Symmetric NAT）**环境（例如部分高校认证校园网、运营商 CGNAT 或手机热点）。可按顺序尝试：
1. **检查校园网 IPv6**：国内高校校园网普遍提供公网 IPv6 地址。确保 Windows 网卡的 IPv6 已启用，EasyTier 会优先尝试 IPv6 直连，成功率远高于 IPv4 NAT 打洞；
2. **检查本机 Windows 防火墙**：首次运行 `easytier-core.exe` 时请允许其访问专用与公用网络，不要拦截 UDP 出入站；
3. **路由器开启 UPnP / Full-Cone NAT**：家里路由器开启 UPnP 或 NAT1（全锥形 NAT）后，即使另一端在校园网对称 NAT 下也通常能打洞成功；
4. 如果两端均处于严格对称 NAT 且无 IPv6，走阿里云 **50~200Mbps 中继** 属于正常兜底行为，依然可以流畅办公。

### 2. 校园网封锁 UDP 或 `11010` 端口，连不上阿里云节点怎么办？

1. 直接将 RemoteDesk **「网络节点」** 中的节点地址从：
   `tcp://<你的阿里云公网IP>:11010`
   切换为 WebSocket 备用通道：
   `ws://<你的阿里云公网IP>:11011`
2. 如果所在网络只允许常见 Web 端口出站，可编辑服务器上的 `server/.env`，将 `EASYTIER_WS_PORT` 修改为 `443` 或 `8443`（需同步在阿里云安全组放行该 TCP 端口，并确认无其他程序占用），然后重新执行 `bash server/deploy.sh --no-pull` 即可。

### 3. 公网开放端口会被扫描器蹭流量 / 盗刷阿里云费用吗？

本部署套件默认内置了双层防盗刷保护，开箱即生效：
- **EasyTier 白名单隔离**：`server/docker-compose.yml` 强制启用了 `--relay-network-whitelist ${EASYTIER_NETWORK_NAME}`，阿里云节点**只允许为你的私有网络名称提供中继转发**；叠加高强度随机 `EASYTIER_NETWORK_SECRET` 端到端加密认证，公网扫描器既无法加入你的组网，也无法借你的节点中继流量。
- **RustDesk 强制公钥鉴权**：启用 `--with-rustdesk` 时，`hbbs` 与 `hbbr` 均带有 `-k _` 参数，强制验证服务器自动生成的 Ed25519 公钥（`server/data/rustdesk/id_ed25519.pub`），未配置正确公钥的任何陌生客户端都会被立即拒绝。

### 4. 国内阿里云服务器执行 `deploy.sh` 时 `docker compose pull` 超时怎么办？

国内部分地域直连 Docker Hub 可能出现拉取缓慢或超时，可为 Docker 配置国内镜像加速源后重试：

```bash
sudo mkdir -p /etc/docker
sudo tee /etc/docker/daemon.json <<'EOF'
{
  "registry-mirrors": [
    "https://docker.1ms.run",
    "https://docker.xuanyuan.me"
  ]
}
EOF
sudo systemctl daemon-reload
sudo systemctl restart docker
bash server/deploy.sh
```

> 也可登录阿里云控制台「容器镜像服务 ACR -> 镜像工具 -> 镜像加速器」获取你账号专属的阿里云加速地址填入 `registry-mirrors`。

### 5. 开启 `--with-rustdesk` 后 RustDesk 客户端提示连不上或 Key 错误？

1. 检查阿里云安全组是否已同时放行 `21116` 的 **TCP 和 UDP**（仅放行 TCP 会导致心跳与打洞异常）；
2. 检查 `server/.env` 中的 `PUBLIC_HOST` 是否填入了阿里云**公网 IP**（切勿填 `172.x.x.x` 这类 ECS 内网 IP）；
3. 重新核对公钥内容（避免末尾多复制换行或空格）：
   ```bash
   cat server/data/rustdesk/id_ed25519.pub
   ```
