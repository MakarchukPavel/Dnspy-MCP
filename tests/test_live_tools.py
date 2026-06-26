"""Tests for [LIVE] tools — agent + attached target required.

The `live_agent` fixture spawns dnspymcpagent.exe already attached to the test
target via `--attach <pid>`. These tests talk to the agent through the MCP
server's live_* proxies.

Coverage: session plumbing, multi-agent registry, heap walker (ClrMD passive
path). ICorDebug-driven thread/module/bp/step tools are validated manually
against real targets; they rely on the dnSpy DnDebugger attach-time callback
burst which is still flaky in this harness and will be covered once the
bootstrap issue is fixed.
"""
from __future__ import annotations

import json

import pytest


def test_agent_connect(live_agent):
    r = live_agent.call_json("debug_session_info")
    assert r is not None
    assert r["current"]
    # debugState is the merged session info — either pid or dump path should be populated
    st = r.get("debugState") or {}
    assert st.get("pid") or st.get("dumpPath")


def test_agent_list_current_switch(live_agent):
    lst = live_agent.call_json("debug_session_list")
    assert isinstance(lst, list) and len(lst) >= 1
    active = [a for a in lst if a["active"]]
    assert len(active) == 1
    cur = live_agent.call_json("debug_session_info")
    assert cur["current"] == active[0]["name"]

    # switching to the same slot should succeed
    sw = live_agent.call_json("debug_session_switch", {"name": active[0]["name"]})
    assert sw["current"] == active[0]["name"]


def test_agent_connect_is_idempotent(live_agent):
    # connecting the already-active session again should succeed and stay active
    lst = live_agent.call_json("debug_session_list")
    slot = next(a for a in lst if a["active"])
    r = live_agent.call_json("debug_session_connect", {
        "host": slot["host"], "port": slot["port"], "name": slot["name"]})
    assert r["connected"] and r["active"] == slot["name"]


def _items(env):
    """Unwrap pagination envelope -> items list. Asserts envelope shape."""
    assert isinstance(env, dict), f"expected envelope, got {type(env).__name__}"
    for k in ("total", "offset", "returned", "truncated", "items"):
        assert k in env, f"missing envelope key: {k}"
    assert isinstance(env["items"], list)
    return env["items"]


def test_agent_list_methods(live_agent):
    env = live_agent.call_json("debug_list_methods")
    items = _items(env)
    assert len(items) > 5
    names = {m.get("method") or m.get("name") for m in items}
    assert any("session." in (n or "") for n in names)


def test_agent_list_methods_paged(live_agent):
    """offset/max should slice + set truncated/nextOffset correctly."""
    full = _items(live_agent.call_json("debug_list_methods", {"max": 500}))
    assert len(full) >= 3, "need at least 3 methods to test paging"
    page1 = live_agent.call_json("debug_list_methods", {"offset": 0, "max": 2})
    assert page1["total"] == len(full)
    assert page1["returned"] == 2
    assert page1["truncated"] is True
    assert page1["nextOffset"] == 2
    page2 = live_agent.call_json("debug_list_methods", {"offset": 2, "max": 2})
    assert page2["offset"] == 2
    # both pages disjoint
    page1_names = {m.get("method") or m.get("name") for m in page1["items"]}
    page2_names = {m.get("method") or m.get("name") for m in page2["items"]}
    assert page1_names.isdisjoint(page2_names)


def test_list_dotnet_processes(live_agent):
    env = live_agent.call_json("debug_list_dotnet_processes")
    items = _items(env)
    assert isinstance(items, list)


def test_agent_current_contains_debug_state(live_agent, testtarget_pid):
    """agent_current must surface the debug state (pid etc.) of the active
    agent's target — no separate session_info tool needed."""
    r = live_agent.call_json("debug_session_info")
    assert r["connected"] is True
    st = r["debugState"]
    assert st is not None
    assert st.get("pid") == testtarget_pid


def test_heap_stats(live_agent):
    stats = _items(live_agent.call_json("debug_heap_stats", {"top": 10}))
    assert len(stats) >= 1
    # field name from agent is `type`; just assert rows are well-formed
    for row in stats:
        assert "type" in row and "count" in row and "totalSize" in row


def test_heap_find_widget(live_agent):
    rows = _items(live_agent.call_json("debug_heap_find_instances", {"typeName": "Widget", "max": 8}))
    assert len(rows) >= 1


def test_heap_read_widget_object(live_agent):
    rows = _items(live_agent.call_json("debug_heap_find_instances", {"typeName": "Widget", "max": 1}))
    assert rows, "no Widget instance on heap"
    addr = rows[0]["address"] if isinstance(rows[0], dict) else rows[0]
    obj = live_agent.call_json("debug_heap_read_object", {"address": str(int(addr)), "maxFields": 16})
    assert obj is not None


def test_heap_read_object_decodes_struct_fields(live_agent):
    """Widget has three value-type fields that previously rendered as the
    opaque "<Struct>" placeholder. The struct-decoder must now resolve:
      - CreatedAt (System.DateTime) -> {kind:"DateTime", value: ISO-8601}
      - Id        (System.Guid)     -> {kind:"Guid", value: guid text}
      - Kind      (WidgetKind enum) -> {kind:"enum", name: member}"""
    rows = _items(live_agent.call_json("debug_heap_find_instances", {"typeName": "Widget", "max": 64}))
    # "Widget" also substring-matches List<Widget>; keep only actual Widget instances.
    widgets = [r for r in rows if (r.get("type") or "").endswith(".Widget")]
    assert widgets, f"no concrete Widget instance on heap: {rows}"
    addr = widgets[0]["address"]
    obj = live_agent.call_json("debug_heap_read_object", {"address": str(int(addr)), "maxFields": 32})
    by_type = {(f.get("typeName") or ""): f for f in obj["fields"]}

    dt = by_type.get("System.DateTime")
    assert dt and isinstance(dt["value"], dict) and dt["value"].get("kind") == "DateTime", f"DateTime not decoded: {obj['fields']}"
    assert "T" in str(dt["value"].get("value")), f"unexpected DateTime rendering: {dt}"

    guid = by_type.get("System.Guid")
    assert guid and isinstance(guid["value"], dict) and guid["value"].get("kind") == "Guid", f"Guid not decoded: {obj['fields']}"
    # Guid.ToString() is 8-4-4-4-12 hex -> 36 chars with 4 dashes.
    assert str(guid["value"].get("value")).count("-") == 4, f"unexpected Guid rendering: {guid}"

    enum_fields = [f for f in obj["fields"] if isinstance(f.get("value"), dict) and f["value"].get("kind") == "enum"]
    assert enum_fields, f"WidgetKind enum field not decoded: {obj['fields']}"
    ev = enum_fields[0]["value"]
    assert ev.get("name") in {"Unknown", "Gadget", "Gizmo", "Doohickey"}, f"enum name not mapped: {ev}"


