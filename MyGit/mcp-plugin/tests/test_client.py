"""Offline tests. Uses httpx.MockTransport; no live Forgejo or credentials."""
import base64
import sys
import unittest
from pathlib import Path

import httpx

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from client import ForgejoClient, Settings, _branch, _safe_write_path


class ForgejoClientTests(unittest.TestCase):
    def settings(self, write=False, allow=()):
        return Settings("http://127.0.0.1:3020", "unit-test-token", write, frozenset(allow))

    def test_read_repositories_authorization_header(self):
        def responder(req):
            self.assertEqual(req.url.path, "/api/v1/user/repos")
            self.assertEqual(req.headers["Authorization"], "token unit-test-token")
            return httpx.Response(200, json=[{"name": "Kassa", "private": True}])

        client = ForgejoClient(self.settings(), httpx.MockTransport(responder))
        self.assertEqual(client.repositories()[0]["name"], "Kassa")

    def test_read_utf8_file(self):
        encoded = base64.b64encode("Привет, код".encode()).decode()
        def responder(req):
            self.assertEqual(req.url.path, "/api/v1/repos/user/project/contents/src/main.py")
            self.assertEqual(req.url.params.get("ref"), "main")
            return httpx.Response(200, json={
                "path": "src/main.py", "size": 20, "sha": "abc", "type": "file",
                "encoding": "base64", "content": encoded
            })
        result = ForgejoClient(self.settings(), httpx.MockTransport(responder)).file("user", "project", "src/main.py", "main")
        self.assertEqual(result["content"], "Привет, код")
        self.assertTrue(result["untrusted_repository_text"])

    def test_read_only_denies_write(self):
        client = ForgejoClient(self.settings())
        with self.assertRaises(PermissionError):
            client.create_repo("test")
        with self.assertRaises(PermissionError):
            client.create_branch("user", "proj", "ai/new")

    def test_repo_allowlist(self):
        client = ForgejoClient(self.settings(True, ("user/allowed",)))
        with self.assertRaises(PermissionError):
            client.create_branch("user", "forbidden", "ai/new")

    def test_protect_branches_and_paths(self):
        with self.assertRaises(ValueError):
            _branch("main")
        with self.assertRaises(ValueError):
            _branch("ai/../main")
        with self.assertRaises(PermissionError):
            _safe_write_path(".forgejo/workflows/build.yml")
        with self.assertRaises(PermissionError):
            _safe_write_path("secrets/.env")
        with self.assertRaises(ValueError):
            _safe_write_path("../outside.txt")

    def test_nonlocal_http_refused(self):
        with self.assertRaises(ValueError):
            Settings("http://example.org", "token").validate()

    def test_write_requires_allowlist(self):
        with self.assertRaises(ValueError):
            Settings("https://git.example.com", "secret", True).validate()

    def test_commit_requires_existing_sha(self):
        def responder(req):
            if req.method != "GET":
                self.fail("Expected SHA guard to stop PUT")
            return httpx.Response(200, json={"sha": "current-sha", "type": "file"})
        client = ForgejoClient(self.settings(True, ("user/proj",)), httpx.MockTransport(responder))
        with self.assertRaises(RuntimeError):
            client.commit_text("user", "proj", "ai/feature", "README.md", "new", "update docs")
        with self.assertRaises(RuntimeError):
            client.commit_text("user", "proj", "ai/feature", "README.md", "new", "update docs", "stale-sha")

    def test_create_private_repo_flag(self):
        def responder(req):
            self.assertEqual(req.method, "POST")
            self.assertEqual(req.url.path, "/api/v1/user/repos")
            import json
            body = json.loads(req.content)
            self.assertIs(body["private"], True)
            return httpx.Response(201, json=body)
        client = ForgejoClient(self.settings(True, ("user/project",)), httpx.MockTransport(responder))
        self.assertTrue(client.create_repo("project")["private"])


if __name__ == "__main__":
    unittest.main()
