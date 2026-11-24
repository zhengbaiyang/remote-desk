"""Security helpers for RemoteDesk Sync Service (stdlib-only)."""

from __future__ import annotations

import hashlib
import hmac
import re
import secrets
import threading
import time
from collections import defaultdict, deque

_USERNAME_RE = re.compile(r"^[a-zA-Z0-9_.-]{3,32}$")
_TOKEN_RE = re.compile(r"^[a-zA-Z0-9_.-]{1,64}$")

_SCRYPT_N = 16384
_SCRYPT_R = 8
_SCRYPT_P = 1
_SCRYPT_DKLEN = 32


class ValidationError(ValueError):
    """Raised when client-supplied parameters fail strict validation."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code
        self.message = message


def validate_username(raw: object) -> str:
    if not isinstance(raw, str):
        raise ValidationError("INVALID_USERNAME", "用户名必须为字符串。")
    username = raw.strip()
    if not _USERNAME_RE.fullmatch(username):
        raise ValidationError(
            "INVALID_USERNAME",
            "用户名需为 3~32 位字母、数字、下划线、点或连字符。",
        )
    if username.startswith("-") or username.startswith("."):
        raise ValidationError("INVALID_USERNAME", "用户名不能以 '-' 或 '.' 开头。")
    return username


def validate_password(raw: object) -> str:
    if not isinstance(raw, str):
        raise ValidationError("INVALID_PASSWORD", "密码必须为字符串。")
    if len(raw) < 6 or len(raw) > 128:
        raise ValidationError("INVALID_PASSWORD", "密码长度需在 6 到 128 个字符之间。")
    if any(ord(ch) < 32 for ch in raw):
        raise ValidationError("INVALID_PASSWORD", "密码不能包含控制字符。")
    return raw


def validate_device_key(raw: object) -> str:
    if not isinstance(raw, str):
        raise ValidationError("INVALID_DEVICE_KEY", "设备标识无效。")
    key = raw.strip()
    if not _TOKEN_RE.fullmatch(key) or key.startswith("-"):
        raise ValidationError("INVALID_DEVICE_KEY", "设备标识格式无效。")
    return key


def validate_device_name(raw: object) -> str:
    if not isinstance(raw, str):
        raise ValidationError("INVALID_DEVICE_NAME", "设备名称必须为字符串。")
    name = raw.strip()
    if len(name) < 1 or len(name) > 40 or any(ord(ch) < 32 for ch in name):
        raise ValidationError("INVALID_DEVICE_NAME", "设备名称需为 1 到 40 个有效字符。")
    return name


def sanitize_optional_str(raw: object, max_len: int = 128) -> str | None:
    if raw is None:
        return None
    if not isinstance(raw, str):
        raise ValidationError("INVALID_PARAM", "参数格式无效。")
    cleaned = raw.strip()
    if not cleaned:
        return None
    if len(cleaned) > max_len or any(ord(ch) < 32 for ch in cleaned):
        raise ValidationError("INVALID_PARAM", "参数包含非法字符或超出长度限制。")
    return cleaned


def normalize_prefix(raw_prefix: str | None) -> str:
    candidate = (raw_prefix or "remotedesk").strip()
    cleaned = re.sub(r"[^a-zA-Z0-9_-]", "", candidate)
    if not cleaned or cleaned[0] == "-":
        cleaned = "remotedesk"
    return cleaned[:24]


def hash_password(password: str, salt_hex: str | None = None) -> tuple[str, str]:
    salt = bytes.fromhex(salt_hex) if salt_hex else secrets.token_bytes(16)
    derived = hashlib.scrypt(
        password.encode("utf-8"),
        salt=salt,
        n=_SCRYPT_N,
        r=_SCRYPT_R,
        p=_SCRYPT_P,
        dklen=_SCRYPT_DKLEN,
    )
    return salt.hex(), derived.hex()


def verify_password(password: str, salt_hex: str, expected_hash_hex: str) -> bool:
    try:
        _, actual_hex = hash_password(password, salt_hex=salt_hex)
    except ValueError:
        return False
    return hmac.compare_digest(actual_hex, expected_hash_hex)


def hash_secret_token(raw_token: str) -> str:
    return hashlib.sha256(raw_token.encode("utf-8")).hexdigest()


def generate_session_token() -> tuple[str, str]:
    raw_token = secrets.token_urlsafe(32)
    return raw_token, hash_secret_token(raw_token)


def generate_account_mesh_credentials(prefix: str) -> tuple[str, str, str]:
    safe_prefix = normalize_prefix(prefix)
    network_name = f"{safe_prefix}-{secrets.token_hex(6)}"
    network_secret = secrets.token_hex(16)
    agent_auth_key = secrets.token_hex(32)
    return network_name, network_secret, agent_auth_key


class RateLimiter:
    """Thread-safe sliding-window failure rate limiter keyed by client IP."""

    def __init__(self, max_attempts: int = 15, window_seconds: int = 300) -> None:
        self._max_attempts = max_attempts
        self._window_seconds = window_seconds
        self._failures: dict[str, deque[float]] = defaultdict(deque)
        self._lock = threading.Lock()

    def is_blocked(self, key: str) -> bool:
        now = time.monotonic()
        cutoff = now - self._window_seconds
        with self._lock:
            bucket = self._failures.get(key)
            if not bucket:
                return False
            while bucket and bucket[0] < cutoff:
                bucket.popleft()
            if not bucket:
                del self._failures[key]
                return False
            return len(bucket) >= self._max_attempts

    def record_failure(self, key: str) -> None:
        now = time.monotonic()
        cutoff = now - self._window_seconds
        with self._lock:
            bucket = self._failures[key]
            while bucket and bucket[0] < cutoff:
                bucket.popleft()
            bucket.append(now)

    def clear(self, key: str) -> None:
        with self._lock:
            self._failures.pop(key, None)
