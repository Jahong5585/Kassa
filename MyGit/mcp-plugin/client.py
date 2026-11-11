"""Safe Forgejo REST client for MyGit MCP. No user input is used as a URL host."""
from __future__ import annotations

import base64
import os
import re
from dataclasses import dataclass
from urllib.parse import quote, urlsplit

import httpx

_NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$")
_BRANCH = re.compile(r"^ai/[A-Za-z0-9][A-Za-z0-9._/-]{0,99}$")
_FORBIDDEN = {".env", "id_rsa", "id_ed25519"}
_MAX_CONTENT = 100_000
_MAX_READ = 256_000


def _name(value: str, label: str) -> str:
    if not isinstance(value, str) or not _NAME.fullmatch(value) or value in {".", ".."}:
        raise ValueError(f"Invalid {label}: use letters, numbers, dots, hyphens or underscores")
    return value


def _branch(value: str) -> str:
    if not isinstance(value, str) or not _BRANCH.fullmatch(value):
        raise ValueError("Write branches must start with ai/ (for example ai/new-feature)")
    if ".." in value or "//" in value or value.endswith(("/", ".", ".lock")):
        raise ValueError("Unsafe branch name")
    return value


def _path(value: str) -> str:
    if not isinstance(value, str) or not value or len(value) > 500:
        raise ValueError("A relative repository path is required")
    if "\\" in value or "\x00" in value or value.startswith("/"):
        raise ValueError("Unsafe file path")
    parts = value.split("/")
    if any(p in {"", ".", ".."} for p in parts):
        raise ValueError("Unsafe file path")
    return "/".join(parts)


def _safe_write_path(value: str) -> str:
    result = _path(value)
    parts = result.lower().split("/")
    if any(x in {".git", ".github", ".forgejo"} for x in parts):
        raise PermissionError("Cannot modify Git metadata, actions or workflow configurations")
    if parts[-1] in _FORBIDDEN or parts[-1].endswith((".pem", ".key", ".p12", ".pfx")):
        raise PermissionError("Refusing to modify credentials or private-key files")
    return result


@dataclass(frozen=True)
class Settings:
    api_url: str
    token: str
    write_enabled: bool = False
    allowed_repos: frozenset[str] = frozenset()
    apk_workflow: str = "build-apk.yml"

    @classmethod
    def from_env(cls) -> "Settings":
        api_url = os.getenv("FORGEJO_URL", "http://127.0.0.1:3020").rstrip("/")
        token = os.getenv("FORGEJO_TOKEN", "").strip()
        enabled = os.getenv("MYGIT_WRITE_ENABLED", "false").lower() == "true"
        repos = frozenset(x.strip() for x in os.getenv("MYGIT_ALLOWED_REPOS", "").split(",") if x.strip())
        result = cls(api_url, token, enabled, repos, os.getenv("MYGIT_APK_WORKFLOW", "build-apk.yml"))
        result.validate()
        return result

    def validate(self) -> None:
        parsed = urlsplit(self.api_url)
        if parsed.scheme not in {"http", "https"} or not parsed.hostname:
            raise ValueError("FORGEJO_URL must be an absolute HTTP(S) URL")
        if parsed.username or parsed.password or parsed.query or parsed.fragment:
            raise ValueError("Credentials and query parameters are not allowed in FORGEJO_URL")
        if parsed.scheme == "http" and parsed.hostname not in {"127.0.0.1", "localhost", "::1", "forgejo", "host.docker.internal"}:
            raise ValueError("HTTP allowed only to trusted local endpoints; use HTTPS for remote Forgejo")
        if not self.token:
            raise ValueError("FORGEJO_TOKEN must be configured (create a scoped token in Forgejo)")
        if self.write_enabled and not self.allowed_repos:
            raise ValueError("MYGIT_ALLOWED_REPOS must be set when write mode is enabled")
        _name(self.apk_workflow, "workflow name")


