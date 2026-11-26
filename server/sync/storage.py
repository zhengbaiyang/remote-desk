"""SQLite persistence layer for RemoteDesk Sync Service (stdlib-only)."""

from __future__ import annotations

import os
import sqlite3
import threading
import time
import uuid
from dataclasses import dataclass

from security import (
    generate_account_mesh_credentials,
    generate_session_token,
    hash_password,
    hash_secret_token,
    verify_password,
)

ONLINE_TTL_SECONDS = 90
REMOTE_SESSION_TTL_SECONDS = 45
SESSION_TTL_SECONDS = 90 * 24 * 3600


class StorageError(Exception):
    """Domain error returned by storage operations."""

    def __init__(self, status_code: int, code: str, message: str) -> None:
        super().__init__(message)
        self.status_code = status_code
        self.code = code
        self.message = message


@dataclass(frozen=True)
class UserRecord:
    id: str
    username: str
    easytier_network_name: str
    easytier_network_secret: str
    agent_auth_key: str
    created_at: int

    def to_public_dict(self) -> dict[str, object]:
        return {
            "id": self.id,
            "username": self.username,
            "createdAtUtc": self.created_at,
        }


def _row_to_device_dict(
    row: sqlite3.Row,
    now_ts: int,
    session_info: dict[str, object] | None = None,
) -> dict[str, object]:
    last_seen = int(row["last_seen_at"])
    info = session_info or {}
    return {
        "id": row["id"],
        "deviceKey": row["device_key"],
        "name": row["name"],
        "hostname": row["hostname"],
        "virtualIpv4": row["virtual_ipv4"],
        "lanIpv4": row["lan_ipv4"],
        "lanIpv6": row["lan_ipv6"],
        "sunshineReady": bool(row["sunshine_ready"]),
        "sunshinePort": int(row["sunshine_port"]),
        "agentPort": int(row["agent_port"]),
        "securityPinRequired": bool(row["security_pin_required"]),
        "gpuEncoder": row["gpu_encoder"],
        "osVersion": row["os_version"],
        "lastSeenAtUtc": last_seen,
        "isOnline": (now_ts - last_seen) <= ONLINE_TTL_SECONDS,
        "remoteControlState": info.get("remoteControlState", "idle"),
        "controlledByDeviceKey": info.get("controlledByDeviceKey"),
        "controlledByDeviceName": info.get("controlledByDeviceName"),
        "controllingTargetDeviceKey": info.get("controllingTargetDeviceKey"),
        "controllingTargetDeviceName": info.get("controllingTargetDeviceName"),
    }


