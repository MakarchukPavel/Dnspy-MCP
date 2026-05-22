"""Tests for the flexible-number param handling.

Every address / token / file-offset param is wire-form `string` (the JSON
Schema for these fields is `{"type": "string"}` so the Anthropic API doesn't
reject the tool catalog the way a JSON-Schema `true` would). String values
accept optional base prefix: 0x/0X (hex), 0o/0O (octal), 0b/0B (binary), or
no prefix (decimal).

Underscores as digit separators are allowed; whitespace is trimmed.
"""
from __future__ import annotations

import pytest


@pytest.fixture
def open_target(mcp, testtarget_asm):
    """Open dnspymcptest for the cases that need a real metadata token."""
    path = str(testtarget_asm)
    mcp.call("reverse_close", {"asmPath": path})
    mcp.call_json("reverse_open", {"asmPath": path})
    yield path
    mcp.call("reverse_close", {"asmPath": path})


def _first_method_token(mcp, asm_path):
    """Pick a real MethodDef token from the test target (Add method)."""
    methods = mcp.call_json("reverse_list_methods", {
        "asmPath": asm_path,
        "typeFullName": "DnSpyMcp.TestTarget.Program",
    })
    add = next(m for m in methods["items"] if m["name"] == "Add")
    return add["token"]


def test_token_rejects_bare_json_number(mcp, open_target):
    """Wire format is string; sending a bare JSON number must fail the MCP
    SDK's parameter binding. This test exists to lock in the schema choice
    so we don't accidentally re-introduce a `true` (any-of) schema."""
    tok = _first_method_token(mcp, open_target)
    r = mcp.call("reverse_il_method_by_token", {"token": tok, "asmPath": open_target})
    assert not r["ok"], "bare JSON number should not bind to a string param"


def test_token_accepts_decimal_string(mcp, open_target):
    """Same value, sent as a quoted decimal string."""
    tok = _first_method_token(mcp, open_target)
    r = mcp.call_json("reverse_il_method_by_token", {"token": str(tok), "asmPath": open_target})
    assert r["total"] >= 1


def test_token_accepts_hex_string(mcp, open_target):
    """The natural form for a metadata token — '0x06000123' style."""
    tok = _first_method_token(mcp, open_target)
    hex_form = f"0x{tok:x}"
    r = mcp.call_json("reverse_il_method_by_token", {"token": hex_form, "asmPath": open_target})
    assert r["total"] >= 1


def test_token_accepts_hex_string_uppercase(mcp, open_target):
    tok = _first_method_token(mcp, open_target)
    r = mcp.call_json("reverse_il_method_by_token", {"token": f"0X{tok:X}", "asmPath": open_target})
    assert r["total"] >= 1


def test_token_accepts_binary_string(mcp, open_target):
    tok = _first_method_token(mcp, open_target)
    r = mcp.call_json("reverse_il_method_by_token", {"token": f"0b{tok:b}", "asmPath": open_target})
    assert r["total"] >= 1


def test_token_accepts_octal_string(mcp, open_target):
    tok = _first_method_token(mcp, open_target)
    r = mcp.call_json("reverse_il_method_by_token", {"token": f"0o{tok:o}", "asmPath": open_target})
    assert r["total"] >= 1


def test_token_accepts_underscore_separators(mcp, open_target):
    tok = _first_method_token(mcp, open_target)
    grouped = f"0x{tok:08x}"  # e.g. 0x06000001
    grouped = grouped[:6] + "_" + grouped[6:]
    r = mcp.call_json("reverse_il_method_by_token", {"token": grouped, "asmPath": open_target})
    assert r["total"] >= 1


def test_token_garbage_string_errors(mcp, open_target):
    r = mcp.call("reverse_il_method_by_token", {"token": "not-a-number", "asmPath": open_target})
    assert not r["ok"]
    # Error must name the field so the agent knows what to fix.
    assert "token" in r["text"].lower()


def test_token_negative_unsigned_errors(mcp, open_target):
    r = mcp.call("reverse_il_method_by_token", {"token": "-1", "asmPath": open_target})
    assert not r["ok"]
    assert "token" in r["text"].lower()


def test_token_overflows_uint32_errors(mcp, open_target):
    """Metadata tokens are uint32 — anything past 0xFFFFFFFF must be rejected."""
    r = mcp.call("reverse_il_method_by_token", {"token": "0x1_0000_0000", "asmPath": open_target})
    assert not r["ok"]
    assert "token" in r["text"].lower() and "overflow" in r["text"].lower()
