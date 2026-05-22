"""Minimal MCP Streamable-HTTP client for pytest use.

One instance corresponds to one MCP session. The client sends JSON-RPC over POST,
negotiating a session id via `Mcp-Session-Id`, and parses the SSE `data: ` frames
that the server returns.
"""
from __future__ import annotations

import json
import urllib.error
import urllib.request
from typing import Any


class MCPClient:
    def __init__(self, endpoint: str, bearer: str | None = "pytest-default-tenant"):
        """bearer=None / empty => no Authorization header is sent (anonymous tenant)."""
        self.endpoint = endpoint
        self.bearer = bearer
        self.session_id: str | None = None
        self._id = 0

    def _next_id(self) -> int:
        self._id += 1
        return self._id

    def _post(self, payload: dict[str, Any]) -> tuple[dict[str, Any], str | None]:
        data = json.dumps(payload).encode()
        headers = {
            "Content-Type": "application/json",
            "Accept": "application/json, text/event-stream",
        }
        if self.bearer:
            headers["Authorization"] = f"Bearer {self.bearer}"
        if self.session_id:
            headers["Mcp-Session-Id"] = self.session_id
        req = urllib.request.Request(self.endpoint, data=data, method="POST", headers=headers)
        try:
            resp = urllib.request.urlopen(req, timeout=30)
        except urllib.error.HTTPError as e:
            body = e.read().decode(errors="replace")
            raise RuntimeError(f"HTTP {e.code}: {body}") from None
        body = resp.read().decode()
        sid = resp.headers.get("Mcp-Session-Id")
        for line in body.splitlines():
            if line.startswith("data: "):
                return json.loads(line[6:]), sid
        if body.strip():
            try:
                return json.loads(body), sid
            except json.JSONDecodeError:
                pass
        return {"raw": body}, sid

    def initialize(self) -> None:
        resp, sid = self._post({
            "jsonrpc": "2.0",
            "id": self._next_id(),
            "method": "initialize",
            "params": {
                "protocolVersion": "2024-11-05",
                "capabilities": {},
                "clientInfo": {"name": "dnspymcp-pytest", "version": "0.1"},
            },
        })
        if sid is None:
            raise RuntimeError(f"no session id returned from initialize: {resp}")
        self.session_id = sid
        self._post({"jsonrpc": "2.0", "method": "notifications/initialized"})

    def list_tools(self) -> list[dict[str, Any]]:
        resp, _ = self._post({
            "jsonrpc": "2.0",
            "id": self._next_id(),
            "method": "tools/list",
            "params": {},
        })
        return resp.get("result", {}).get("tools", [])

    def call(self, name: str, arguments: dict[str, Any] | None = None) -> dict[str, Any]:
        """Call a tool. Returns dict with keys: ok(bool), text(str), raw(dict)."""
        resp, _ = self._post({
            "jsonrpc": "2.0",
            "id": self._next_id(),
            "method": "tools/call",
            "params": {"name": name, "arguments": arguments or {}},
        })
        if "error" in resp:
            return {"ok": False, "text": json.dumps(resp["error"]), "raw": resp}
        result = resp.get("result", {})
        is_error = bool(result.get("isError"))
        text = "".join(
            c.get("text", "")
            for c in result.get("content", [])
            if c.get("type") == "text"
        )
        return {"ok": not is_error, "text": text, "raw": resp}

    def call_json(self, name: str, arguments: dict[str, Any] | None = None) -> Any:
        """Call a tool and parse its text payload as JSON. Raises on tool error."""
        r = self.call(name, arguments)
        if not r["ok"]:
            raise AssertionError(f"{name} failed: {r['text']}")
        if not r["text"]:
            return None
        try:
            return json.loads(r["text"])
        except json.JSONDecodeError:
            return r["text"]