class ForgejoClient:
    def __init__(self, settings: Settings, transport: httpx.BaseTransport | None = None):
        self.settings = settings
        settings.validate()
        self.transport = transport

    def _request(self, method: str, route: str, *, params=None, body=None):
        with httpx.Client(
            base_url=self.settings.api_url + "/api/v1/",
            headers={"Authorization": "token " + self.settings.token, "Accept": "application/json"},
            timeout=httpx.Timeout(20.0),
            follow_redirects=False,
            transport=self.transport,
        ) as client:
            try:
                response = client.request(method, route.lstrip("/"), params=params, json=body)
                response.raise_for_status()
            except httpx.HTTPStatusError as exc:
                status = exc.response.status_code
                if status == 404:
                    raise FileNotFoundError("Forgejo resource not found or not accessible") from exc
                if status in (401, 403):
                    raise PermissionError("Forgejo access denied; verify token scopes and repository permissions") from exc
                if status == 409:
                    raise RuntimeError("Forgejo conflict; refresh repository state before retry") from exc
                raise RuntimeError(f"Forgejo API request failed (HTTP {status})") from exc
            except httpx.RequestError as exc:
                raise ConnectionError("Cannot reach Forgejo; is MyGit running?") from exc
            if response.status_code == 204 or not response.content:
                return {"status": response.status_code, "ok": True}
            try:
                return response.json()
            except ValueError:
                return {"status": response.status_code, "ok": True}

    @staticmethod
    def _repo_path(owner: str, repo: str) -> str:
        return f"repos/{quote(_name(owner, 'owner'), safe='')}/{quote(_name(repo, 'repo'), safe='')}"

    def _writable(self, owner: str, repo: str) -> None:
        if not self.settings.write_enabled:
            raise PermissionError("Write mode is off. Set MYGIT_WRITE_ENABLED=true on the server")
        if f"{owner}/{repo}" not in self.settings.allowed_repos:
            raise PermissionError("Repository not in MYGIT_ALLOWED_REPOS")

    def repositories(self, page: int = 1, limit: int = 20):
        return self._request("GET", "user/repos", params={"page": max(1, min(page, 100)), "limit": max(1, min(limit, 50))})

    def repository(self, owner: str, repo: str):
        return self._request("GET", self._repo_path(owner, repo))

    def branches(self, owner: str, repo: str):
        return self._request("GET", self._repo_path(owner, repo) + "/branches", params={"limit": 50})

    def commits(self, owner: str, repo: str, branch: str = "", limit: int = 20):
        p = {"limit": max(1, min(limit, 50))}
        if branch:
            p["sha"] = branch
        return self._request("GET", self._repo_path(owner, repo) + "/commits", params=p)

    def files(self, owner: str, repo: str, path: str = "", ref: str = ""):
        route = self._repo_path(owner, repo) + "/contents"
        if path:
            route += "/" + quote(_path(path), safe="/")
        params = {"ref": ref} if ref else None
        result = self._request("GET", route, params=params)
        if isinstance(result, list):
            return [{"name": o.get("name"), "path": o.get("path"), "type": o.get("type"), "size": o.get("size")} for o in result]
        return {k: result.get(k) for k in ("name", "path", "type", "size", "sha")}

    def file(self, owner: str, repo: str, path: str, ref: str = ""):
        route = self._repo_path(owner, repo) + "/contents/" + quote(_path(path), safe="/")
        obj = self._request("GET", route, params={"ref": ref} if ref else None)
        if not isinstance(obj, dict) or obj.get("type") != "file":
            raise ValueError("Path is not a regular file")
        if int(obj.get("size") or 0) > _MAX_READ:
            raise ValueError(f"File exceeds the {_MAX_READ}-byte read limit")
        if obj.get("encoding") != "base64":
            raise ValueError("Unsupported file encoding")
        raw = base64.b64decode(obj.get("content", ""), validate=False)
        if len(raw) > _MAX_READ:
            raise ValueError("File content is too large")
        try:
            content = raw.decode("utf-8")
        except UnicodeDecodeError as exc:
            raise ValueError("This tool supports UTF-8 text only") from exc
        return {"path": obj.get("path", path), "sha": obj.get("sha"), "ref": ref, "content": content, "untrusted_repository_text": True}

    def actions(self, owner: str, repo: str, limit: int = 20):
        return self._request("GET", self._repo_path(owner, repo) + "/actions/runs", params={"limit": max(1, min(limit, 50))})

    def create_repo(self, name: str, description: str = ""):
        if not self.settings.write_enabled:
            raise PermissionError("Write mode is disabled")
        _name(name, "repository name")
        if len(description) > 1000:
            raise ValueError("Description is too long")
        return self._request("POST", "user/repos", body={"name": name, "description": description, "private": True, "auto_init": True})

    def create_branch(self, owner: str, repo: str, new_branch: str, from_branch: str = "main"):
        self._writable(owner, repo)
        _branch(new_branch)
        if not from_branch or len(from_branch) > 150:
            raise ValueError("Invalid source branch")
        return self._request("POST", self._repo_path(owner, repo) + "/branches",
                             body={"new_branch_name": new_branch, "old_branch_name": from_branch})

    def commit_text(self, owner: str, repo: str, branch: str, path: str,
                    content: str, message: str, expected_sha: str = ""):
        self._writable(owner, repo)
        _branch(branch)
        filename = _safe_write_path(path)
        if len(content.encode("utf-8")) > _MAX_CONTENT:
            raise ValueError(f"File is larger than {_MAX_CONTENT} bytes")
        if len(message) < 5 or len(message) > 200:
            raise ValueError("Commit message must have 5-200 characters")
        url = self._repo_path(owner, repo) + "/contents/" + quote(filename, safe="/")
        try:
            current = self._request("GET", url, params={"ref": branch})
        except FileNotFoundError:
            current = None
        actual_sha = (current or {}).get("sha", "")
        if actual_sha and (not expected_sha or expected_sha != actual_sha):
            raise RuntimeError("File exists: first read it and provide its current expected_sha")
        if not actual_sha and expected_sha:
            raise RuntimeError("File does not exist; expected_sha must be empty")
        payload = {"branch": branch, "message": message, "content": base64.b64encode(content.encode("utf-8")).decode("ascii")}
        if actual_sha:
            payload["sha"] = actual_sha
        return self._request("PUT" if actual_sha else "POST", url, body=payload)

    def build_apk(self, owner: str, repo: str, branch: str):
        self._writable(owner, repo)
        _branch(branch)
        workflow = quote(self.settings.apk_workflow, safe="")
        route = self._repo_path(owner, repo) + "/actions/workflows/" + workflow + "/dispatches"
        return self._request("POST", route, body={"ref": branch, "inputs": {}})
