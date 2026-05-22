"""Tests for per-Bearer tenant isolation.

Two MCPClient instances using different Authorization Bearer tokens must
each see their own opened binaries and connected debug agents — neither can
list, switch to, or xref into the other tenant's state. Requests without a
Bearer header collapse to a shared 'anonymous' tenant which is itself
isolated from every named tenant. Additionally a debug-agent host:port may
only be held by one tenant at a time.
"""
from __future__ import annotations

import pytest

from conftest import (
    AGENT_HOST,
    AGENT_PORT,
    MCP_ENDPOINT,
)
from mcp_client import MCPClient


@pytest.fixture(scope="module")
def tenant_a(mcp_proc):
    c = MCPClient(MCP_ENDPOINT, bearer="tenant-alpha")
    c.initialize()
    return c


@pytest.fixture(scope="module")
def tenant_b(mcp_proc):
    c = MCPClient(MCP_ENDPOINT, bearer="tenant-bravo")
    c.initialize()
    return c


@pytest.fixture(scope="module")
def anon(mcp_proc):
    """Anonymous client — no Authorization header sent at all."""
    c = MCPClient(MCP_ENDPOINT, bearer=None)
    c.initialize()
    return c


def test_anonymous_tenant_is_isolated(tenant_a, anon, testtarget_asm):
    """Anonymous (no/blank Bearer) callers form their own tenant — what
    tenant A opens does NOT leak into the anonymous view, and vice versa."""
    path = str(testtarget_asm)
    for c in (tenant_a, anon):
        c.call("reverse_close", {"asmPath": path})

    tenant_a.call_json("reverse_open", {"asmPath": path})
    try:
        anon_list = anon.call_json("reverse_list") or []
        assert not any(x["path"].lower() == path.lower() for x in anon_list), \
            "anonymous tenant must not see tenant A's opened asm"
    finally:
        tenant_a.call("reverse_close", {"asmPath": path})


def test_workspace_is_per_tenant(tenant_a, tenant_b, testtarget_asm):
    """Tenant A's reverse_open must not leak into Tenant B's reverse_list."""
    path = str(testtarget_asm)
    # Make sure neither tenant has it open going in (cleanup from a prior run).
    for c in (tenant_a, tenant_b):
        c.call("reverse_close", {"asmPath": path})

    tenant_a.call_json("reverse_open", {"asmPath": path})
    try:
        a_list = tenant_a.call_json("reverse_list")
        assert any(x["path"].lower() == path.lower() for x in a_list), \
            "tenant A should see its own open"

        b_list = tenant_b.call_json("reverse_list") or []
        assert not any(x["path"].lower() == path.lower() for x in b_list), \
            "tenant B must NOT see tenant A's opened asm"

        # Tenant B cannot reverse_switch into A's asm.
        r = tenant_b.call("reverse_switch", {"asmPath": path})
        assert not r["ok"], "tenant B switch into A's asm should fail"
        assert "not opened" in r["text"].lower()
    finally:
        tenant_a.call("reverse_close", {"asmPath": path})


def test_both_tenants_can_open_same_binary(tenant_a, tenant_b, testtarget_asm):
    """Allowing parallel opens of the same file is required — two RE agents
    should be able to inspect the same DLL independently."""
    path = str(testtarget_asm)
    for c in (tenant_a, tenant_b):
        c.call("reverse_close", {"asmPath": path})

    ra = tenant_a.call_json("reverse_open", {"asmPath": path})
    rb = tenant_b.call_json("reverse_open", {"asmPath": path})
    try:
        assert ra["path"].lower() == path.lower()
        assert rb["path"].lower() == path.lower()
        # Each tenant sees exactly its own one open.
        assert sum(1 for x in tenant_a.call_json("reverse_list") if x["path"].lower() == path.lower()) == 1
        assert sum(1 for x in tenant_b.call_json("reverse_list") if x["path"].lower() == path.lower()) == 1
    finally:
        tenant_a.call("reverse_close", {"asmPath": path})
        tenant_b.call("reverse_close", {"asmPath": path})


def test_debug_session_exclusivity(live_agent, tenant_a, tenant_b):
    """A debug agent at (host, port) can only be held by one tenant. live_agent
    holds 127.0.0.1:5555 under the default-bearer tenant; neither tenant_a nor
    tenant_b (different bearers) should be able to grab the same address, and
    neither must see the default tenant's sessions in debug_session_list."""
    # live_agent already holds AGENT_HOST:AGENT_PORT under the default bearer.
    # Both other tenants must be refused with a clear cross-tenant error.
    for c, name in ((tenant_a, "tenant-a-attempt"), (tenant_b, "tenant-b-attempt")):
        c.call("debug_session_disconnect", {"name": name})
        r = c.call("debug_session_connect", {
            "host": AGENT_HOST, "port": AGENT_PORT, "name": name})
        assert not r["ok"], f"{name} should be refused, got: {r}"
        assert "another" in r["text"].lower() or "already" in r["text"].lower(), \
            f"{name} got non-exclusivity error: {r['text']}"

    # And neither tenant should see the default tenant's "default" session.
    for c in (tenant_a, tenant_b):
        sessions = c.call_json("debug_session_list") or []
        assert not any(s["name"] == "default" for s in sessions), \
            f"tenant leaked the default tenant's session: {sessions}"


def test_reclaim_after_disconnect(tenant_a, tenant_b, agent_proc):
    """Once tenant A disconnects from a free port, tenant B should be able to
    pick it up. Uses a deliberately-non-listening port (5599) so we don't
    have to coordinate with live_agent's hold on the real agent port."""
    # No real agent listens on 5599 — but the reservation is enforced
    # BEFORE the TCP connect, so we can observe the lock behaviour without
    # spinning up a second agent.
    name = "handoff"
    tenant_a.call("debug_session_disconnect", {"name": name})
    tenant_b.call("debug_session_disconnect", {"name": name})

    # Tenant A reserves and immediately fails the TCP connect (no listener).
    # The reservation MUST be released on the connect-failure path; otherwise
    # tenant B's subsequent attempt would also be refused.
    ra = tenant_a.call("debug_session_connect", {
        "host": AGENT_HOST, "port": 5599, "name": name})
    assert not ra["ok"], "no listener on 5599 — connect should fail"

    # Now tenant B should be free to attempt 5599 (and also fail at TCP, but
    # NOT be refused by the exclusivity lock).
    rb = tenant_b.call("debug_session_connect", {
        "host": AGENT_HOST, "port": 5599, "name": name})
    assert not rb["ok"], "no listener on 5599 — connect should fail"
    # The error must be a TCP-level failure (not the cross-tenant lock).
    assert "another" not in rb["text"].lower() and "already" not in rb["text"].lower(), \
        f"reservation leaked from tenant A's failed connect: {rb['text']}"