def test_heap_static_field(live_agent, mcp):
    """debug_heap_static_field reads a type's static fields (the entry point into
    singletons). Program has TickCounter (int) and StateLabel (string). Skips if
    the host predates the tool."""
    if not any(t.get("name") == "debug_heap_static_field" for t in mcp.list_tools()):
        pytest.skip("debug_heap_static_field not in this MCP host build (rebuild dist via builder.ps1)")
    T = "DnSpyMcp.TestTarget.Program"
    tc = live_agent.call_json("debug_heap_static_field", {"typeName": T, "fieldName": "TickCounter"})
    assert tc["fieldType"] == "System.Int32" and tc["initialized"], tc
    assert tc["value"]["kind"] == "primitive" and isinstance(tc["value"]["value"], int), tc
    sl = live_agent.call_json("debug_heap_static_field", {"typeName": T, "fieldName": "StateLabel"})
    assert sl["value"]["kind"] == "string", sl
    aw = live_agent.call_json("debug_heap_static_field", {"typeName": T, "fieldName": "AliveWidgets"})
    assert aw["value"]["kind"] == "object" and "List" in (aw["value"].get("type") or ""), aw
    # unknown field -> structured error
    assert not live_agent.call("debug_heap_static_field", {"typeName": T, "fieldName": "Nope"})["ok"]


def test_heap_read_collection(live_agent, mcp):
    """debug_heap_read_collection decodes a List<T> as elements and a
    Dictionary<K,V> as {key,value} pairs. Program.AliveWidgets (List<Widget>)
    and Program.Counters (Dictionary<string,int>). Skips if host predates the tool."""
    if not any(t.get("name") == "debug_heap_read_collection" for t in mcp.list_tools()):
        pytest.skip("debug_heap_read_collection not in this MCP host build (rebuild dist via builder.ps1)")
    T = "DnSpyMcp.TestTarget.Program"

    lst = live_agent.call_json("debug_heap_static_field", {"typeName": T, "fieldName": "AliveWidgets"})["value"]
    addr = int(lst["address"], 16)
    coll = live_agent.call_json("debug_heap_read_collection", {"address": str(addr), "count": 3})
    assert coll["kind"] == "list" and coll["count"] == 10 and coll["returned"] == 3 and coll["truncated"], coll
    assert coll["items"][0]["value"]["type"].endswith(".Widget"), coll

    dic = live_agent.call_json("debug_heap_static_field", {"typeName": T, "fieldName": "Counters"})["value"]
    daddr = int(dic["address"], 16)
    d = live_agent.call_json("debug_heap_read_collection", {"address": str(daddr)})
    assert d["kind"] == "dictionary" and d["count"] == 3, d
    kv = {e["key"]["value"]: e["value"] for e in d["entries"]}
    assert kv == {"alpha": 1, "beta": 2, "gamma": 3}, d


def test_heap_read_string(live_agent):
    strs = _items(live_agent.call_json("debug_heap_find_instances", {"typeName": "System.String", "max": 1}))
    if not strs:
        pytest.skip("no strings on heap yet")
    addr = strs[0]["address"] if isinstance(strs[0], dict) else strs[0]
    s = live_agent.call_json("debug_heap_read_string", {"address": str(int(addr))})
    assert s is not None


def test_pause_and_list_threads(live_agent):
    live_agent.call_json("debug_pause")
    live_agent.call_json("debug_wait_paused", {"timeoutMs": 3000})
    threads = _items(live_agent.call_json("debug_thread_list"))
    assert len(threads) >= 1
    live_agent.call_json("debug_go")


def test_list_modules(live_agent):
    mods = _items(live_agent.call_json("debug_list_modules"))
    assert len(mods) >= 1
    assert any("dnspymcptest" in (m.get("path") or m.get("name") or "").lower() for m in mods)
    # Default schema is the slim view: shortName + name + address only.
    sample = mods[0]
    assert set(sample.keys()) == {"shortName", "name", "address"}, f"unexpected slim schema: {sample}"


def test_list_modules_verbose(live_agent):
    mods = _items(live_agent.call_json("debug_list_modules", {"verbose": True}))
    assert len(mods) >= 1
    sample = mods[0]
    # Verbose schema: slim fields plus 5 extras (appDomain/assembly/size/isDynamic/isInMemory).
    expected = {"shortName", "name", "address", "appDomain", "assembly", "size", "isDynamic", "isInMemory"}
    assert set(sample.keys()) == expected, f"unexpected verbose schema: {sample}"


def test_bp_set_by_name_and_list(live_agent):
    r = live_agent.call_json("debug_bp_set_by_name", {
        "modulePath": "dnspymcptest",
        "typeFullName": "DnSpyMcp.TestTarget.Program",
        "methodName": "Add",
    })
    assert r is not None
    bps = _items(live_agent.call_json("debug_bp_list"))
    assert len(bps) >= 1


