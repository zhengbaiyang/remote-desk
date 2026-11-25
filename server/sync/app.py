"""RemoteDesk lightweight account & device sync HTTP/TLS server (stdlib-only)."""

from __future__ import annotations

import json
import os
import signal
import ssl
import sys
import time
from dataclasses import dataclass
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse

from security import (
    RateLimiter,
    ValidationError,
    normalize_prefix,
    sanitize_optional_str,
    validate_device_key,
    validate_device_name,
    validate_password,
    validate_username,
)
from storage import StorageError, SyncDatabase, UserRecord

MAX_REQUEST_BODY_BYTES = 64 * 1024


@dataclass(frozen=True)
class SyncServerConfig:
    host: str
    port: int
    data_dir: str
    public_host: str
    easytier_port: int
    easytier_ws_port: int
    easytier_network_prefix: str
    invite_code: str
    tls_cert_path: str | None = None
    tls_key_path: str | None = None

    @classmethod
    def from_env(cls) -> "SyncServerConfig":
        port = _parse_port(os.environ.get("SYNC_PORT"), 11020)
        et_port = _parse_port(os.environ.get("EASYTIER_PORT"), 11010)
        et_ws_port = _parse_port(os.environ.get("EASYTIER_WS_PORT"), 11011)
        data_dir = os.environ.get("SYNC_DATA_DIR", "/data")
        public_host = (os.environ.get("PUBLIC_HOST") or "127.0.0.1").strip()
        prefix = normalize_prefix(os.environ.get("EASYTIER_NETWORK_PREFIX") or os.environ.get("EASYTIER_NETWORK_NAME"))
        invite = (os.environ.get("SYNC_INVITE_CODE") or "").strip()
        cert_path = (os.environ.get("SYNC_TLS_CERT") or "").strip() or None
        key_path = (os.environ.get("SYNC_TLS_KEY") or "").strip() or None
        return cls(
            host=os.environ.get("SYNC_HOST", "0.0.0.0"),
            port=port,
            data_dir=data_dir,
            public_host=public_host,
            easytier_port=et_port,
            easytier_ws_port=et_ws_port,
            easytier_network_prefix=prefix,
            invite_code=invite,
            tls_cert_path=cert_path,
            tls_key_path=key_path,
        )


def _parse_port(raw: str | None, fallback: int) -> int:
    try:
        val = int((raw or "").strip())
        return val if 1 <= val <= 65535 else fallback
    except ValueError:
        return fallback


def _build_mesh_config(cfg: SyncServerConfig, user: UserRecord) -> dict[str, str]:
    host = cfg.public_host or "127.0.0.1"
    return {
        "peerUrl": f"tcp://{host}:{cfg.easytier_port}",
        "fallbackWsUrl": f"ws://{host}:{cfg.easytier_ws_port}",
        "networkName": user.easytier_network_name,
        "networkSecret": user.easytier_network_secret,
        "agentAuthKey": user.agent_auth_key,
        "subnetCidr": "10.126.126.0/24",
    }


class SyncHttpServer(ThreadingHTTPServer):
    allow_reuse_address = True
    daemon_threads = True

    def __init__(
        self,
        server_address: tuple[str, int],
        handler_cls: type[BaseHTTPRequestHandler],
        db: SyncDatabase,
        cfg: SyncServerConfig,
        rate_limiter: RateLimiter,
    ) -> None:
        self.db = db
        self.cfg = cfg
        self.rate_limiter = rate_limiter
        super().__init__(server_address, handler_cls)


class SyncRequestHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server: SyncHttpServer  # type: ignore[assignment]

    def log_message(self, format: str, *args: object) -> None:
        ts = time.strftime("%Y-%m-%d %H:%M:%S")
        sys.stdout.write(f"[{ts}] {self.address_string()} {format % args}\n")
        sys.stdout.flush()

    def do_GET(self) -> None:
        self._dispatch("GET")

    def do_POST(self) -> None:
        self._dispatch("POST")

    def do_PATCH(self) -> None:
        self._dispatch("PATCH")

    def do_DELETE(self) -> None:
        self._dispatch("DELETE")

    def _dispatch(self, method: str) -> None:
        path = urlparse(self.path).path
        try:
            if method == "GET" and path == "/api/v1/health":
                self._send_json(
                    HTTPStatus.OK,
                    {
                        "ok": True,
                        "version": "1.0.0",
                        "registrationMode": "invite",
                    },
                )
                return

            if method == "POST" and path == "/api/v1/auth/register":
                self._handle_register()
                return

            if method == "POST" and path == "/api/v1/auth/login":
                self._handle_login()
                return

            if method == "POST" and path == "/api/v1/auth/logout":
                self._handle_logout()
                return

            if method == "GET" and path == "/api/v1/account/profile":
                user = self._require_user()
                self._send_json(
                    HTTPStatus.OK,
                    {
                        "user": user.to_public_dict(),
                        "meshConfig": _build_mesh_config(self.server.cfg, user),
                    },
                )
                return

            if method == "POST" and path == "/api/v1/devices/checkin":
                user = self._require_user()
                self._handle_checkin(user)
                return

            if method == "POST" and path == "/api/v1/sessions/acquire":
                user = self._require_user()
                self._handle_session_acquire(user, is_heartbeat=False)
                return

            if method == "POST" and path == "/api/v1/sessions/heartbeat":
                user = self._require_user()
                self._handle_session_acquire(user, is_heartbeat=True)
                return

            if method == "POST" and path == "/api/v1/sessions/release":
                user = self._require_user()
                self._handle_session_release(user)
                return

            if method == "GET" and path == "/api/v1/devices":
                user = self._require_user()
                devices, now_ts = self.server.db.list_devices(user.id)
                self._send_json(HTTPStatus.OK, {"devices": devices, "serverTimeUtc": now_ts})
                return

            if path.startswith("/api/v1/devices/") and len(path) > len("/api/v1/devices/"):
                device_id = path[len("/api/v1/devices/") :].strip("/")
                if "/" in device_id or not device_id:
                    self._send_error_json(404, "NOT_FOUND", "接口不存在。")
                    return

                user = self._require_user()
                if method == "PATCH":
                    body = self._read_json_body()
                    new_name = validate_device_name(body.get("name"))
                    updated = self.server.db.rename_device(user.id, device_id, new_name)
                    self._send_json(HTTPStatus.OK, {"device": updated})
                    return
                if method == "DELETE":
                    self.server.db.delete_device(user.id, device_id)
                    self._send_json(HTTPStatus.OK, {"ok": True})
                    return

            self._send_error_json(404, "NOT_FOUND", "接口不存在。")
        except ValidationError as err:
            self._send_error_json(400, err.code, err.message)
        except StorageError as err:
            self._send_error_json(err.status_code, err.code, err.message)
        except Exception as err:  # pragma: no cover
            sys.stderr.write(f"[ERROR] {method} {path}: {err!r}\n")
            sys.stderr.flush()
            self._send_error_json(500, "INTERNAL_ERROR", "服务器内部处理异常。")

    def _client_ip(self) -> str:
        return self.client_address[0] if self.client_address else "unknown"

    def _handle_register(self) -> None:
        ip = self._client_ip()
        if self.server.rate_limiter.is_blocked(ip):
            self._send_error_json(429, "RATE_LIMITED", "尝试过于频繁，请 5 分钟后再试。")
            return

        body = self._read_json_body()
        username = validate_username(body.get("username"))
        password = validate_password(body.get("password"))
        invite_code = body.get("inviteCode")
        if not isinstance(invite_code, str):
            self.server.rate_limiter.record_failure(ip)
            raise StorageError(403, "INVITE_REQUIRED", "需要填写有效邀请码才能注册账号。")

        try:
            user, token = self.server.db.create_user_with_invite(
                username=username,
                password=password,
                invite_code=invite_code,
                network_prefix=self.server.cfg.easytier_network_prefix,
            )
        except StorageError as err:
            if err.status_code == 403:
                self.server.rate_limiter.record_failure(ip)
            raise

        self.server.rate_limiter.clear(ip)
        self._send_json(
            HTTPStatus.CREATED,
            {
                "token": token,
                "user": user.to_public_dict(),
                "meshConfig": _build_mesh_config(self.server.cfg, user),
            },
        )

    def _handle_login(self) -> None:
        ip = self._client_ip()
        if self.server.rate_limiter.is_blocked(ip):
            self._send_error_json(429, "RATE_LIMITED", "登录失败次数过多，请 5 分钟后再试。")
            return

        body = self._read_json_body()
        username = validate_username(body.get("username"))
        password = validate_password(body.get("password"))

        try:
            user, token = self.server.db.authenticate_user(username, password)
        except StorageError as err:
            if err.status_code == 401:
                self.server.rate_limiter.record_failure(ip)
            raise

        self.server.rate_limiter.clear(ip)
        self._send_json(
            HTTPStatus.OK,
            {
                "token": token,
                "user": user.to_public_dict(),
                "meshConfig": _build_mesh_config(self.server.cfg, user),
            },
        )

    def _handle_logout(self) -> None:
        raw_token = self._extract_bearer_token()
        if raw_token:
            self.server.db.delete_session(raw_token)
        self._send_json(HTTPStatus.OK, {"ok": True})

    def _handle_checkin(self, user: UserRecord) -> None:
        body = self._read_json_body()
        device_key = validate_device_key(body.get("deviceKey"))
        name = validate_device_name(body.get("name"))
        hostname = sanitize_optional_str(body.get("hostname"), 64) or name
        lan_ipv4 = sanitize_optional_str(body.get("lanIpv4"), 64)
        lan_ipv6 = sanitize_optional_str(body.get("lanIpv6"), 128)
        sunshine_ready = bool(body.get("sunshineReady", False))
        sunshine_port = _parse_port(str(body.get("sunshinePort", 47989)), 47989)
        agent_port = _parse_port(str(body.get("agentPort", 47999)), 47999)
        security_pin_required = bool(body.get("securityPinRequired", False))
        gpu_encoder = sanitize_optional_str(body.get("gpuEncoder"), 64)
        os_version = sanitize_optional_str(body.get("osVersion"), 64)

        device, devices, now_ts = self.server.db.upsert_device_checkin(
            user_id=user.id,
            device_key=device_key,
            name=name,
            hostname=hostname,
            lan_ipv4=lan_ipv4,
            lan_ipv6=lan_ipv6,
            sunshine_ready=sunshine_ready,
            sunshine_port=sunshine_port,
            agent_port=agent_port,
            security_pin_required=security_pin_required,
            gpu_encoder=gpu_encoder,
            os_version=os_version,
        )
        self._send_json(
            HTTPStatus.OK,
            {
                "device": device,
                "devices": devices,
                "serverTimeUtc": now_ts,
            },
        )

    def _handle_session_acquire(self, user: UserRecord, *, is_heartbeat: bool) -> None:
        body = self._read_json_body()
        session_id = sanitize_optional_str(body.get("sessionId"), 64)
        if not session_id:
            raise ValidationError("INVALID_SESSION_ID", "会话标识无效。")
        controller_key = validate_device_key(body.get("controllerDeviceKey"))
        target_key = validate_device_key(body.get("targetDeviceKey"))
        if controller_key == target_key:
            raise ValidationError("INVALID_SESSION_TARGET", "不能由本机远控本机。")

        if is_heartbeat:
            devices, now_ts = self.server.db.heartbeat_remote_session(
                user_id=user.id,
                session_id=session_id,
                controller_device_key=controller_key,
                target_device_key=target_key,
            )
        else:
            devices, now_ts = self.server.db.acquire_remote_session(
                user_id=user.id,
                session_id=session_id,
                controller_device_key=controller_key,
                target_device_key=target_key,
            )
        self._send_json(
            HTTPStatus.OK,
            {
                "ok": True,
                "sessionId": session_id,
                "devices": devices,
                "serverTimeUtc": now_ts,
            },
        )

    def _handle_session_release(self, user: UserRecord) -> None:
        body = self._read_json_body()
        session_id = sanitize_optional_str(body.get("sessionId"), 64)
        if not session_id:
            raise ValidationError("INVALID_SESSION_ID", "会话标识无效。")
        controller_key = validate_device_key(body.get("controllerDeviceKey"))
        devices, now_ts = self.server.db.release_remote_session(
            user_id=user.id,
            session_id=session_id,
            controller_device_key=controller_key,
        )
        self._send_json(
            HTTPStatus.OK,
            {
                "ok": True,
                "devices": devices,
                "serverTimeUtc": now_ts,
            },
        )

    def _extract_bearer_token(self) -> str:
        auth = self.headers.get("Authorization", "")
        if not auth.lower().startswith("bearer "):
            return ""
        return auth[7:].strip()

    def _require_user(self) -> UserRecord:
        token = self._extract_bearer_token()
        user = self.server.db.get_user_by_token(token)
        if user is None:
            raise StorageError(401, "UNAUTHORIZED", "登录状态已失效，请重新登录。")
        return user

    def _read_json_body(self) -> dict[str, object]:
        raw_len = self.headers.get("Content-Length")
        if raw_len is None:
            raise ValidationError("BAD_REQUEST", "缺少请求体。")
        try:
            length = int(raw_len)
        except ValueError as exc:
            raise ValidationError("BAD_REQUEST", "无效的 Content-Length。") from exc
        if length <= 0 or length > MAX_REQUEST_BODY_BYTES:
            raise ValidationError("PAYLOAD_TOO_LARGE", "请求体为空或超出长度限制。")
        raw = self.rfile.read(length)
        try:
            parsed = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise ValidationError("INVALID_JSON", "请求体不是有效的 JSON。") from exc
        if not isinstance(parsed, dict):
            raise ValidationError("INVALID_JSON", "请求体必须为 JSON 对象。")
        return parsed

    def _send_error_json(self, status_code: int, code: str, message: str) -> None:
        self._send_json(status_code, {"error": code, "message": message})

    def _send_json(self, status_code: int, payload: dict[str, object]) -> None:
        encoded = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status_code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(encoded)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(encoded)


