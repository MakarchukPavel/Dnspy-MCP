"""Tool-schema sanity tests.

Anthropic's API rejects an MCP tool catalog if any inputSchema property is
emitted as the JSON-Schema `true` literal (which means "anything goes") —
that pattern used to leak out of our address/token/offset params when they
were custom struct types with a JsonConverter. These tests pin the wire
format so a future refactor can't silently regress it.
"""
from __future__ import annotations


# Fields that must show up as `{"type": "string"}` in the tool's inputSchema —
# these are the address / token / file-offset params whose schema breakage
# previously took down the whole 87-tool DnspyMCP catalog on the API side.
STRING_PARAMS = {
    "debug_bp_set_il":          ["token", "offset"],
    "debug_disasm":             ["address"],
    "debug_heap_read_object":   ["address"],
    "debug_heap_read_string":   ["address"],
    "debug_memory_read":        ["address"],
    "debug_memory_read_int":    ["address"],
    "debug_memory_write":       ["address"],
    "reverse_clear_annotation": ["token"],
    "reverse_il_method_by_token": ["token"],
    "reverse_patch_bytes":      ["offset"],
    "reverse_patch_il_nop":     ["startOffset", "endOffset"],
    "reverse_rename_member":    ["token"],
    "reverse_set_comment":      ["token"],
}


def test_no_property_is_schema_true(mcp):
    """Every tool's inputSchema.properties.<field> must be an object — never
    `true`/`false`. The Anthropic API treats boolean schemas as invalid and
    will reject the whole catalog with no per-tool diagnostic."""
    tools = mcp.list_tools()
    offenders = []
    for t in tools:
        props = (t.get("inputSchema") or {}).get("properties") or {}
        for name, schema in props.items():
            if not isinstance(schema, dict):
                offenders.append(f"{t['name']}.{name} = {schema!r}")
    assert not offenders, "tools have non-object property schemas (Anthropic API will reject):\n  " + "\n  ".join(offenders)


def test_address_and_token_params_are_strings(mcp):
    """The specific fields we converted to wire-form string must declare
    `{"type": "string"}` so callers know what to send."""
    tools_by_name = {t["name"]: t for t in mcp.list_tools()}
    misses = []
    for tool_name, fields in STRING_PARAMS.items():
        t = tools_by_name.get(tool_name)
        if t is None:
            misses.append(f"tool not registered: {tool_name}")
            continue
        props = (t.get("inputSchema") or {}).get("properties") or {}
        for f in fields:
            sch = props.get(f)
            if not isinstance(sch, dict):
                misses.append(f"{tool_name}.{f} schema is {sch!r} (expected an object with type=string)")
                continue
            t_val = sch.get("type")
            ok = t_val == "string" or (isinstance(t_val, list) and "string" in t_val)
            if not ok:
                misses.append(f"{tool_name}.{f} type={t_val!r} (expected 'string')")
    assert not misses, "schema mismatches:\n  " + "\n  ".join(misses)