def test_wait_paused_reports_bp_hit(live_agent):
    """When a registered breakpoint triggers the pause, debug_wait_paused
    must return bpHit carrying the matching registry id/description/token.
    debug_session_info should mirror it while still paused.

    Reuses the BP set by test_bp_set_by_name_and_list (Add). We don't clean
    up afterwards so test_step_in_out below stays paused as it expects.
    """
    # Locate the Add breakpoint registered by an earlier test.
    bps = _items(live_agent.call_json("debug_bp_list"))
    add_bps = [b for b in bps if "Add" in (b.get("description") or "")]
    assert add_bps, f"no Add breakpoint found in {bps}; test ordering changed?"
    expected_ids = {b["id"] for b in add_bps}

    # Add() runs every loop tick (every 500ms in dnspymcptest) — a generous
    # 5s timeout absorbs the warmup if the target is between ticks.
    r = live_agent.call_json("debug_wait_paused", {"timeoutMs": 5000})
    assert r["state"] == "Paused"
    hit = r.get("bpHit")
    assert hit is not None, f"wait_paused returned no bpHit; full: {r}"
    assert hit["count"] >= 1
    first = hit["hits"][0]
    assert first["id"] in expected_ids, f"expected one of {expected_ids}, got hit={first}"
    assert "Add" in (first.get("description") or "")
    assert first.get("ilOffset") == 0

    # session.info should mirror the bpHit while still paused.
    # debug_session_info nests the agent payload under `debugState`.
    info = live_agent.call_json("debug_session_info")
    ds = info.get("debugState") or {}
    assert ds.get("bpHit") is not None, "session.info missed the BP hit"
    assert ds["bpHit"]["hits"][0]["id"] in expected_ids


def test_conditional_bp_count(live_agent):
    """Setting a BP with `count >= N` must NOT pause until the Nth hit.
    HitCount in bp.list reflects every callback, including ones that didn't
    pause; the first wait_paused after registration should report bpHit
    with hitCount >= N."""
    # Clean any leftover BPs so the count starts at 0.
    for old in _items(live_agent.call_json("debug_bp_list")):
        live_agent.call_json("debug_bp_delete", {"id": old["id"]})
    live_agent.call_json("debug_go")

    bp = live_agent.call_json("debug_bp_set_by_name", {
        "modulePath": "dnspymcptest",
        "typeFullName": "DnSpyMcp.TestTarget.Program",
        "methodName": "Add",
        "condition": "count >= 5",
    })
    bp_id = bp["id"]
    assert bp["condition"] == "count >= 5"
    assert bp["hitCount"] == 0
    try:
        # Each Add() call ticks every 500ms. count>=5 means we pause on the
        # 5th hit — generous timeout.
        r = live_agent.call_json("debug_wait_paused", {"timeoutMs": 6000})
        assert r["state"] == "Paused"
        hit = r.get("bpHit") or {}
        # The first hit that triggered the pause should be the 5th — the
        # actual count may be slightly higher if the dispatcher fired again
        # before our wait_paused returned, but never less than 5.
        bps = _items(live_agent.call_json("debug_bp_list"))
        ours = next(b for b in bps if b["id"] == bp_id)
        assert ours["hitCount"] >= 5, f"expected hitCount>=5, got {ours}"
    finally:
        live_agent.call_json("debug_bp_delete", {"id": bp_id})
        live_agent.call_json("debug_go")


def test_frame_locals_arguments(live_agent):
    """Pause inside Add() and read its arguments. The two int parameters
    a/b must come back as primitive Int32s with concrete values."""
    for old in _items(live_agent.call_json("debug_bp_list")):
        live_agent.call_json("debug_bp_delete", {"id": old["id"]})
    live_agent.call_json("debug_go")

    bp = live_agent.call_json("debug_bp_set_by_name", {
        "modulePath": "dnspymcptest",
        "typeFullName": "DnSpyMcp.TestTarget.Program",
        "methodName": "Add",
    })
    bp_id = bp["id"]
    try:
        wait = live_agent.call_json("debug_wait_paused", {"timeoutMs": 5000})
        assert wait["state"] == "Paused"

        # Add(int a, int b) — args at indices 0 and 1 (no `this`, it's static).
        args = live_agent.call_json("debug_frame_arguments", {"frameIndex": 0})
        assert args["count"] >= 2, f"expected >=2 args, got {args}"
        kinds = {a["value"].get("kind") for a in args["items"][:2]}
        # Both should be primitives (Int32).
        assert "primitive" in kinds, f"expected primitive args, got {args}"

        # Locals — Add()'s body is just `return a + b`, may or may not have
        # locals depending on optimization. Smoke: the call returns count>=0.
        locals_ = live_agent.call_json("debug_frame_locals", {"frameIndex": 0})
        assert locals_["count"] >= 0
    finally:
        live_agent.call_json("debug_bp_delete", {"id": bp_id})
        live_agent.call_json("debug_go")


def _drain_bps(live_agent):
    for old in _items(live_agent.call_json("debug_bp_list")):
        live_agent.call_json("debug_bp_delete", {"id": old["id"]})
    live_agent.call_json("debug_go")


def test_conditional_bp_value_arg_primitive(live_agent):
    """Value condition on a bare primitive argument: `arg0 == N`. We learn the
    current Add() arg0 while paused, arm `arg0 == current+4`, then confirm the
    next pause lands exactly on that value."""
    _drain_bps(live_agent)
    plain = live_agent.call_json("debug_bp_set_by_name", {
        "modulePath": "dnspymcptest", "typeFullName": "DnSpyMcp.TestTarget.Program",
        "methodName": "Add"})
    plain_id = plain["id"]
    r = live_agent.call_json("debug_wait_paused", {"timeoutMs": 5000})
    assert r["state"] == "Paused", r
    cur = live_agent.call_json("debug_frame_arguments", {"frameIndex": 0})["items"][0]["value"]["value"]
    target = int(cur) + 4

    cond = live_agent.call_json("debug_bp_set_by_name", {
        "modulePath": "dnspymcptest", "typeFullName": "DnSpyMcp.TestTarget.Program",
        "methodName": "Add", "condition": f"arg0 == {target}"})
    cond_id = cond["id"]
    assert cond["condition"] == f"arg0 == {target}"
    live_agent.call_json("debug_bp_delete", {"id": plain_id})
    live_agent.call_json("debug_go")
    try:
        r2 = live_agent.call_json("debug_wait_paused", {"timeoutMs": 6000})
        assert r2["state"] == "Paused", r2
        got = live_agent.call_json("debug_frame_arguments", {"frameIndex": 0})["items"][0]["value"]["value"]
        assert int(got) == target, f"expected arg0=={target}, got {got}"
    finally:
        live_agent.call_json("debug_bp_delete", {"id": cond_id})
        live_agent.call_json("debug_go")


def _arg0_object_fields(live_agent):
    args = live_agent.call_json("debug_frame_arguments", {"frameIndex": 0})
    a0 = args["items"][0]["value"]
    assert a0.get("kind") == "object", f"arg0 not an object: {a0}"
    obj = live_agent.call_json("debug_heap_read_object", {"address": str(int(a0["address"])), "maxFields": 32})
    return obj["fields"]


