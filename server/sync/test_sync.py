"""Unit and integration tests for RemoteDesk Sync Service."""

from __future__ import annotations

import io
import json
import os
import sys
import tempfile
import threading
import unittest
import urllib.error
import urllib.request

SYNC_DIR = os.path.dirname(os.path.abspath(__file__))
if SYNC_DIR not in sys.path:
    sys.path.insert(0, SYNC_DIR)

from app import SyncHttpServer, SyncRequestHandler, SyncServerConfig, create_server  # noqa: E402
from security import RateLimiter  # noqa: E402
from storage import SyncDatabase  # noqa: E402


class _FakeSocket:
    def __init__(self, raw_request: bytes) -> None:
        self._rfile = io.BytesIO(raw_request)
        self.written = io.BytesIO()

    def makefile(self, mode: str, *args: object, **kwargs: object) -> io.BytesIO:
        if "r" in mode:
            return self._rfile
        return self.written

    def sendall(self, data: bytes) -> None:
        self.written.write(data)

    def close(self) -> None:
        pass


class _InProcessApp:
    """Exercises SyncRequestHandler without binding a TCP socket (sandbox-safe)."""

    def __init__(self, db: SyncDatabase, cfg: SyncServerConfig) -> None:
        self.db = db
        self.cfg = cfg
        self.rate_limiter = RateLimiter()

    def request(
        self,
        method: str,
        path: str,
        body: dict[str, object] | None = None,
        token: str | None = None,
    ) -> tuple[int, dict[str, object]]:
        payload = b"" if body is None else json.dumps(body).encode("utf-8")
        headers = [
            f"{method} {path} HTTP/1.1",
            "Host: 127.0.0.1",
            "Connection: close",
        ]
        if body is not None:
            headers.append("Content-Type: application/json")
            headers.append(f"Content-Length: {len(payload)}")
        if token is not None:
            headers.append(f"Authorization: Bearer {token}")
        raw_req = ("\r\n".join(headers) + "\r\n\r\n").encode("utf-8") + payload
        sock = _FakeSocket(raw_req)

        # Invoke BaseHTTPRequestHandler directly against _FakeSocket.
        handler = SyncRequestHandler.__new__(SyncRequestHandler)
        handler.connection = sock  # type: ignore[assignment]
        handler.rfile = sock.makefile("rb")
        handler.wfile = sock.makefile("wb")
        handler.client_address = ("127.0.0.1", 50000)
        handler.server = self  # type: ignore[assignment]
        handler.close_connection = True
        handler.handle_one_request()

        raw_resp = sock.written.getvalue()
        head, _, resp_body = raw_resp.partition(b"\r\n\r\n")
        status_line = head.split(b"\r\n", 1)[0].decode("ascii")
        status_code = int(status_line.split(" ")[1])
        return status_code, json.loads(resp_body.decode("utf-8"))


class SyncServiceTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.cfg = SyncServerConfig(
            host="127.0.0.1",
            port=0,
            data_dir=self._tmp.name,
            public_host="203.0.113.10",
            easytier_port=11010,
            easytier_ws_port=11011,
            easytier_network_prefix="rdtest",
            invite_code="INVITE-2026",
        )
        self.server = None
        self.base_url = None
        self.in_proc = None
        try:
            self.server, self.db = create_server(self.cfg)
            port = self.server.server_address[1]
            self.base_url = f"http://127.0.0.1:{port}"
            self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
            self.thread.start()
            # Probe whether outbound loopback connect is permitted in this environment.
            with urllib.request.urlopen(self.base_url + "/api/v1/health", timeout=2) as _:
                pass
        except (PermissionError, urllib.error.URLError):
            if self.server is not None:
                self.server.shutdown()
                self.server.server_close()
                self.server = None
                self.db.close()
            # Windows sandbox disallows outbound loopback connect; run handler directly in-process.
            self.db = SyncDatabase(os.path.join(self._tmp.name, "sync.db"))
            self.db.seed_env_invite_code(self.cfg.invite_code)
            self.in_proc = _InProcessApp(self.db, self.cfg)

    def tearDown(self) -> None:
        if self.server is not None:
            self.server.shutdown()
            self.server.server_close()
        self.db.close()
        self._tmp.cleanup()

    def _request(
        self,
        method: str,
        path: str,
        body: dict[str, object] | None = None,
        token: str | None = None,
    ) -> tuple[int, dict[str, object]]:
        if self.in_proc is not None:
            return self.in_proc.request(method, path, body, token)
        assert self.base_url is not None
        data = None if body is None else json.dumps(body).encode("utf-8")
        headers: dict[str, str] = {}
        if data is not None:
            headers["Content-Type"] = "application/json"
        if token is not None:
            headers["Authorization"] = f"Bearer {token}"
        req = urllib.request.Request(self.base_url + path, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(req, timeout=5) as resp:
                return resp.status, json.loads(resp.read().decode("utf-8"))
        except urllib.error.HTTPError as err:
            return err.code, json.loads(err.read().decode("utf-8"))

    def test_health_and_invite_registration_isolation(self) -> None:
        status, health = self._request("GET", "/api/v1/health")
        self.assertEqual(200, status)
        self.assertTrue(health["ok"])
        self.assertEqual("invite", health["registrationMode"])

        # Missing or wrong invite code rejected with 403.
        status, err = self._request(
            "POST",
            "/api/v1/auth/register",
            {"username": "alice", "password": "password123", "inviteCode": "WRONG"},
        )
        self.assertEqual(403, status)
        self.assertEqual("INVALID_INVITE", err["error"])

        # Register user A with valid invite code.
        status, reg_a = self._request(
            "POST",
            "/api/v1/auth/register",
            {"username": "Alice_01", "password": "password123", "inviteCode": "INVITE-2026"},
        )
        self.assertEqual(201, status)
        token_a = str(reg_a["token"])
        mesh_a = reg_a["meshConfig"]
        self.assertTrue(str(mesh_a["networkName"]).startswith("rdtest-"))
        self.assertEqual("tcp://203.0.113.10:11010", mesh_a["peerUrl"])
        self.assertEqual("ws://203.0.113.10:11011", mesh_a["fallbackWsUrl"])
        self.assertEqual(32, len(str(mesh_a["networkSecret"])))
        self.assertEqual(64, len(str(mesh_a["agentAuthKey"])))

        # Duplicate username (case-insensitive) rejected with 409.
        status, dup = self._request(
            "POST",
            "/api/v1/auth/register",
            {"username": "alice_01", "password": "password123", "inviteCode": "INVITE-2026"},
        )
        self.assertEqual(409, status)
        self.assertEqual("USERNAME_EXISTS", dup["error"])

        # Register user B and verify distinct isolated EasyTier network.
        status, reg_b = self._request(
            "POST",
            "/api/v1/auth/register",
            {"username": "Bob_02", "password": "password456", "inviteCode": "INVITE-2026"},
        )
        self.assertEqual(201, status)
        token_b = str(reg_b["token"])
        mesh_b = reg_b["meshConfig"]
        self.assertNotEqual(mesh_a["networkName"], mesh_b["networkName"])
        self.assertNotEqual(mesh_a["networkSecret"], mesh_b["networkSecret"])
        self.assertNotEqual(mesh_a["agentAuthKey"], mesh_b["agentAuthKey"])

        # Wrong password rejected (401), correct login succeeds and returns same meshConfig.
        status, _ = self._request("POST", "/api/v1/auth/login", {"username": "alice_01", "password": "badpassword"})
        self.assertEqual(401, status)

        status, login_a = self._request(
            "POST",
            "/api/v1/auth/login",
            {"username": "alice_01", "password": "password123"},
        )
        self.assertEqual(200, status)
        self.assertEqual(mesh_a, login_a["meshConfig"])

        # Logout invalidates token.
        token_login_a = str(login_a["token"])
        status, _ = self._request("POST", "/api/v1/auth/logout", {}, token=token_login_a)
        self.assertEqual(200, status)
        status, _ = self._request("GET", "/api/v1/account/profile", token=token_login_a)
        self.assertEqual(401, status)

        # Device checkin allocates .2 then .3 deterministically and preserves across checkins.
        status, chk1 = self._request(
            "POST",
            "/api/v1/devices/checkin",
            {
                "deviceKey": "dev-dorm-pc",
                "name": "Dorm Desktop",
                "hostname": "DORM-PC",
                "lanIpv4": "192.168.1.50",
                "sunshineReady": True,
                "gpuEncoder": "NVENC",
            },
            token=token_a,
        )
        self.assertEqual(200, status)
        self.assertEqual("10.126.126.2", chk1["device"]["virtualIpv4"])
        self.assertTrue(chk1["device"]["isOnline"])

        status, chk2 = self._request(
            "POST",
            "/api/v1/devices/checkin",
            {
                "deviceKey": "dev-laptop",
                "name": "Class Laptop",
                "hostname": "LAPTOP",
                "sunshineReady": False,
            },
            token=token_a,
        )
        self.assertEqual(200, status)
        self.assertEqual("10.126.126.3", chk2["device"]["virtualIpv4"])

        # Repeat checkin preserves existing IPv4.
        status, chk1_repeat = self._request(
            "POST",
            "/api/v1/devices/checkin",
            {
                "deviceKey": "dev-dorm-pc",
                "name": "Dorm Desktop",
                "hostname": "DORM-PC",
                "sunshineReady": True,
            },
            token=token_a,
        )
        self.assertEqual(200, status)
        self.assertEqual("10.126.126.2", chk1_repeat["device"]["virtualIpv4"])

        # Cross-user isolation: User B sees 0 devices and cannot rename/delete User A's device.
        status, list_b = self._request("GET", "/api/v1/devices", token=token_b)
        self.assertEqual(200, status)
        self.assertEqual([], list_b["devices"])

        dev1_id = str(chk1["device"]["id"])
        status, _ = self._request("PATCH", f"/api/v1/devices/{dev1_id}", {"name": "Hacked"}, token=token_b)
        self.assertEqual(404, status)
        status, _ = self._request("DELETE", f"/api/v1/devices/{dev1_id}", token=token_b)
        self.assertEqual(404, status)

        # User A renames & deletes device 1, freeing 10.126.126.2 for next device.
        status, renamed = self._request("PATCH", f"/api/v1/devices/{dev1_id}", {"name": "Main PC"}, token=token_a)
        self.assertEqual(200, status)
        self.assertEqual("Main PC", renamed["device"]["name"])

        status, _ = self._request("DELETE", f"/api/v1/devices/{dev1_id}", token=token_a)
        self.assertEqual(200, status)

        status, chk3 = self._request(
            "POST",
            "/api/v1/devices/checkin",
            {"deviceKey": "dev-new-box", "name": "New Box", "hostname": "NEW-BOX"},
            token=token_a,
        )
        self.assertEqual(200, status)
        self.assertEqual("10.126.126.2", chk3["device"]["virtualIpv4"])

    def test_rate_limiter_blocks_after_threshold(self) -> None:
        limiter = RateLimiter(max_attempts=3, window_seconds=60)
        for _ in range(2):
            limiter.record_failure("1.2.3.4")
            self.assertFalse(limiter.is_blocked("1.2.3.4"))
        limiter.record_failure("1.2.3.4")
        self.assertTrue(limiter.is_blocked("1.2.3.4"))
        limiter.clear("1.2.3.4")
        self.assertFalse(limiter.is_blocked("1.2.3.4"))


if __name__ == "__main__":
    unittest.main()