def create_server(cfg: SyncServerConfig) -> tuple[SyncHttpServer, SyncDatabase]:
    os.makedirs(cfg.data_dir, exist_ok=True)
    db_path = os.path.join(cfg.data_dir, "sync.db")
    db = SyncDatabase(db_path)
    if cfg.invite_code:
        db.seed_env_invite_code(cfg.invite_code)
    limiter = RateLimiter()
    server = SyncHttpServer((cfg.host, cfg.port), SyncRequestHandler, db, cfg, limiter)

    if cfg.tls_cert_path and cfg.tls_key_path and os.path.isfile(cfg.tls_cert_path) and os.path.isfile(cfg.tls_key_path):
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.minimum_version = ssl.TLSVersion.TLSv1_2
        context.load_cert_chain(certfile=cfg.tls_cert_path, keyfile=cfg.tls_key_path)
        server.socket = context.wrap_socket(server.socket, server_side=True)

    return server, db


def main() -> None:
    cfg = SyncServerConfig.from_env()
    server, db = create_server(cfg)
    scheme = "https" if (cfg.tls_cert_path and cfg.tls_key_path and os.path.isfile(cfg.tls_cert_path)) else "http"
    sys.stdout.write(
        f"RemoteDesk Sync listening on {scheme}://{cfg.host}:{cfg.port} "
        f"(public={cfg.public_host}, prefix={cfg.easytier_network_prefix})\n"
    )
    sys.stdout.flush()

    def _shutdown_handler(_signum: int, _frame: object) -> None:
        server.shutdown()

    signal.signal(signal.SIGTERM, _shutdown_handler)
    signal.signal(signal.SIGINT, _shutdown_handler)

    try:
        server.serve_forever()
    finally:
        server.server_close()
        db.close()


if __name__ == "__main__":
    main()