def test_conditional_bp_value_field_int(live_agent):
    """Field-path value condition `arg0.Value == 14` on Inspect(Widget). Only
    the widget whose Value is 14 should trigger the pause."""
    _drain_bps(live_agent)
    bp = live_agent.call_json("debug_bp_set_by_name", {
        "modulePath": "dnspymcptest", "typeFullName": "DnSpyMcp.TestTarget.Program",
        "methodName": "Inspect", "condition": "arg0.Value == 14"})
    bp_id = bp["id"]
    try:
        r = live_agent.call_json("debug_wait_paused", {"timeoutMs": 10000})
        assert r["state"] == "Paused", r
        fields = _arg0_object_fields(live_agent)
        val = next(f["value"] for f in fields if "Value" in f["name"])
        assert val == 14, f"condition fired on wrong widget: {fields}"
    finally:
        live_agent.call_json("debug_bp_delete", {"id": bp_id})
        live_agent.call_json("debug_go")


def test_conditional_bp_value_field_enum(live_agent):
    """Field-path enum condition `arg0.Kind == 'Gadget'` compares by member
    name. Only widgets whose Kind is Gadget should trigger the pause."""
    _drain_bps(live_agent)
    bp = live_agent.call_json("debug_bp_set_by_name", {
        "modulePath": "dnspymcptest", "typeFullName": "DnSpyMcp.TestTarget.Program",
        "methodName": "Inspect", "condition": "arg0.Kind == 'Gadget'"})
    bp_id = bp["id"]
    try:
        r = live_agent.call_json("debug_wait_paused", {"timeoutMs": 10000})
        assert r["state"] == "Paused", r
        fields = _arg0_object_fields(live_agent)
        kind = next(f["value"] for f in fields if "Kind" in f["name"])
        assert isinstance(kind, dict) and kind.get("name") == "Gadget", f"wrong enum match: {kind}"
    finally:
        live_agent.call_json("debug_bp_delete", {"id": bp_id})
        live_agent.call_json("debug_go")


def test_eval_expression_object_graph(live_agent, mcp):
    """debug_eval reads an object-graph path off the paused frame without
    running target code. Pauses in Inspect(Widget) and reads arg0 plus a
    string / int / enum / Guid / DateTime field. Skips when the running MCP
    host predates the tool (dist not rebuilt) — the agent side is covered by
    the standalone probe regardless."""
    if not any(t.get("name") == "debug_eval" for t in mcp.list_tools()):
        pytest.skip("debug_eval not in this MCP host build (rebuild dist via builder.ps1)")

    _drain_bps(live_agent)
    bp = live_agent.call_json("debug_bp_set_by_name", {
        "modulePath": "dnspymcptest", "typeFullName": "DnSpyMcp.TestTarget.Program",
        "methodName": "Inspect"})
    bp_id = bp["id"]
    try:
        r = live_agent.call_json("debug_wait_paused", {"timeoutMs": 6000})
        assert r["state"] == "Paused", r

        root = live_agent.call_json("debug_eval", {"expr": "arg0"})["value"]
        assert root.get("kind") == "object", root

        name = live_agent.call_json("debug_eval", {"expr": "arg0.Name"})["value"]
        assert name.get("kind") == "string" and str(name.get("value")).startswith("widget-"), name

        val = live_agent.call_json("debug_eval", {"expr": "arg0.Value"})["value"]
        assert val.get("kind") == "primitive" and isinstance(val.get("value"), int), val

        gid = live_agent.call_json("debug_eval", {"expr": "arg0.Id"})["value"]
        assert gid.get("kind") == "Guid", gid

        kind = live_agent.call_json("debug_eval", {"expr": "arg0.Kind"})["value"]
        # enum surfaces nested under the primitive wrapper
        enum_obj = kind.get("value") if isinstance(kind.get("value"), dict) else kind
        assert enum_obj.get("name") in {"Unknown", "Gadget", "Gizmo", "Doohickey"}, kind

        # unknown field -> structured error, not an exception
        miss = live_agent.call_json("debug_eval", {"expr": "arg0.Nope"})["value"]
        assert miss.get("kind") == "error", miss

        # method invocation is rejected at the tool boundary
        assert not live_agent.call("debug_eval", {"expr": "arg0.ToString()"})["ok"]
    finally:
        live_agent.call_json("debug_bp_delete", {"id": bp_id})
        live_agent.call_json("debug_go")


def test_eval_call_func_eval(live_agent, mcp):
    """debug_eval_call runs real func-eval on the paused receiver: a ToString
    override, a computed property getter, a 0-arg method, and an exception path.
    Skips when the running MCP host predates the tool (dist not rebuilt) — the
    agent side is covered by the standalone probe regardless."""
    if not any(t.get("name") == "debug_eval_call" for t in mcp.list_tools()):
        pytest.skip("debug_eval_call not in this MCP host build (rebuild dist via builder.ps1)")

    _drain_bps(live_agent)
    bp = live_agent.call_json("debug_bp_set_by_name", {
        "modulePath": "dnspymcptest", "typeFullName": "DnSpyMcp.TestTarget.Program",
        "methodName": "Inspect"})
    bp_id = bp["id"]
    try:
        r = live_agent.call_json("debug_wait_paused", {"timeoutMs": 6000})
        assert r["state"] == "Paused", r

        ts = live_agent.call_json("debug_eval_call", {"expr": "arg0.ToString()"})["value"]
        assert ts.get("kind") == "string" and ts["value"].startswith("Widget("), ts

        label = live_agent.call_json("debug_eval_call", {"expr": "arg0.Label"})["value"]
        assert label.get("kind") == "string" and "#" in label["value"], label

        doubled = live_agent.call_json("debug_eval_call", {"expr": "arg0.Doubled()"})["value"]
        assert doubled.get("kind") == "primitive" and isinstance(doubled.get("value"), int), doubled

        boom = live_agent.call_json("debug_eval_call", {"expr": "arg0.Boom()"})["value"]
        assert boom.get("kind") == "exception" and "InvalidOperation" in (boom.get("type") or ""), boom

        # unknown member -> structured tool error
        assert not live_agent.call("debug_eval_call", {"expr": "arg0.Nope()"})["ok"]
    finally:
        live_agent.call_json("debug_bp_delete", {"id": bp_id})
        live_agent.call_json("debug_go")