class SyncDatabase:
    """Thread-safe SQLite database backing accounts, sessions, devices, and invites."""

    def __init__(self, db_path: str) -> None:
        self._db_path = db_path
        parent = os.path.dirname(os.path.abspath(db_path))
        if parent:
            os.makedirs(parent, exist_ok=True)
        self._lock = threading.Lock()
        self._conn = sqlite3.connect(db_path, check_same_thread=False)
        self._conn.row_factory = sqlite3.Row
        with self._lock:
            self._conn.execute("PRAGMA journal_mode=WAL;")
            self._conn.execute("PRAGMA foreign_keys=ON;")
            self._init_schema()

    def close(self) -> None:
        with self._lock:
            self._conn.close()

    def _init_schema(self) -> None:
        self._conn.executescript(
            """
            CREATE TABLE IF NOT EXISTS users (
                id TEXT PRIMARY KEY,
                username TEXT NOT NULL,
                username_lower TEXT NOT NULL UNIQUE,
                password_salt TEXT NOT NULL,
                password_hash TEXT NOT NULL,
                easytier_network_name TEXT NOT NULL UNIQUE,
                easytier_network_secret TEXT NOT NULL,
                agent_auth_key TEXT NOT NULL,
                created_at INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS sessions (
                token_hash TEXT PRIMARY KEY,
                user_id TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                expires_at INTEGER NOT NULL,
                FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS devices (
                id TEXT PRIMARY KEY,
                user_id TEXT NOT NULL,
                device_key TEXT NOT NULL,
                name TEXT NOT NULL,
                hostname TEXT NOT NULL,
                virtual_ipv4 TEXT NOT NULL,
                lan_ipv4 TEXT,
                lan_ipv6 TEXT,
                sunshine_ready INTEGER NOT NULL DEFAULT 0,
                sunshine_port INTEGER NOT NULL DEFAULT 47989,
                agent_port INTEGER NOT NULL DEFAULT 47999,
                security_pin_required INTEGER NOT NULL DEFAULT 0,
                gpu_encoder TEXT,
                os_version TEXT,
                last_seen_at INTEGER NOT NULL,
                created_at INTEGER NOT NULL,
                UNIQUE (user_id, device_key),
                UNIQUE (user_id, virtual_ipv4),
                FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS invite_codes (
                code_hash TEXT PRIMARY KEY,
                label TEXT NOT NULL,
                max_uses INTEGER NOT NULL DEFAULT 0,
                used_count INTEGER NOT NULL DEFAULT 0,
                created_at INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS remote_sessions (
                id TEXT PRIMARY KEY,
                user_id TEXT NOT NULL,
                controller_device_key TEXT NOT NULL,
                target_device_key TEXT NOT NULL,
                started_at INTEGER NOT NULL,
                last_heartbeat_at INTEGER NOT NULL,
                FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
            );
            """
        )
        self._conn.commit()

    def seed_env_invite_code(self, raw_invite_code: str, label: str = "env-default") -> None:
        cleaned = raw_invite_code.strip()
        if not cleaned:
            return
        code_hash = hash_secret_token(cleaned)
        now_ts = int(time.time())
        with self._lock:
            self._conn.execute(
                """
                INSERT OR IGNORE INTO invite_codes (code_hash, label, max_uses, used_count, created_at)
                VALUES (?, ?, 0, 0, ?)
                """,
                (code_hash, label, now_ts),
            )
            self._conn.commit()

    def create_user_with_invite(
        self,
        username: str,
        password: str,
        invite_code: str,
        network_prefix: str,
    ) -> tuple[UserRecord, str]:
        cleaned_invite = (invite_code or "").strip()
        if not cleaned_invite:
            raise StorageError(403, "INVITE_REQUIRED", "需要填写有效邀请码才能注册账号。")

        invite_hash = hash_secret_token(cleaned_invite)
        username_lower = username.lower()
        salt_hex, pass_hash_hex = hash_password(password)
        user_id = uuid.uuid4().hex
        now_ts = int(time.time())
        net_name, net_secret, auth_key = generate_account_mesh_credentials(network_prefix)
        raw_token, token_hash = generate_session_token()
        expires_at = now_ts + SESSION_TTL_SECONDS

        with self._lock:
            try:
                self._conn.execute("BEGIN IMMEDIATE")
                invite_row = self._conn.execute(
                    "SELECT code_hash, max_uses, used_count FROM invite_codes WHERE code_hash = ?",
                    (invite_hash,),
                ).fetchone()
                if invite_row is None:
                    raise StorageError(403, "INVALID_INVITE", "邀请码无效，请联系服务器管理员获取。")

                max_uses = int(invite_row["max_uses"])
                used_count = int(invite_row["used_count"])
                if max_uses > 0 and used_count >= max_uses:
                    raise StorageError(403, "INVITE_EXHAUSTED", "该邀请码已达到最大使用次数。")

                existing_user = self._conn.execute(
                    "SELECT id FROM users WHERE username_lower = ?",
                    (username_lower,),
                ).fetchone()
                if existing_user is not None:
                    raise StorageError(409, "USERNAME_EXISTS", "该用户名已存在，请直接登录或更换用户名。")

                self._conn.execute(
                    """
                    UPDATE invite_codes SET used_count = used_count + 1 WHERE code_hash = ?
                    """,
                    (invite_hash,),
                )
                self._conn.execute(
                    """
                    INSERT INTO users (
                        id, username, username_lower, password_salt, password_hash,
                        easytier_network_name, easytier_network_secret, agent_auth_key, created_at
                    )
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
                    """,
                    (
                        user_id,
                        username,
                        username_lower,
                        salt_hex,
                        pass_hash_hex,
                        net_name,
                        net_secret,
                        auth_key,
                        now_ts,
                    ),
                )
                self._conn.execute(
                    """
                    INSERT INTO sessions (token_hash, user_id, created_at, expires_at)
                    VALUES (?, ?, ?, ?)
                    """,
                    (token_hash, user_id, now_ts, expires_at),
                )
                self._conn.commit()
            except Exception:
                self._conn.rollback()
                raise

        user = UserRecord(
            id=user_id,
            username=username,
            easytier_network_name=net_name,
            easytier_network_secret=net_secret,
            agent_auth_key=auth_key,
            created_at=now_ts,
        )
        return user, raw_token

    def authenticate_user(self, username: str, password: str) -> tuple[UserRecord, str]:
        username_lower = username.lower()
        now_ts = int(time.time())
        with self._lock:
            row = self._conn.execute(
                """
                SELECT id, username, password_salt, password_hash,
                       easytier_network_name, easytier_network_secret, agent_auth_key, created_at
                FROM users
                WHERE username_lower = ?
                """,
                (username_lower,),
            ).fetchone()
            if row is None or not verify_password(password, row["password_salt"], row["password_hash"]):
                raise StorageError(401, "INVALID_CREDENTIALS", "账号或密码错误。")

            raw_token, token_hash = generate_session_token()
            expires_at = now_ts + SESSION_TTL_SECONDS
            self._conn.execute(
                """
                INSERT INTO sessions (token_hash, user_id, created_at, expires_at)
                VALUES (?, ?, ?, ?)
                """,
                (token_hash, row["id"], now_ts, expires_at),
            )
            self._conn.commit()

            user = UserRecord(
                id=row["id"],
                username=row["username"],
                easytier_network_name=row["easytier_network_name"],
                easytier_network_secret=row["easytier_network_secret"],
                agent_auth_key=row["agent_auth_key"],
                created_at=int(row["created_at"]),
            )
            return user, raw_token

    def get_user_by_token(self, raw_token: str) -> UserRecord | None:
        if not raw_token:
            return None
        token_hash = hash_secret_token(raw_token)
        now_ts = int(time.time())
        with self._lock:
            row = self._conn.execute(
                """
                SELECT u.id, u.username, u.easytier_network_name, u.easytier_network_secret,
                       u.agent_auth_key, u.created_at, s.expires_at
                FROM sessions s
                JOIN users u ON u.id = s.user_id
                WHERE s.token_hash = ?
                """,
                (token_hash,),
            ).fetchone()
            if row is None:
                return None
            if int(row["expires_at"]) < now_ts:
                self._conn.execute("DELETE FROM sessions WHERE token_hash = ?", (token_hash,))
                self._conn.commit()
                return None
            return UserRecord(
                id=row["id"],
                username=row["username"],
                easytier_network_name=row["easytier_network_name"],
                easytier_network_secret=row["easytier_network_secret"],
                agent_auth_key=row["agent_auth_key"],
                created_at=int(row["created_at"]),
            )

    def delete_session(self, raw_token: str) -> None:
        if not raw_token:
            return
        token_hash = hash_secret_token(raw_token)
        with self._lock:
            self._conn.execute("DELETE FROM sessions WHERE token_hash = ?", (token_hash,))
            self._conn.commit()

    def _allocate_virtual_ipv4(self, user_id: str) -> str:
        rows = self._conn.execute(
            "SELECT virtual_ipv4 FROM devices WHERE user_id = ?",
            (user_id,),
        ).fetchall()
        used = {str(r["virtual_ipv4"]) for r in rows}
        for host_octet in range(2, 255):
            candidate = f"10.126.126.{host_octet}"
            if candidate not in used:
                return candidate
        raise StorageError(507, "SUBNET_EXHAUSTED", "该账号下的虚拟 IP 配额（253 台设备）已满，请先移除旧设备。")

    def upsert_device_checkin(
        self,
        user_id: str,
        device_key: str,
        name: str,
        hostname: str,
        lan_ipv4: str | None,
        lan_ipv6: str | None,
        sunshine_ready: bool,
        sunshine_port: int,
        agent_port: int,
        security_pin_required: bool,
        gpu_encoder: str | None,
        os_version: str | None,
    ) -> tuple[dict[str, object], list[dict[str, object]], int]:
        now_ts = int(time.time())
        with self._lock:
            try:
                self._conn.execute("BEGIN IMMEDIATE")
                existing = self._conn.execute(
                    """
                    SELECT id, virtual_ipv4, name
                    FROM devices
                    WHERE user_id = ? AND device_key = ?
                    """,
                    (user_id, device_key),
                ).fetchone()

                if existing is not None:
                    device_id = str(existing["id"])
                    effective_name = str(existing["name"]).strip() or name
                    self._conn.execute(
                        """
                        UPDATE devices
                        SET name = ?, hostname = ?, lan_ipv4 = ?, lan_ipv6 = ?,
                            sunshine_ready = ?, sunshine_port = ?, agent_port = ?,
                            security_pin_required = ?, gpu_encoder = ?, os_version = ?,
                            last_seen_at = ?
                        WHERE id = ? AND user_id = ?
                        """,
                        (
                            effective_name,
                            hostname,
                            lan_ipv4,
                            lan_ipv6,
                            1 if sunshine_ready else 0,
                            sunshine_port,
                            agent_port,
                            1 if security_pin_required else 0,
                            gpu_encoder,
                            os_version,
                            now_ts,
                            device_id,
                            user_id,
                        ),
                    )
                else:
                    device_id = uuid.uuid4().hex
                    virtual_ipv4 = self._allocate_virtual_ipv4(user_id)
                    self._conn.execute(
                        """
                        INSERT INTO devices (
                            id, user_id, device_key, name, hostname, virtual_ipv4,
                            lan_ipv4, lan_ipv6, sunshine_ready, sunshine_port, agent_port,
                            security_pin_required, gpu_encoder, os_version, last_seen_at, created_at
                        )
                        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                        """,
                        (
                            device_id,
                            user_id,
                            device_key,
                            name,
                            hostname,
                            virtual_ipv4,
                            lan_ipv4,
                            lan_ipv6,
                            1 if sunshine_ready else 0,
                            sunshine_port,
                            agent_port,
                            1 if security_pin_required else 0,
                            gpu_encoder,
                            os_version,
                            now_ts,
                            now_ts,
                        ),
                    )

                self._conn.commit()
            except Exception:
                self._conn.rollback()
                raise

            devices = self._build_device_list_locked(user_id, now_ts)
        current = next(d for d in devices if d["id"] == device_id)
        return current, devices, now_ts

    def _prune_expired_remote_sessions(self, now_ts: int) -> None:
        cutoff = now_ts - REMOTE_SESSION_TTL_SECONDS
        self._conn.execute(
            "DELETE FROM remote_sessions WHERE last_heartbeat_at < ?",
            (cutoff,),
        )

    def _build_device_list_locked(self, user_id: str, now_ts: int) -> list[dict[str, object]]:
        cutoff = now_ts - REMOTE_SESSION_TTL_SECONDS
        rows = self._conn.execute(
            "SELECT * FROM devices WHERE user_id = ? ORDER BY created_at ASC",
            (user_id,),
        ).fetchall()
        sessions = self._conn.execute(
            """
            SELECT controller_device_key, target_device_key
            FROM remote_sessions
            WHERE user_id = ? AND last_heartbeat_at >= ?
            ORDER BY started_at ASC
            """,
            (user_id, cutoff),
        ).fetchall()

        name_by_key = {str(r["device_key"]): str(r["name"]) for r in rows}
        controlled_by: dict[str, str] = {}
        controlling_target: dict[str, str] = {}
        for sess in sessions:
            ctrl_key = str(sess["controller_device_key"])
            tgt_key = str(sess["target_device_key"])
            controlled_by.setdefault(tgt_key, ctrl_key)
            controlling_target.setdefault(ctrl_key, tgt_key)

        devices: list[dict[str, object]] = []
        for row in rows:
            dkey = str(row["device_key"])
            info: dict[str, object] = {}
            if dkey in controlled_by:
                ctrl_key = controlled_by[dkey]
                info = {
                    "remoteControlState": "controlled",
                    "controlledByDeviceKey": ctrl_key,
                    "controlledByDeviceName": name_by_key.get(ctrl_key, ctrl_key),
                    "controllingTargetDeviceKey": None,
                    "controllingTargetDeviceName": None,
                }
            elif dkey in controlling_target:
                tgt_key = controlling_target[dkey]
                info = {
                    "remoteControlState": "controlling",
                    "controlledByDeviceKey": None,
                    "controlledByDeviceName": None,
                    "controllingTargetDeviceKey": tgt_key,
                    "controllingTargetDeviceName": name_by_key.get(tgt_key, tgt_key),
                }
            devices.append(_row_to_device_dict(row, now_ts, info))
        return devices

    def acquire_remote_session(
        self,
        user_id: str,
        session_id: str,
        controller_device_key: str,
        target_device_key: str,
    ) -> tuple[list[dict[str, object]], int]:
        now_ts = int(time.time())
        with self._lock:
            try:
                self._conn.execute("BEGIN IMMEDIATE")
                self._prune_expired_remote_sessions(now_ts)
                active_rows = self._conn.execute(
                    """
                    SELECT id, controller_device_key, target_device_key
                    FROM remote_sessions
                    WHERE user_id = ? AND id != ?
                    """,
                    (user_id, session_id),
                ).fetchall()

                for row in active_rows:
                    if str(row["target_device_key"]) == target_device_key:
                        raise StorageError(409, "TARGET_BEING_CONTROLLED", "目标设备当前正被其他设备远控，暂不可连接。")
                    if str(row["controller_device_key"]) == target_device_key:
                        raise StorageError(409, "TARGET_IS_CONTROLLING", "目标设备当前正在远控其他设备，暂不可被远控。")

                existing_same = self._conn.execute(
                    "SELECT id FROM remote_sessions WHERE id = ? AND user_id = ?",
                    (session_id, user_id),
                ).fetchone()
                if existing_same is not None:
                    self._conn.execute(
                        """
                        UPDATE remote_sessions
                        SET last_heartbeat_at = ?
                        WHERE id = ? AND user_id = ?
                        """,
                        (now_ts, session_id, user_id),
                    )
                else:
                    self._conn.execute(
                        """
                        INSERT INTO remote_sessions (
                            id, user_id, controller_device_key, target_device_key, started_at, last_heartbeat_at
                        )
                        VALUES (?, ?, ?, ?, ?, ?)
                        """,
                        (session_id, user_id, controller_device_key, target_device_key, now_ts, now_ts),
                    )
                self._conn.commit()
            except Exception:
                self._conn.rollback()
                raise

            devices = self._build_device_list_locked(user_id, now_ts)
        return devices, now_ts

    def heartbeat_remote_session(
        self,
        user_id: str,
        session_id: str,
        controller_device_key: str,
        target_device_key: str,
    ) -> tuple[list[dict[str, object]], int]:
        return self.acquire_remote_session(user_id, session_id, controller_device_key, target_device_key)

    def release_remote_session(
        self,
        user_id: str,
        session_id: str,
        controller_device_key: str,
    ) -> tuple[list[dict[str, object]], int]:
        now_ts = int(time.time())
        with self._lock:
            self._conn.execute(
                "DELETE FROM remote_sessions WHERE user_id = ? AND id = ? AND controller_device_key = ?",
                (user_id, session_id, controller_device_key),
            )
            self._conn.commit()
            devices = self._build_device_list_locked(user_id, now_ts)
        return devices, now_ts

    def list_devices(self, user_id: str) -> tuple[list[dict[str, object]], int]:
        now_ts = int(time.time())
        with self._lock:
            devices = self._build_device_list_locked(user_id, now_ts)
        return devices, now_ts

    def rename_device(self, user_id: str, device_id: str, new_name: str) -> dict[str, object]:
        now_ts = int(time.time())
        with self._lock:
            cur = self._conn.execute(
                "UPDATE devices SET name = ? WHERE id = ? AND user_id = ?",
                (new_name, device_id, user_id),
            )
            self._conn.commit()
            if cur.rowcount == 0:
                raise StorageError(404, "DEVICE_NOT_FOUND", "未找到该设备或无权操作。")
            devices = self._build_device_list_locked(user_id, now_ts)
        return next(d for d in devices if d["id"] == device_id)

    def delete_device(self, user_id: str, device_id: str) -> None:
        with self._lock:
            row = self._conn.execute(
                "SELECT device_key FROM devices WHERE id = ? AND user_id = ?",
                (device_id, user_id),
            ).fetchone()
            cur = self._conn.execute(
                "DELETE FROM devices WHERE id = ? AND user_id = ?",
                (device_id, user_id),
            )
            if cur.rowcount == 0:
                self._conn.commit()
                raise StorageError(404, "DEVICE_NOT_FOUND", "未找到该设备或无权删除。")
            if row is not None:
                dkey = str(row["device_key"])
                self._conn.execute(
                    "DELETE FROM remote_sessions WHERE user_id = ? AND (controller_device_key = ? OR target_device_key = ?)",
                    (user_id, dkey, dkey),
                )
            self._conn.commit()
