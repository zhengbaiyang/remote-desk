#!/usr/bin/env bash
set -euo pipefail

# RemoteDesk 阿里云节点一键部署脚本（适配 Ubuntu / Alibaba Cloud Linux）
# 用法：
#   bash server/deploy.sh                  # 启动 EasyTier + 账号同步服务 (推荐)
#   bash server/deploy.sh --with-rustdesk  # 同时启动 EasyTier + 同步服务 + RustDesk (hbbs/hbbr)
#   bash server/deploy.sh --no-pull        # 跳过镜像拉取，直接构建并启动

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENV_EXAMPLE="$SCRIPT_DIR/.env.example"
ENV_FILE="$SCRIPT_DIR/.env"
COMPOSE_FILE="$SCRIPT_DIR/docker-compose.yml"
SYNC_DATA_DIR="$SCRIPT_DIR/data/sync"
SYNC_TLS_CERT="$SYNC_DATA_DIR/tls.crt"
SYNC_TLS_KEY="$SYNC_DATA_DIR/tls.key"
RUSTDESK_DATA_DIR="$SCRIPT_DIR/data/rustdesk"
RUSTDESK_PUB_KEY_FILE="$RUSTDESK_DATA_DIR/id_ed25519.pub"

CLI_WITH_RUSTDESK=false
SKIP_PULL=false

usage() {
  cat <<'EOF'
RemoteDesk 阿里云节点一键部署脚本

用法：
  bash server/deploy.sh [选项]

选项：
  --with-rustdesk   同时启用可选的 RustDesk 自建服务 (hbbs + hbbr)
  --no-pull         跳过 docker compose pull（仅重新应用配置并启动）
  -h, --help        显示此帮助信息
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --with-rustdesk)
      CLI_WITH_RUSTDESK=true
      shift
      ;;
    --no-pull)
      SKIP_PULL=true
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "[错误] 未知参数：$1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

log_info() {
  printf '[信息] %s\n' "$1"
}

log_warn() {
  printf '[提示] %s\n' "$1" >&2
}

log_error() {
  printf '[错误] %s\n' "$1" >&2
}

read_env_var() {
  local key="$1"
  local file="$2"
  if [[ ! -f "$file" ]]; then
    return 0
  fi
  awk -F= -v k="$key" '
    $0 ~ "^[[:space:]]*" k "=" {
      val = substr($0, index($0, "=") + 1)
      sub(/\r$/, "", val)
      gsub(/^[[:space:]]+|[[:space:]]+$/, "", val)
      if (length(val) >= 2 &&
          ((substr(val, 1, 1) == "\"" && substr(val, length(val), 1) == "\"") ||
           (substr(val, 1, 1) == "\047" && substr(val, length(val), 1) == "\047"))) {
        val = substr(val, 2, length(val) - 2)
      }
      result = val
    }
    END { print result }
  ' "$file"
}

set_env_var() {
  local key="$1"
  local value="$2"
  local file="$3"
  local tmp_file
  tmp_file="$(mktemp "${file}.XXXXXX")"
  ENV_KEY="$key" ENV_VAL="$value" awk '
    BEGIN {
      k = ENVIRON["ENV_KEY"]
      v = ENVIRON["ENV_VAL"]
      updated = 0
    }
    $0 ~ "^[[:space:]]*" k "=" {
      print k "=" v
      updated = 1
      next
    }
    { print }
    END {
      if (!updated) {
        print k "=" v
      }
    }
  ' "$file" > "$tmp_file"
  mv "$tmp_file" "$file"
}

generate_secret() {
  local bytes="${1:-16}"
  if command -v openssl >/dev/null 2>&1; then
    openssl rand -hex "$bytes"
    return 0
  fi
  if [[ -r /dev/urandom ]]; then
    head -c "$bytes" /dev/urandom | od -An -tx1 | tr -d ' \n'
    printf '\n'
    return 0
  fi
  log_error "无法生成随机密钥：系统缺少 openssl 且 /dev/urandom 不可读。"
  exit 1
}