def test_eval_call_args_and_generics(live_agent, mcp):
    """func-eval v2: literal arguments + generic methods (the
    GetTypedColumnValue<T>(name) shape). Skips if the host predates the tool."""
    if not any(t.get("name") == "debug_eval_call" for t in mcp.list_tools()):
        pytest.skip("debug_eval_call not in this MCP host build (rebuild dist via builder.ps1)")

    _drain_bps(live_agent)
    bp = live_agent.call_json("debug_bp_set_by_name", {
        "modulePath": "dnspymcptest", "typeFullName": "DnSpyMcp.TestTarget.Program",
        "methodName": "Inspect"})
    bp_id = bp["id"]
    try:
        assert live_agent.call_json("debug_wait_paused", {"timeoutMs": 6000})["state"] == "Paused"

        tag = live_agent.call_json("debug_eval_call", {"expr": "arg0.Tag(\"hi\")"})["value"]
        assert tag.get("kind") == "string" and tag["value"].endswith(":hi"), tag

        plus = live_agent.call_json("debug_eval_call", {"expr": "arg0.Plus(10)"})["value"]
        assert plus.get("kind") == "primitive" and isinstance(plus.get("value"), int), plus

        tn = live_agent.call_json("debug_eval_call", {"expr": "arg0.TypeName<System.Guid>()"})["value"]
        assert tn.get("kind") == "string" and tn["value"] == "Guid", tn

        comb = live_agent.call_json("debug_eval_call", {"expr": "arg0.Combine<System.Int32>(\"x\")"})["value"]
        assert comb.get("kind") == "string" and comb["value"] == "x:Int32", comb

        # generic method whose RETURN is the type parameter T (value type) — must work
        conv = live_agent.call_json("debug_eval_call", {"expr": "arg0.Conv<System.Guid>(\"x\")"})["value"]
        assert conv.get("kind") in ("object", "Guid", "struct"), conv

        # wrong arity -> structured tool error
        assert not live_agent.call("debug_eval_call", {"expr": "arg0.Plus()"})["ok"]
    finally:
        live_agent.call_json("debug_bp_delete", {"id": bp_id})
        live_agent.call_json("debug_go")


def test_eval_call_overload_by_arg_type(live_agent, mcp):
    """func-eval v3: among same-arity overloads, the one whose parameter types
    fit the argument literals wins. Pick<T>(string) vs Pick<T>(Widget) and
    Classify(string) vs Classify(int) — mirrors Entity.GetTypedColumnValue<T>(string)
    vs <T>(EntitySchemaColumn). Skips if the host predates the tool."""
    if not any(t.get("name") == "debug_eval_call" for t in mcp.list_tools()):
        pytest.skip("debug_eval_call not in this MCP host build (rebuild dist via builder.ps1)")

    _drain_bps(live_agent)
    bp = live_agent.call_json("debug_bp_set_by_name", {
        "modulePath": "dnspymcptest", "typeFullName": "DnSpyMcp.TestTarget.Program",
        "methodName": "Inspect"})
    bp_id = bp["id"]
    try:
        assert live_agent.call_json("debug_wait_paused", {"timeoutMs": 6000})["state"] == "Paused"

        # generic, ambiguous: string arg must pick Pick<T>(string), not Pick<T>(Widget)
        pick = live_agent.call_json("debug_eval_call", {"expr": "arg0.Pick<System.Object>(\"hi\")"})["value"]
        assert pick.get("kind") == "string" and pick["value"] == "str:hi", pick

        # non-generic: string vs int overload picked by literal type
        cs = live_agent.call_json("debug_eval_call", {"expr": "arg0.Classify(\"x\")"})["value"]
        assert cs.get("value") == "S:x", cs
        ci = live_agent.call_json("debug_eval_call", {"expr": "arg0.Classify(7)"})["value"]
        assert ci.get("value") == "I:7", ci
    finally:
        live_agent.call_json("debug_bp_delete", {"id": bp_id})
        live_agent.call_json("debug_go")


def test_eval_call_object_args_and_multihop(live_agent, mcp):
    """func-eval v4: multi-hop receivers (arg0.Link.Doubled()) and object/
    reference arguments (arg0.Same(arg0), arg0.Pick<T>(arg0)). Skips if the
    host predates the tool."""
    if not any(t.get("name") == "debug_eval_call" for t in mcp.list_tools()):
        pytest.skip("debug_eval_call not in this MCP host build (rebuild dist via builder.ps1)")

    _drain_bps(live_agent)
    bp = live_agent.call_json("debug_bp_set_by_name", {
        "modulePath": "dnspymcptest", "typeFullName": "DnSpyMcp.TestTarget.Program",
        "methodName": "Inspect"})
    bp_id = bp["id"]
    try:
        assert live_agent.call_json("debug_wait_paused", {"timeoutMs": 6000})["state"] == "Paused"

        # multi-hop receiver: arg0.Link is a Widget; call a method on it
        hop = live_agent.call_json("debug_eval_call", {"expr": "arg0.Link.Doubled()"})["value"]
        assert hop.get("kind") == "primitive" and isinstance(hop.get("value"), int), hop

        # 2-hop receiver ending in a property getter
        nm = live_agent.call_json("debug_eval_call", {"expr": "arg0.Link.Link.Name"})["value"]
        assert nm.get("kind") == "string" and nm["value"].startswith("widget-"), nm

        # object argument (non-generic): pass arg0 itself
        same = live_agent.call_json("debug_eval_call", {"expr": "arg0.Same(arg0)"})["value"]
        assert same.get("value") == "same:yes", same

        # object argument selects the (Widget) overload over (string)
        pick = live_agent.call_json("debug_eval_call", {"expr": "arg0.Pick<System.Object>(arg0)"})["value"]
        assert pick.get("kind") == "string" and pick["value"].startswith("widget:"), pick
    finally:
        live_agent.call_json("debug_bp_delete", {"id": bp_id})
        live_agent.call_json("debug_go")


