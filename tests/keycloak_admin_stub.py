"""Minimal Keycloak Admin API fixture for owner-account provisioning tests."""
import json
import sys
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class Handler(BaseHTTPRequestHandler):
    usernames = set()

    def do_POST(self):
        raw = self.read_body()
        if self.path == "/auth/realms/kasa/protocol/openid-connect/token":
            self.reply(200, {"access_token": "fixture-token-" + "x" * 48})
            return
        if self.path == "/auth/admin/realms/kasa/users":
            if self.headers.get("Authorization") != "Bearer fixture-token-" + "x" * 48:
                self.reply(401, {"error": "unauthorized"})
                return
            body = json.loads(raw or b"{}")
            username = body.get("username")
            credential = (body.get("credentials") or [{}])[0]
            checks = {
                "username": bool(username),
                "actions": body.get("requiredActions") == ["UPDATE_PASSWORD", "CONFIGURE_TOTP"],
                "credential_type": credential.get("type") == "password",
                "temporary": credential.get("temporary") is True,
                "password_length": len(credential.get("value", "")) >= 16,
            }
            if not all(checks.values()):
                print("Keycloak fixture validation:", checks, file=sys.stderr, flush=True)
                self.reply(400, {"error": "invalid_fixture_request"})
                return
            if username in self.usernames:
                self.reply(409, {"error": "exists"})
                return
            self.usernames.add(username)
            subject = str(uuid.uuid4())
            self.send_response(201)
            self.send_header("Location", "/auth/admin/realms/kasa/users/" + subject)
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        self.reply(404, {"error": "not_found"})

    def do_DELETE(self):
        if self.path.startswith("/auth/admin/realms/kasa/users/"):
            self.send_response(204)
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        self.reply(404, {"error": "not_found"})

    def reply(self, status, body):
        data = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def read_body(self):
        if self.headers.get("Transfer-Encoding", "").lower() != "chunked":
            return self.rfile.read(int(self.headers.get("Content-Length", "0")))
        chunks = []
        while True:
            size = int(self.rfile.readline().split(b";", 1)[0].strip(), 16)
            if size == 0:
                while self.rfile.readline().strip():
                    pass
                break
            chunks.append(self.rfile.read(size))
            self.rfile.read(2)
        return b"".join(chunks)

    def log_message(self, *_):
        pass


server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
with open(sys.argv[1], "w", encoding="ascii") as stream:
    stream.write(str(server.server_port))
server.serve_forever()