generate_invite_code() {
  local raw
  raw="$(generate_secret 4 | tr '[:lower:]' '[:upper:]')"
  printf 'RD-%s-%s\n' "${raw:0:4}" "${raw:4:4}"
}

is_valid_ipv4() {
  local ip="$1"
  if [[ ! "$ip" =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]]; then
    return 1
  fi
  local -a octets=()
  IFS='.' read -r -a octets <<< "$ip"
  local o
  for o in "${octets[@]}"; do
    if (( 10#$o > 255 )); then
      return 1
    fi
  done
  return 0
}

detect_public_ipv4() {
  local candidate=""
  local endpoints=(
    "https://4.ipw.cn"
    "https://ip.3322.net"
    "https://ifconfig.me/ip"
    "https://api.ipify.org"
  )
  local url
  for url in "${endpoints[@]}"; do
    if command -v curl >/dev/null 2>&1; then
      candidate="$(curl -4 -fsS --max-time 4 "$url" 2>/dev/null | tr -d '[:space:]' || true)"
    elif command -v wget >/dev/null 2>&1; then
      candidate="$(wget -4 -qO- --timeout=4 "$url" 2>/dev/null | tr -d '[:space:]' || true)"
    else
      break
    fi
    if is_valid_ipv4 "$candidate"; then
      printf '%s\n' "$candidate"
      return 0
    fi
  done
  return 1
}

check_docker() {
  if ! command -v docker >/dev/null 2>&1; then
    log_error "未检测到 docker，请先安装 Docker Engine 后再运行本脚本。"
    cat <<'EOF' >&2

参考安装命令：
  # Ubuntu / Debian（使用阿里云镜像源加速）：
  curl -fsSL https://get.docker.com | sudo sh -s -- --mirror Aliyun
  sudo systemctl enable --now docker

  # Alibaba Cloud Linux 3：
  sudo dnf -y install dnf-plugins-core
  sudo dnf config-manager --add-repo https://mirrors.aliyun.com/docker-ce/linux/centos/docker-ce.repo
  sudo dnf -y install docker-ce docker-ce-cli containerd.io docker-compose-plugin
  sudo systemctl enable --now docker
EOF
    exit 1
  fi

  if docker compose version >/dev/null 2>&1; then
    COMPOSE_CMD=(docker compose)
  elif command -v docker-compose >/dev/null 2>&1; then
    COMPOSE_CMD=(docker-compose)
  else
    log_error "未检测到 docker compose 插件，请先安装 docker-compose-plugin。"
    cat <<'EOF' >&2

参考安装命令：
  # Ubuntu / Debian：
  sudo apt-get update && sudo apt-get install -y docker-compose-plugin

  # Alibaba Cloud Linux 3：
  sudo dnf -y install docker-compose-plugin
EOF
    exit 1
  fi

  if ! docker info >/dev/null 2>&1; then
    log_error "无法连接到 Docker 守护进程。请确认 Docker 已启动，或使用 sudo 运行本脚本。"
    echo "  提示：sudo systemctl enable --now docker" >&2
    echo "  提示：sudo bash server/deploy.sh" >&2
    exit 1
  fi
}

prepare_env() {
  if [[ ! -f "$ENV_EXAMPLE" ]]; then
    log_error "缺少模板文件：$ENV_EXAMPLE"
    exit 1
  fi

  local is_fresh=false
  if [[ ! -f "$ENV_FILE" ]]; then
    is_fresh=true
    log_info "未发现 $ENV_FILE，正在从模板初始化配置..."
    cp "$ENV_EXAMPLE" "$ENV_FILE"

    local prefix secret invite
    prefix="rd-$(generate_secret 6)"
    secret="$(generate_secret 16)"
    invite="$(generate_invite_code)"
    set_env_var "EASYTIER_NETWORK_PREFIX" "$prefix" "$ENV_FILE"
    set_env_var "EASYTIER_NETWORK_NAME" "${prefix}-main" "$ENV_FILE"
    set_env_var "EASYTIER_NETWORK_SECRET" "$secret" "$ENV_FILE"
    set_env_var "SYNC_INVITE_CODE" "$invite" "$ENV_FILE"
    log_info "已自动生成随机账号网络前缀、高强度网络密钥与注册邀请码。"

    local detected_ip=""
    detected_ip="$(detect_public_ipv4 || true)"

    if [[ -n "$detected_ip" ]]; then
      log_info "自动探测到当前服务器公网 IPv4：$detected_ip"
      if [[ -t 0 ]]; then
        local input_host=""
        read -r -p "请确认公网 IP 或域名（直接回车使用 ${detected_ip}）: " input_host || true
        input_host="$(printf '%s' "$input_host" | tr -d '[:space:]')"
        if [[ -n "$input_host" ]]; then
          set_env_var "PUBLIC_HOST" "$input_host" "$ENV_FILE"
        else
          set_env_var "PUBLIC_HOST" "$detected_ip" "$ENV_FILE"
        fi
      else
        set_env_var "PUBLIC_HOST" "$detected_ip" "$ENV_FILE"
      fi
    else
      log_warn "未能自动探测公网 IPv4。"
      if [[ -t 0 ]]; then
        local input_host=""
        read -r -p "请输入阿里云服务器公网 IP 或域名（留空稍后手动修改 server/.env）: " input_host || true
        input_host="$(printf '%s' "$input_host" | tr -d '[:space:]')"
        if [[ -n "$input_host" ]]; then
          set_env_var "PUBLIC_HOST" "$input_host" "$ENV_FILE"
        fi
      fi
    fi
  else
    log_info "已检测到现有配置文件：$ENV_FILE"
    local existing_secret existing_prefix existing_invite existing_sync_port
    existing_secret="$(read_env_var "EASYTIER_NETWORK_SECRET" "$ENV_FILE")"
    existing_prefix="$(read_env_var "EASYTIER_NETWORK_PREFIX" "$ENV_FILE")"
    existing_invite="$(read_env_var "SYNC_INVITE_CODE" "$ENV_FILE")"
    existing_sync_port="$(read_env_var "SYNC_PORT" "$ENV_FILE")"

    if [[ -z "$existing_secret" || "$existing_secret" == "change-me-to-a-strong-secret" ]]; then
      set_env_var "EASYTIER_NETWORK_SECRET" "$(generate_secret 16)" "$ENV_FILE"
      log_info "检测到默认占位密钥，已自动替换为高强度随机密钥。"
    fi

    if [[ -z "$existing_prefix" || "$existing_prefix" == "remotedesk" ]]; then
      local existing_net_name
      existing_net_name="$(read_env_var "EASYTIER_NETWORK_NAME" "$ENV_FILE")"
      if [[ -n "$existing_net_name" && "$existing_net_name" != "remotedesk-campus" ]]; then
        set_env_var "EASYTIER_NETWORK_PREFIX" "$existing_net_name" "$ENV_FILE"
      else
        local generated_prefix="rd-$(generate_secret 6)"
        set_env_var "EASYTIER_NETWORK_PREFIX" "$generated_prefix" "$ENV_FILE"
        if [[ -z "$existing_net_name" || "$existing_net_name" == "remotedesk-campus" ]]; then
          set_env_var "EASYTIER_NETWORK_NAME" "${generated_prefix}-main" "$ENV_FILE"
        fi
      fi
    fi

    if [[ -z "$existing_invite" || "$existing_invite" == "change-me-invite-code" ]]; then
      set_env_var "SYNC_INVITE_CODE" "$(generate_invite_code)" "$ENV_FILE"
      log_info "已为同步服务自动生成注册邀请码。"
    fi

    if [[ -z "$existing_sync_port" ]]; then
      set_env_var "SYNC_PORT" "11020" "$ENV_FILE"
    fi
  fi

  if [[ "$CLI_WITH_RUSTDESK" == true ]]; then
    set_env_var "ENABLE_RUSTDESK" "true" "$ENV_FILE"
  fi

  chmod 600 "$ENV_FILE" 2>/dev/null || true
}

ensure_sync_tls_cert() {
  local public_host="$1"
  mkdir -p "$SYNC_DATA_DIR"
  chmod 755 "$SYNC_DATA_DIR" 2>/dev/null || true

  local tls_ok=false
  if [[ -s "$SYNC_TLS_CERT" && -s "$SYNC_TLS_KEY" ]]; then
    tls_ok=true
  elif ! command -v openssl >/dev/null 2>&1; then
    log_warn "系统未安装 openssl，同步服务将以 HTTP 模式启动（建议安装 openssl 后重建证书）。"
  else
    local san_entry="DNS:localhost"
    if [[ -n "$public_host" && "$public_host" != "your-aliyun-public-ip-or-domain" ]]; then
      if is_valid_ipv4 "$public_host"; then
        san_entry="IP:${public_host},DNS:localhost"
      else
        san_entry="DNS:${public_host},DNS:localhost"
      fi
    fi

    log_info "正在为同步服务生成自签名 TLS 证书（SAN=${san_entry}）..."
    openssl req -x509 -newkey rsa:2048 -sha256 -days 3650 -nodes \
      -keyout "$SYNC_TLS_KEY" \
      -out "$SYNC_TLS_CERT" \
      -subj "/CN=RemoteDesk-Sync" \
      -addext "subjectAltName=${san_entry}" >/dev/null 2>&1
    tls_ok=true
  fi

  if [[ -f "$SYNC_TLS_KEY" ]]; then
    chmod 600 "$SYNC_TLS_KEY" 2>/dev/null || true
  fi
  if [[ -f "$SYNC_TLS_CERT" ]]; then
    chmod 644 "$SYNC_TLS_CERT" 2>/dev/null || true
  fi
  if ! chown -R 10001:10001 "$SYNC_DATA_DIR" 2>/dev/null; then
    chmod 777 "$SYNC_DATA_DIR" 2>/dev/null || true
    chmod 644 "$SYNC_TLS_KEY" 2>/dev/null || true
  fi

  [[ "$tls_ok" == true ]]
}

get_cert_fingerprint() {
  if [[ -s "$SYNC_TLS_CERT" ]] && command -v openssl >/dev/null 2>&1; then
    openssl x509 -in "$SYNC_TLS_CERT" -noout -fingerprint -sha256 2>/dev/null \
      | sed 's/.*=//; s/://g' \
      | tr '[:lower:]' '[:upper:]' || true
  fi
}

wait_for_rustdesk_key() {
  local tries=15
  local i
  for ((i = 1; i <= tries; i++)); do
    if [[ -s "$RUSTDESK_PUB_KEY_FILE" ]]; then
      return 0
    fi
    sleep 1
  done
  return 1
}

main() {
  if [[ ! -f "$COMPOSE_FILE" ]]; then
    log_error "缺少编排文件：$COMPOSE_FILE"
    exit 1
  fi

  check_docker
  prepare_env

  local public_host network_prefix network_name network_secret easytier_port easytier_ws_port sync_port invite_code enable_rustdesk rustdesk_relay_host
  public_host="$(read_env_var "PUBLIC_HOST" "$ENV_FILE")"
  network_prefix="$(read_env_var "EASYTIER_NETWORK_PREFIX" "$ENV_FILE")"
  network_name="$(read_env_var "EASYTIER_NETWORK_NAME" "$ENV_FILE")"
  network_secret="$(read_env_var "EASYTIER_NETWORK_SECRET" "$ENV_FILE")"
  easytier_port="$(read_env_var "EASYTIER_PORT" "$ENV_FILE")"
  easytier_ws_port="$(read_env_var "EASYTIER_WS_PORT" "$ENV_FILE")"
  sync_port="$(read_env_var "SYNC_PORT" "$ENV_FILE")"
  invite_code="$(read_env_var "SYNC_INVITE_CODE" "$ENV_FILE")"
  enable_rustdesk="$(read_env_var "ENABLE_RUSTDESK" "$ENV_FILE")"
  rustdesk_relay_host="$(read_env_var "RUSTDESK_RELAY_HOST" "$ENV_FILE")"

  easytier_port="${easytier_port:-11010}"
  easytier_ws_port="${easytier_ws_port:-11011}"
  sync_port="${sync_port:-11020}"
  network_prefix="${network_prefix:-remotedesk}"

  local rustdesk_enabled=false
  local enable_rustdesk_lc
  enable_rustdesk_lc="$(printf '%s' "$enable_rustdesk" | tr '[:upper:]' '[:lower:]')"
  case "$enable_rustdesk_lc" in
    true|1|yes)
      rustdesk_enabled=true
      ;;
  esac

  if [[ -z "$rustdesk_relay_host" || "$rustdesk_relay_host" == '${PUBLIC_HOST}' ]]; then
    rustdesk_relay_host="$public_host"
  fi

  if [[ -z "$network_name" || -z "$network_secret" ]]; then
    log_error "EASYTIER_NETWORK_NAME 或 EASYTIER_NETWORK_SECRET 不能为空，请检查 $ENV_FILE。"
    exit 1
  fi

  if [[ "$rustdesk_enabled" == true && ( -z "$rustdesk_relay_host" || "$rustdesk_relay_host" == "your-aliyun-public-ip-or-domain" ) ]]; then
    log_error "启用 RustDesk 时必须在 $ENV_FILE 中配置真实的 PUBLIC_HOST（公网 IP 或域名）。"
    exit 1
  fi

  local tls_ready=false
  if ensure_sync_tls_cert "$public_host"; then
    tls_ready=true
  fi

  local -a compose_args=(-f "$COMPOSE_FILE" --env-file "$ENV_FILE")
  if [[ "$rustdesk_enabled" == true ]]; then
    mkdir -p "$RUSTDESK_DATA_DIR"
    compose_args+=(--profile rustdesk)
    log_info "部署模式：EasyTier + 账号同步服务 + RustDesk (hbbs/hbbr)"
  else
    log_info "部署模式：EasyTier P2P 中继 + 账号与设备云同步服务（推荐）"
  fi

  if [[ "$SKIP_PULL" == false ]]; then
    log_info "正在拉取最新基础镜像..."
    if [[ "${COMPOSE_CMD[0]}" == "docker" ]]; then
      "${COMPOSE_CMD[@]}" "${compose_args[@]}" pull --ignore-buildable
    else
      "${COMPOSE_CMD[@]}" "${compose_args[@]}" pull --ignore-pull-failures || true
    fi
  else
    log_info "已跳过镜像拉取 (--no-pull)。"
  fi

  log_info "正在构建并启动服务..."
  "${COMPOSE_CMD[@]}" "${compose_args[@]}" up -d --build --remove-orphans

  local rustdesk_pub_key=""
  if [[ "$rustdesk_enabled" == true ]]; then
    log_info "等待 RustDesk 生成加密公钥..."
    if wait_for_rustdesk_key; then
      rustdesk_pub_key="$(tr -d '\r\n' < "$RUSTDESK_PUB_KEY_FILE")"
    else
      log_warn "暂未读取到 $RUSTDESK_PUB_KEY_FILE，可稍后执行：cat $RUSTDESK_PUB_KEY_FILE"
    fi
  fi

  local display_host="${public_host:-<你的阿里云公网IP>}"
  local host_warning=""
  if [[ "$display_host" == "your-aliyun-public-ip-or-domain" ]]; then
    display_host="<你的阿里云公网IP>"
    host_warning="注意：server/.env 中的 PUBLIC_HOST 仍为占位值，请替换为真实公网 IP。"
  fi

  local sync_scheme="http"
  local cert_fp=""
  if [[ "$tls_ready" == true && -s "$SYNC_TLS_CERT" ]]; then
    sync_scheme="https"
    cert_fp="$(get_cert_fingerprint)"
  fi

  printf '\n'
  printf '======================================================================\n'
  printf '  RemoteDesk 阿里云自建节点部署完成\n'
  printf '======================================================================\n'
  if [[ -n "$host_warning" ]]; then
    printf '  [!] %s\n\n' "$host_warning"
  fi

  printf '1) 阿里云安全组 / 防火墙必须放行的入方向端口：\n'
  printf '   - 账号同步服务   ：TCP %s（HTTPS）\n' "$sync_port"
  printf '   - EasyTier 主端口 ：TCP %s、UDP %s\n' "$easytier_port" "$easytier_port"
  printf '   - EasyTier 备用 WS：TCP %s（校园网封锁 UDP/常规端口时使用）\n' "$easytier_ws_port"
  if [[ "$rustdesk_enabled" == true ]]; then
    printf '   - RustDesk hbbs   ：TCP 21115、TCP 21116、UDP 21116、TCP 21118\n'
    printf '   - RustDesk hbbr   ：TCP 21117、TCP 21119\n'
  fi

  printf '\n2) 在 RemoteDesk 客户端 -> 「网络节点 / 账号同步」登录即可自动组网：\n'
  printf '   - 同步服务器地址 ：%s://%s:%s\n' "$sync_scheme" "$display_host" "$sync_port"
  printf '   - 注册邀请码     ：%s\n' "$invite_code"
  if [[ -n "$cert_fp" ]]; then
    printf '   - TLS 证书指纹   ：%s\n' "$cert_fp"
  fi
  printf '   - 白名单隔离前缀 ：%s-*（仅放行本服注册账号的私有网络）\n' "$network_prefix"

  printf '\n3) 高级（无账号手动组网备用参数）：\n'
  printf '   - 节点地址：%s\n' "tcp://${display_host}:${easytier_port}"
  printf '   - 备用地址：%s\n' "ws://${display_host}:${easytier_ws_port}"
  printf '   - 网络名称：%s\n' "$network_name"
  printf '   - 网络密钥：%s\n' "$network_secret"

  if [[ "$rustdesk_enabled" == true ]]; then
    printf '\n4) 可选 RustDesk 客户端参数（「设置 -> 网络」）：\n'
    printf '   - ID / 中继服务器：%s\n' "$rustdesk_relay_host"
    if [[ -n "$rustdesk_pub_key" ]]; then
      printf '   - Key (公钥)     ：%s\n' "$rustdesk_pub_key"
    else
      printf '   - Key (公钥)     ：请稍后执行 cat %s 查看\n' "$RUSTDESK_PUB_KEY_FILE"
    fi
  fi

  local profile_hint=""
  if [[ "$rustdesk_enabled" == true ]]; then
    profile_hint=" --profile rustdesk"
  fi

  printf '\n常用运维命令：\n'
  printf '   - 查看状态：%s -f %s%s ps\n' "${COMPOSE_CMD[*]}" "$COMPOSE_FILE" "$profile_hint"
  printf '   - 查看日志：%s -f %s%s logs -f --tail=50\n' "${COMPOSE_CMD[*]}" "$COMPOSE_FILE" "$profile_hint"
  printf '   - 停止服务：%s -f %s%s down\n' "${COMPOSE_CMD[*]}" "$COMPOSE_FILE" "$profile_hint"
  printf '======================================================================\n'
}

main "$@"