def test_jit_status_reports_funceval_readiness(live_agent, mcp):
    """debug_jit_status reports per-module func-eval readiness. The shared
    session attached to an ALREADY-running target, so its own module pre-existed
    -> loadedUnderDebugger should be False (func-eval there hits BAD_START_POINT).
    Read-only; doesn't disturb the session. Skips if the host predates the tool."""
    if not any(t.get("name") == "debug_jit_status" for t in mcp.list_tools()):
        pytest.skip("debug_jit_status not in this MCP host build (rebuild dist via builder.ps1)")
    r = live_agent.call_json("debug_jit_status", {"pattern": "dnspymcptest"})
    assert r.get("mode") == "live", r
    mods = r.get("modules") or []
    assert mods, f"dnspymcptest module not tracked: {r}"
    # attached to a pre-existing process -> not loaded under the debugger
    assert mods[0]["loadedUnderDebugger"] is False, mods


def test_launch_under_debugger(live_agent, mcp, testtarget_pid, testtarget_asm):
    """debug_launch starts the target UNDER the debugger (paused at entry), so
    every module loads with JIT optimization disabled and func-eval works. We
    launch a second target instance, run to a breakpoint, eval, then clean it up
    and restore the live attach. Skips when the host predates the tool."""
    if not any(t.get("name") == "debug_launch" for t in mcp.list_tools()):
        pytest.skip("debug_launch not in this MCP host build (rebuild dist via builder.ps1)")

    launched_pid = None
    try:
        r = live_agent.call_json("debug_launch", {"exePath": str(testtarget_asm)})
        assert r.get("launched") and r.get("pid"), r
        launched_pid = r["pid"]

        bp = live_agent.call_json("debug_bp_set_by_name", {
            "modulePath": "dnspymcptest", "typeFullName": "DnSpyMcp.TestTarget.Program",
            "methodName": "Inspect"})
        live_agent.call_json("debug_go")
        assert live_agent.call_json("debug_wait_paused", {"timeoutMs": 8000})["state"] == "Paused"

        ts = live_agent.call_json("debug_eval_call", {"expr": "arg0.ToString()"})["value"]
        assert ts.get("kind") == "string" and ts["value"].startswith("Widget("), ts
        live_agent.call_json("debug_bp_delete", {"id": bp["id"]})
        live_agent.call_json("debug_go")
    finally:
        live_agent.call_json("debug_pid_detach")
        if launched_pid:
            import subprocess
            subprocess.run(["taskkill", "/F", "/PID", str(launched_pid)], capture_output=True)
        # Restore the shared live session for subsequent tests.
        live_agent.call_json("debug_pid_attach", {"pid": testtarget_pid})


def test_load_dump_postmortem(live_agent, mcp, testtarget_pid):
    """debug_load_dump: snapshot the test target to a full .dmp, load it, and
    confirm passive heap walk + struct decoding work on the dump. Restores the
    live attach afterwards so later tests still have a live target. Skips when
    the host predates the tool or a dump can't be produced (permissions)."""
    import os
    import subprocess
    import time

    if not any(t.get("name") == "debug_load_dump" for t in mcp.list_tools()):
        pytest.skip("debug_load_dump not in this MCP host build (rebuild dist via builder.ps1)")

    dump = os.path.join(os.environ.get("TEMP", "."), "dnspymcp_pytest.dmp")
    if os.path.exists(dump):
        try: os.remove(dump)
        except OSError: pass
    subprocess.run(["rundll32.exe", r"C:\Windows\System32\comsvcs.dll,MiniDump",
                    str(testtarget_pid), dump, "full"], capture_output=True)
    for _ in range(40):
        if os.path.exists(dump) and os.path.getsize(dump) > 0: break
        time.sleep(0.25)
    if not (os.path.exists(dump) and os.path.getsize(dump) > 0):
        pytest.skip("could not create a dump (insufficient privileges?)")

    try:
        r = live_agent.call_json("debug_load_dump", {"path": dump})
        assert "dump loaded" in (r.get("description") or ""), r
        ds = live_agent.call_json("debug_session_info").get("debugState") or {}
        assert ds.get("dumpPath") == dump and not ds.get("isAttached"), ds

        rows = _items(live_agent.call_json("debug_heap_find_instances", {"typeName": "Widget", "max": 64}))
        widgets = [w for w in rows if (w.get("type") or "").endswith(".Widget")]
        assert widgets, f"no Widget in dump heap: {rows[:3]}"
        obj = live_agent.call_json("debug_heap_read_object", {"address": str(int(widgets[0]["address"])), "maxFields": 32})
        by_type = {(f.get("typeName") or ""): f for f in obj["fields"]}
        assert by_type.get("System.Guid", {}).get("value", {}).get("kind") == "Guid", obj["fields"]
        assert by_type.get("System.DateTime", {}).get("value", {}).get("kind") == "DateTime", obj["fields"]
    finally:
        # Restore the shared live session for subsequent tests.
        live_agent.call_json("debug_pid_attach", {"pid": testtarget_pid})


def test_conditional_bp_invalid_syntax(live_agent):
    r = live_agent.call("debug_bp_set_by_name", {
        "modulePath": "dnspymcptest",
        "typeFullName": "DnSpyMcp.TestTarget.Program",
        "methodName": "Add",
        "condition": "garbage>>10",
    })
    assert not r["ok"], "expected parse error for bad condition"
    assert "condition" in r["text"].lower() or "count" in r["text"].lower()


def test_step_in_out(live_agent):
    # Stop at a known managed-code site (Compute) so step_in/out have
    # meaningful targets — pausing on a random thread can land in
    # GC / kernel-call frames where step_out is undefined.
    for old in _items(live_agent.call_json("debug_bp_list")):
        live_agent.call_json("debug_bp_delete", {"id": old["id"]})
    live_agent.call_json("debug_go")
    bp = live_agent.call_json("debug_bp_set_by_name", {
        "modulePath": "dnspymcptest",
        "typeFullName": "DnSpyMcp.TestTarget.Program",
        "methodName": "Compute",
    })
    bp_id = bp["id"]
    try:
        live_agent.call_json("debug_wait_paused", {"timeoutMs": 5000})
        live_agent.call_json("debug_step_in", {"timeoutMs": 3000})
        live_agent.call_json("debug_wait_paused", {"timeoutMs": 3000})
        live_agent.call_json("debug_step_out", {"timeoutMs": 3000})
    finally:
        live_agent.call_json("debug_bp_delete", {"id": bp_id})
        live_agent.call_json("debug_go")


def test_memory_roundtrip(live_agent):
    # read-then-write-same-byte on the top-of-stack address for thread 0 — this
    # is always mapped and writable, so the roundtrip is a reliable smoke test.
    # ICorDebug requires a paused state for stack walking.
    live_agent.call_json("debug_pause")
    live_agent.call_json("debug_wait_paused", {"timeoutMs": 3000})
    try:
        threads = _items(live_agent.call_json("debug_thread_list"))
        assert threads, "no threads while paused"
        tid = threads[0]["uniqueId"]
        stk = _items(live_agent.call_json("debug_thread_stack", {"threadId": tid, "max": 1}))
        assert stk, "empty stack while paused"
        addr = stk[0].get("stackStart")
        assert addr, "no stackStart in frame"
        data = live_agent.call_json("debug_memory_read", {"address": str(int(addr)), "size": 1})
        live_agent.call_json("debug_memory_write", {"address": str(int(addr)), "hex": data["hex"]})
    finally:
        live_agent.call_json("debug_go")


def test_thread_stack_id_aliases(live_agent):
    # debug_thread_stack must accept uniqueId, osThreadId, and the legacy
    # threadId alias and produce the same stack for the same thread.
    live_agent.call_json("debug_pause")
    live_agent.call_json("debug_wait_paused", {"timeoutMs": 3000})
    try:
        threads = _items(live_agent.call_json("debug_thread_list"))
        assert threads, "no threads while paused"
        # pick the first thread that has a non-zero osThreadId so we can
        # round-trip both fields.
        sel = next((t for t in threads if t.get("osThreadId")), threads[0])
        u, o = sel["uniqueId"], sel["osThreadId"]

        s_unique = _items(live_agent.call_json("debug_thread_stack", {"uniqueId": u, "max": 1}))
        s_legacy = _items(live_agent.call_json("debug_thread_stack", {"threadId": u, "max": 1}))
        s_os = _items(live_agent.call_json("debug_thread_stack", {"osThreadId": o, "max": 1}))
        assert s_unique == s_legacy, "uniqueId vs legacy threadId disagree"
        assert s_unique == s_os, "uniqueId vs osThreadId disagree"
    finally:
        live_agent.call_json("debug_go")


def test_thread_stack_id_validation(live_agent):
    # Missing both ids must error; passing both must error.
    live_agent.call_json("debug_pause")
    live_agent.call_json("debug_wait_paused", {"timeoutMs": 3000})
    try:
        r = live_agent.call("debug_thread_stack", {"max": 1})
        assert not r["ok"], f"expected error when no id supplied, got {r}"
        r = live_agent.call("debug_thread_stack", {"uniqueId": 1, "osThreadId": 2, "max": 1})
        assert not r["ok"], f"expected error when both ids supplied, got {r}"
    finally:
        live_agent.call_json("debug_go")


# ---- attach / detach lifecycle (Phase 2 API) -----------------------------
# Run these LAST in the file — they cycle the debugger attachment and we want
# the earlier tests to use the stable fixture-managed attach.


def _debug_state(live_agent):
    """Helper: fetch the merged debug state from agent_current."""
    return live_agent.call_json("debug_session_info").get("debugState") or {}


def test_bp_id_counter_resets_after_detach(live_agent, testtarget_pid):
    """Every debug session must start with a fresh BP id space — `_nextId`
    is reset alongside the registry map in BreakpointRegistry.Clear() so
    re-attach (to the same or a different PID) doesn't leak the counter
    from the previous session. Within a session id stays monotonic
    (Remove() does NOT reset)."""
    # Clear any leftover BPs and force the counter forward by adding a few.
    for old in _items(live_agent.call_json("debug_bp_list")):
        live_agent.call_json("debug_bp_delete", {"id": old["id"]})
    bp1 = live_agent.call_json("debug_bp_set_by_name", {
        "modulePath": "dnspymcptest",
        "typeFullName": "DnSpyMcp.TestTarget.Program",
        "methodName": "Add",
    })
    bp2 = live_agent.call_json("debug_bp_set_by_name", {
        "modulePath": "dnspymcptest",
        "typeFullName": "DnSpyMcp.TestTarget.Program",
        "methodName": "Multiply",
    })
    assert bp2["id"] > bp1["id"], "ids must be monotonic within a session"

    # Cycle the debugger session.
    live_agent.call_json("debug_pid_detach")
    live_agent.call_json("debug_pid_attach", {"pid": testtarget_pid})

    # Fresh session: id counter starts back at 1.
    bp_fresh = live_agent.call_json("debug_bp_set_by_name", {
        "modulePath": "dnspymcptest",
        "typeFullName": "DnSpyMcp.TestTarget.Program",
        "methodName": "Add",
    })
    assert bp_fresh["id"] == 1, \
        f"counter not reset: got id={bp_fresh['id']} (expected 1)"

    # bp.list shows only this one.
    bps = _items(live_agent.call_json("debug_bp_list"))
    assert len(bps) == 1 and bps[0]["id"] == 1
    live_agent.call_json("debug_bp_delete", {"id": 1})


def test_detach_attach_cycle(live_agent, testtarget_pid):
    """Detach then re-attach to the same PID. Post-cycle session must report
    attached with matching pid; lastExit info should show the detach."""
    # Detach
    r = live_agent.call_json("debug_pid_detach")
    assert r["detached"] is True
    assert r.get("lastExitedPid") == testtarget_pid
    assert "detach" in (r.get("lastExitReason") or "").lower()

    # agent_current's debugState mirrors the same state
    st = _debug_state(live_agent)
    assert st.get("isAttached") is False
    assert st.get("pid") in (None, 0)   # serializer may omit or send 0
    assert st.get("lastExitedPid") == testtarget_pid

    # Re-attach
    r2 = live_agent.call_json("debug_pid_attach", {"pid": testtarget_pid})
    assert r2["attached"] is True
    assert r2["pid"] == testtarget_pid

    # Fresh attach clears lastExit history
    st2 = _debug_state(live_agent)
    assert st2.get("isAttached") is True
    assert st2.get("pid") == testtarget_pid
    assert st2.get("lastExitedPid") in (None, 0)
    assert not st2.get("lastExitReason")


def test_detach_is_idempotent(live_agent, testtarget_pid):
    """Calling detach twice in a row must not error. Second call reports
    detached=False (nothing to detach)."""
    live_agent.call_json("debug_pid_detach")
    r = live_agent.call_json("debug_pid_detach")
    assert r["detached"] is False  # nothing to detach the second time

    # Restore the attachment for any subsequent test
    live_agent.call_json("debug_pid_attach", {"pid": testtarget_pid})


def test_attach_to_nonexistent_pid_errors(live_agent, testtarget_pid):
    """Attach to a PID that doesn't exist must surface a clear error, not
    silently half-attach. The agent must NOT die (STA exception kills process
    is precisely what we guarded against in DebuggerSession.Attach)."""
    # Detach first so we're in a clean state to probe the error path
    live_agent.call_json("debug_pid_detach")

    bad_pid = 1234567  # virtually guaranteed not to exist
    r = live_agent.call("debug_pid_attach", {"pid": bad_pid})
    assert not r["ok"], f"expected error, got ok response: {r}"
    msg = (r.get("text") or r.get("error") or "").lower()
    # Accept any of the common flavors — ArgumentException / "not running" /
    # "not found" / HRESULT — what matters is there IS a message.
    assert msg, "error must carry a descriptive message"

    # Agent must still be alive and able to re-attach. If the STA thread died
    # this would throw ConnectionRefused.
    r2 = live_agent.call_json("debug_pid_attach", {"pid": testtarget_pid})
    assert r2["attached"] is True
    assert r2["pid"] == testtarget_pid


def test_attach_with_initial_breakpoints(live_agent, testtarget_pid):
    """debug_pid_attach with initialBreakpointsJson sets breakpoints atomically
    during the attach handshake, eliminating the race where the target runs
    a method before the caller's first bp.set RPC arrives.

    Each spec produces a row in initialBreakpoints[]; failures are reported
    inline, not raised as the whole attach failing.
    """
    import json as _json
    # Detach first to enter the attach path with a clean slate.
    live_agent.call_json("debug_pid_detach")

    specs = [
        {"kind": "by_name", "modulePath": "dnspymcptest",
         "typeFullName": "DnSpyMcp.TestTarget.Program", "methodName": "Multiply"},
        {"kind": "by_name", "modulePath": "dnspymcptest",
         "typeFullName": "DnSpyMcp.TestTarget.Program", "methodName": "DoesNotExist"},
    ]
    r = live_agent.call_json("debug_pid_attach", {
        "pid": testtarget_pid,
        "initialBreakpointsJson": _json.dumps(specs),
    })
    assert r["attached"] is True
    assert r["pid"] == testtarget_pid
    bps = r.get("initialBreakpoints")
    assert bps is not None and len(bps) == 2, f"expected 2 BP results, got {bps}"
    assert bps[0]["ok"] is True, f"first spec should have succeeded: {bps[0]}"
    assert "Multiply" in (bps[0]["bp"]["description"] or "")
    assert bps[1]["ok"] is False, f"DoesNotExist should fail: {bps[1]}"
    assert "not found" in (bps[1]["error"] or "").lower()

    # Sanity: BP shows up in debug_bp_list and Multiply gets hit promptly.
    bp_list = _items(live_agent.call_json("debug_bp_list"))
    assert any("Multiply" in (b.get("description") or "") for b in bp_list)
    wait = live_agent.call_json("debug_wait_paused", {"timeoutMs": 5000})
    assert wait["state"] == "Paused"
    hit = wait.get("bpHit") or {}
    descs = [h.get("description", "") for h in hit.get("hits", [])]
    assert any("Multiply" in d or "Add" in d for d in descs), f"unexpected hit: {hit}"

    # Clean up so subsequent attach tests start from a known state.
    live_agent.call_json("debug_go")


def test_attach_with_initial_breakpoint_condition(live_agent, testtarget_pid):
    """`condition` in an initialBreakpointsJson spec must be applied — earlier
    revisions silently dropped it because the SetByName/SetIl helpers in
    BreakpointHandlers passed `null` to CreateBreakpoint regardless of the
    spec's condition field. Regression: bp.list must surface the condition,
    and hitCount must increment as the predicate fires."""
    import json as _json
    live_agent.call_json("debug_pid_detach")
    spec = [
        {"kind": "by_name", "modulePath": "dnspymcptest",
         "typeFullName": "DnSpyMcp.TestTarget.Program", "methodName": "Multiply",
         "condition": "count >= 3"},
    ]
    r = live_agent.call_json("debug_pid_attach", {
        "pid": testtarget_pid,
        "initialBreakpointsJson": _json.dumps(spec),
    })
    assert r["initialBreakpoints"][0]["ok"] is True
    bp_id = r["initialBreakpoints"][0]["bp"]["id"]
    # The attach response should carry the condition string — proves
    # entry.Condition was wired through.
    assert r["initialBreakpoints"][0]["bp"].get("condition") == "count >= 3"

    try:
        # Wait for the BP to actually pause us; that requires count >= 3.
        wait = live_agent.call_json("debug_wait_paused", {"timeoutMs": 6000})
        assert wait["state"] == "Paused"
        # bp.list must reflect the condition AND a non-zero hitCount.
        bps = _items(live_agent.call_json("debug_bp_list"))
        ours = next(b for b in bps if b["id"] == bp_id)
        assert ours.get("condition") == "count >= 3", \
            f"condition lost on initialBreakpoints path: {ours}"
        assert ours.get("hitCount", 0) >= 3, \
            f"hitCount should be >= 3, got {ours.get('hitCount')}"
    finally:
        live_agent.call_json("debug_bp_delete", {"id": bp_id})
        live_agent.call_json("debug_go")


def test_attach_same_pid_is_idempotent(live_agent, testtarget_pid):
    """Calling attach with the already-attached pid should succeed (internally
    detaches + re-attaches, but from caller's view it just works)."""
    r = live_agent.call_json("debug_pid_attach", {"pid": testtarget_pid})
    assert r["attached"] is True
    assert r["pid"] == testtarget_pid
    # agent_current confirms
    st = _debug_state(live_agent)
    assert st.get("isAttached") and st.get("pid") == testtarget_pid
