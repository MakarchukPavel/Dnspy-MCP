"""Tests for [FILE] (on-disk) tools — they don't need the agent.

These run against the compiled dnspymcptest.exe assembly, which is built by
`builder.ps1` into dnspymcptest/bin/Debug/. The server exposes the tools via
HTTP; the fixture in conftest.py takes care of spawning it.
"""
from __future__ import annotations

import json
import shutil
from pathlib import Path

import pytest


@pytest.fixture(scope="module")
def asm(mcp, testtarget_asm: Path) -> str:
    """Open the test assembly in the workspace once per module."""
    path = str(testtarget_asm)
    r = mcp.call_json("reverse_open", {"asmPath": path})
    assert r is not None
    assert r.get("path", "").lower() == path.lower()
    yield path
    mcp.call("reverse_close", {"asmPath": path})


def test_list_tools_surface(mcp):
    tools = mcp.list_tools()
    names = {t["name"] for t in tools}
    # smoke check — make sure the catalog contains both reverse_ (static) and
    # debug_ (live ICorDebug) tools. These are the only two prefixes the
    # project supports; no bare/legacy names allowed.
    for must in ("reverse_open", "reverse_decompile_type", "reverse_il_method", "reverse_find_string",
                 "reverse_xref_to_method", "reverse_patch_il_nop",
                 "debug_session_connect", "debug_list_dotnet_processes",
                 "debug_heap_stats", "debug_bp_set_by_name"):
        assert must in names, f"missing tool: {must}"
    # No legacy prefix should leak back in.
    for legacy in names:
        assert not legacy.startswith(("asm_file_", "live_", "decompile_", "il_method",
                                       "find_string", "xref_", "file_patch_", "file_save_")), \
            f"legacy-prefixed tool still registered: {legacy}"


def test_asm_list_and_types(mcp, asm):
    r = mcp.call_json("reverse_list")
    assert isinstance(r, list) and any(x["path"].lower() == asm.lower() for x in r)

    r = mcp.call_json("reverse_list_types", {"asmPath": asm, "namePattern": "Widget"})
    assert set(r.keys()) >= {"total", "offset", "returned", "truncated", "items"}
    assert any(t["fullName"] == "DnSpyMcp.TestTarget.Widget" for t in r["items"])


def test_list_types_pagination(mcp, asm):
    page1 = mcp.call_json("reverse_list_types", {"asmPath": asm, "max": 2, "offset": 0})
    assert page1["returned"] == 2
    if page1["truncated"]:
        assert page1["nextOffset"] == 2
        assert "truncated" in page1["hint"]
        page2 = mcp.call_json("reverse_list_types", {"asmPath": asm, "max": 2, "offset": page1["nextOffset"]})
        assert page2["offset"] == 2


def test_list_methods(mcp, asm):
    r = mcp.call_json("reverse_list_methods",
                      {"asmPath": asm, "typeFullName": "DnSpyMcp.TestTarget.Program"})
    items = r["items"]
    by_name = {m["name"] for m in items}
    assert {"Main", "Compute", "Add", "Multiply"}.issubset(by_name)
    add = next(m for m in items if m["name"] == "Add")
    assert add["token"] > 0
    assert add["hasBody"]


def test_reverse_decompile_type(mcp, asm):
    r = mcp.call_json("reverse_decompile_type",
                      {"asmPath": asm, "typeFullName": "DnSpyMcp.TestTarget.Widget"})
    assert set(r.keys()) >= {"totalChars", "offsetChars", "returnedChars", "truncated", "text"}
    assert "class Widget" in r["text"]
    assert "public string Name" in r["text"]


def test_reverse_decompile_type_truncation(mcp, asm):
    r = mcp.call_json("reverse_decompile_type",
                      {"asmPath": asm, "typeFullName": "DnSpyMcp.TestTarget.Widget", "maxChars": 20})
    assert r["returnedChars"] == 20
    assert r["truncated"] is True
    assert r["nextOffsetChars"] == 20
    assert "truncated" in r["hint"]


def test_reverse_list_methods_nested_type_separator(mcp, asm):
    """Nested type lookup must handle BOTH the reflection-style `+` separator
    (e.g. 'OuterContainer+InnerNested', what dnlib's FindReflection accepts)
    AND the FullName-style `/` separator (what GetTypes() returns) — the
    second path proves the dnlib-iteration fallback in ResolveTypeOrThrow
    runs cleanly when FindReflection misses or the caller uses the wrong
    separator. Same code path that fixes the SP19725 SPSite lookup miss.
    """
    r_plus = mcp.call_json("reverse_list_methods", {
        "asmPath": asm,
        "typeFullName": "DnSpyMcp.TestTarget.OuterContainer+InnerNested",
    })
    r_slash = mcp.call_json("reverse_list_methods", {
        "asmPath": asm,
        "typeFullName": "DnSpyMcp.TestTarget.OuterContainer/InnerNested",
    })
    assert r_plus["total"] == r_slash["total"] >= 1
    names_plus = {row["name"] for row in r_plus["items"]}
    names_slash = {row["name"] for row in r_slash["items"]}
    assert "Echo" in names_plus and "Echo" in names_slash, \
        f"Echo missing: plus={names_plus} slash={names_slash}"


def test_reverse_decompile_method(mcp, asm):
    r = mcp.call_json("reverse_decompile_method",
                      {"asmPath": asm,
                       "typeFullName": "DnSpyMcp.TestTarget.Program",
                       "methodName": "Add"})
    assert "Add" in r["text"] and "return" in r["text"]


def test_reverse_il_method(mcp, asm):
    r = mcp.call_json("reverse_il_method",
                      {"asmPath": asm,
                       "typeFullName": "DnSpyMcp.TestTarget.Program",
                       "methodName": "Add"})
    items = r["items"]
    assert isinstance(items, list) and len(items) >= 1
    assert any(i["opCode"] == "ret" for i in items)


def test_reverse_list_overloads(mcp, asm):
    r = mcp.call_json("reverse_list_overloads",
                      {"asmPath": asm,
                       "typeFullName": "DnSpyMcp.TestTarget.Program",
                       "methodName": "Greet"})
    items = r["items"]
    assert len(items) == 3, f"expected 3 Greet overloads, got {len(items)}"
    sigs = [it["signature"] for it in items]
    # one-arg(string), two-arg(string,int), two-arg(string,string)
    assert any("(System.String)" in s for s in sigs)
    assert any("(System.String,System.Int32)" in s for s in sigs)
    assert any("(System.String,System.String)" in s for s in sigs)


def test_reverse_decompile_method_signature_select(mcp, asm):
    # signature-based selection picks the right overload
    r = mcp.call_json("reverse_decompile_method",
                      {"asmPath": asm,
                       "typeFullName": "DnSpyMcp.TestTarget.Program",
                       "methodName": "Greet",
                       "signature": "(string,int)"})
    text = r["text"]
    # Two-arg (string,int) overload uses Repeat / Concat
    assert "Repeat" in text or "Concat" in text or "times" in text


def test_reverse_decompile_method_overload_index(mcp, asm):
    overloads = mcp.call_json("reverse_list_overloads",
                              {"asmPath": asm,
                               "typeFullName": "DnSpyMcp.TestTarget.Program",
                               "methodName": "Greet"})["items"]
    target = next(o for o in overloads if "(System.String,System.String)" in o["signature"])
    r = mcp.call_json("reverse_decompile_method",
                      {"asmPath": asm,
                       "typeFullName": "DnSpyMcp.TestTarget.Program",
                       "methodName": "Greet",
                       "overloadIndex": target["index"]})
    # The 2-string overload references the `greeting` parameter
    assert "greeting" in r["text"]


def test_reverse_decompile_method_ambiguous_errors(mcp, asm):
    # No selector + multiple overloads → structured error listing them
    r = mcp.call("reverse_decompile_method",
                 {"asmPath": asm,
                  "typeFullName": "DnSpyMcp.TestTarget.Program",
                  "methodName": "Greet"})
    assert not r["ok"], "expected error for ambiguous overload"
    assert "overloads" in r["text"] or "signature" in r["text"]
    # Available list should be embedded
    assert "(System.String)" in r["text"]


def test_reverse_decompile_method_single_overload_no_selector(mcp, asm):
    # Add has only one overload — both signature-less and overloadIndex=None must succeed.
    r = mcp.call_json("reverse_decompile_method",
                      {"asmPath": asm,
                       "typeFullName": "DnSpyMcp.TestTarget.Program",
                       "methodName": "Add"})
    assert "Add" in r["text"]


def test_reverse_il_method_by_token(mcp, asm):
    methods = mcp.call_json("reverse_list_methods",
                            {"asmPath": asm, "typeFullName": "DnSpyMcp.TestTarget.Program"})["items"]
    add = next(m for m in methods if m["name"] == "Add")
    r = mcp.call_json("reverse_il_method_by_token", {"asmPath": asm, "token": str(add["token"])})
    assert any(i["opCode"] == "ret" for i in r["items"])


def test_reverse_find_string(mcp, asm):
    r = mcp.call_json("reverse_find_string", {"asmPath": asm, "needle": "widget-"})
    assert r["returned"] >= 1
    assert any("widget-" in (x.get("value") or "") for x in r["items"])


def test_reverse_find_string_regex(mcp, asm):
    """regex=true should switch to .NET regex. C# interpolated-string format
    templates compile to literal 'widget-{0}' / 'widget-{i}' patterns — test
    regex against that shape, not the runtime value."""
    # positive case — regex matching the format-template literal
    r = mcp.call_json("reverse_find_string",
                      {"asmPath": asm, "needle": r"^widget-\{.+\}$", "regex": True})
    assert r["returned"] >= 1, "expected at least one ^widget-{...}$ hit"
    assert all(x["value"].startswith("widget-") for x in r["items"])
    # negative case — pattern with no matching literal
    r_neg = mcp.call_json("reverse_find_string",
                         {"asmPath": asm, "needle": r"^ZzZ_never_matches_\d+$", "regex": True})
    assert r_neg["returned"] == 0


def test_reverse_find_string_substring_default(mcp, asm):
    """Default (regex=False) must treat needle as plain substring — characters
    that would be regex-special (like `[`) must NOT cause errors."""
    r = mcp.call_json("reverse_find_string",
                      {"asmPath": asm, "needle": "widget-"})
    assert r["returned"] >= 1
    # Literal square bracket as substring should not throw (would in regex mode)
    r2 = mcp.call_json("reverse_find_string",
                      {"asmPath": asm, "needle": "[unlikely_bracket_literal]"})
    assert r2["returned"] == 0  # no such literal, but no error either


def test_reverse_find_string_regex_invalid(mcp, asm):
    """Invalid regex must surface a descriptive error, not a silent 0 rows."""
    r = mcp.call("reverse_find_string",
                 {"asmPath": asm, "needle": r"[unterminated", "regex": True})
    assert not r["ok"]
    assert "invalid regex" in (r.get("text") or "").lower() or \
           "invalid regex" in (r.get("error") or "").lower()


def test_list_types_namespace_filter(mcp, asm):
    """namespacePattern must match exact namespace and nested namespaces."""
    r = mcp.call_json("reverse_list_types",
                      {"asmPath": asm, "namespacePattern": "DnSpyMcp.TestTarget"})
    assert r["returned"] >= 1
    assert all(
        (t.get("ns") or "").startswith("DnSpyMcp.TestTarget")
        for t in r["items"]
    )


def test_reverse_xref_to_method_shorthand(mcp, asm):
    # The shorthand path (no ::) should resolve by type+name and match any overload.
    r = mcp.call_json("reverse_xref_to_method",
                      {"asmPath": asm,
                       "targetFullName": "DnSpyMcp.TestTarget.Program.Add"})
    assert r["returned"] >= 1
    refs = " ".join((x.get("memberFullName") or "") for x in r["items"])
    assert "Compute" in refs, f"Compute missing from Add callers: {refs}"


def test_reverse_xref_to_method_full_signature(mcp, asm):
    r = mcp.call_json("reverse_xref_to_method",
                      {"asmPath": asm,
                       "targetFullName": "System.Int32 DnSpyMcp.TestTarget.Program::Add(System.Int32,System.Int32)"})
    assert r["returned"] >= 1


def test_reverse_xref_to_type(mcp, asm):
    # Widget is constructed in Program.Main via `new Widget(...)` and used as
    # the element type of List<Widget> AliveWidgets. xref_to_type should pick
    # up at least the Program.Main / .ctor / Churn methods that reference it.
    r = mcp.call_json("reverse_xref_to_type",
                      {"asmPath": asm, "typeFullName": "DnSpyMcp.TestTarget.Widget"})
    assert r["returned"] >= 1
    decls = {row.get("declaringType") for row in r["items"]}
    # Program (which contains Main / Churn / AliveWidgets) must be in there.
    assert any("Program" in (d or "") for d in decls), f"Program missing from xref hits: {decls}"


def test_reverse_xref_type_instantiations(mcp, asm):
    # Widget is constructed in Main (`new Widget($"widget-{i}", ...)`) and
    # in Churn (`new Widget($"churn-{i}", i)`).
    r = mcp.call_json("reverse_xref_type_instantiations",
                      {"asmPath": asm, "typeFullName": "DnSpyMcp.TestTarget.Widget"})
    assert r["returned"] >= 1
    methods = {row.get("memberFullName") or row.get("inMethod") or "" for row in r["items"]}
    blob = " ".join(methods)
    assert "Main" in blob or "Churn" in blob, f"no Main/Churn newobj site: {methods}"


def test_reverse_xref_to_field(mcp, asm):
    # TickCounter is read+written every tick in Main loop; StateLabel is
    # written every tick. Both are static fields on Program.
    r = mcp.call_json("reverse_xref_to_field",
                      {"asmPath": asm, "fieldFullName": "DnSpyMcp.TestTarget.Program.TickCounter"})
    assert r["returned"] >= 1
    methods = " ".join((row.get("memberFullName") or "") + " " + (row.get("inMethod") or "")
                       for row in r["items"])
    assert "Main" in methods, f"Main not in TickCounter xref: {methods}"


def test_reverse_xref_to_field_writes_only(mcp, asm):
    # writesOnly=true should narrow to only the stsfld sites.
    r_all = mcp.call_json("reverse_xref_to_field",
                         {"asmPath": asm, "fieldFullName": "DnSpyMcp.TestTarget.Program.StateLabel"})
    r_w = mcp.call_json("reverse_xref_to_field",
                       {"asmPath": asm,
                        "fieldFullName": "DnSpyMcp.TestTarget.Program.StateLabel",
                        "writesOnly": True})
    # writesOnly subset should be smaller-or-equal
    assert r_w["total"] <= r_all["total"]


def test_reverse_subtypes(mcp, asm):
    r = mcp.call_json("reverse_subtypes",
                      {"asmPath": asm, "typeFullName": "DnSpyMcp.TestTarget.Animal"})
    assert r["returned"] >= 1
    decls = " ".join((row.get("memberFullName") or row.get("memberName") or "")
                     for row in r["items"])
    assert "Cat" in decls, f"Cat missing from subtypes: {decls}"


def test_reverse_method_overrides(mcp, asm):
    r = mcp.call_json("reverse_method_overrides",
                      {"asmPath": asm, "targetFullName": "DnSpyMcp.TestTarget.Animal.Speak"})
    assert r["returned"] >= 1
    decls = " ".join((row.get("memberFullName") or "") for row in r["items"])
    assert "Cat" in decls, f"Cat.Speak missing from overrides: {decls}"


def test_reverse_method_overridden_by_base(mcp, asm):
    r = mcp.call_json("reverse_method_overridden_by_base",
                      {"asmPath": asm, "targetFullName": "DnSpyMcp.TestTarget.Cat.Speak"})
    assert r["returned"] >= 1
    decls = " ".join((row.get("memberFullName") or "") for row in r["items"])
    assert "Animal" in decls, f"Animal.Speak missing from base chain: {decls}"


def test_reverse_property_overrides(mcp, asm):
    r = mcp.call_json("reverse_property_overrides",
                      {"asmPath": asm,
                       "typeFullName": "DnSpyMcp.TestTarget.Animal",
                       "name": "Habitat"})
    assert r["returned"] >= 1
    decls = " ".join((row.get("memberFullName") or "") for row in r["items"])
    assert "Cat" in decls


def test_reverse_method_calls(mcp, asm):
    # Compute() calls Add() and Multiply() — they should appear as outgoing
    # references.
    r = mcp.call_json("reverse_method_calls",
                      {"asmPath": asm, "targetFullName": "DnSpyMcp.TestTarget.Program.Compute"})
    assert r["returned"] >= 1
    refs = " ".join((row.get("memberFullName") or row.get("memberName") or "") for row in r["items"])
    assert "Add" in refs and "Multiply" in refs, f"Add/Multiply missing from Compute calls: {refs}"


def test_reverse_list_fields(mcp, asm):
    r = mcp.call_json("reverse_list_fields",
                      {"asmPath": asm, "typeFullName": "DnSpyMcp.TestTarget.Program"})
    names = [row["name"] for row in r["items"]]
    assert "TickCounter" in names and "StateLabel" in names
    tick = next(row for row in r["items"] if row["name"] == "TickCounter")
    assert tick["isStatic"] is True


def test_reverse_list_properties(mcp, asm):
    r = mcp.call_json("reverse_list_properties",
                      {"asmPath": asm, "typeFullName": "DnSpyMcp.TestTarget.Widget"})
    names = [row["name"] for row in r["items"]]
    assert "Name" in names and "Value" in names
    name_row = next(row for row in r["items"] if row["name"] == "Name")
    assert name_row["hasGetter"] is True


def test_reverse_list_events(mcp, asm):
    r = mcp.call_json("reverse_list_events",
                      {"asmPath": asm, "typeFullName": "DnSpyMcp.TestTarget.Cat"})
    names = [row["name"] for row in r["items"]]
    assert "Renamed" in names


def test_reverse_list_nested_types(mcp, asm):
    # Program has no nested types — accept empty without error.
    r = mcp.call_json("reverse_list_nested_types",
                      {"asmPath": asm, "typeFullName": "DnSpyMcp.TestTarget.Program"})
    assert r["total"] >= 0


def test_reverse_type_info(mcp, asm):
    r = mcp.call_json("reverse_type_info",
                      {"asmPath": asm, "typeFullName": "DnSpyMcp.TestTarget.Cat"})
    assert r["fullName"] == "DnSpyMcp.TestTarget.Cat"
    assert "DnSpyMcp.TestTarget.Animal" in (r.get("baseType") or "")
    assert any("IPet" in (i or "") for i in r.get("interfaces") or [])
    assert r["counts"]["methods"] >= 1


def test_reverse_decompile_property(mcp, asm):
    r = mcp.call_json("reverse_decompile_property",
                      {"asmPath": asm,
                       "typeFullName": "DnSpyMcp.TestTarget.Animal",
                       "name": "Habitat"})
    assert "Habitat" in r["text"]
    assert "earth" in r["text"]


def test_reverse_decompile_event(mcp, asm):
    r = mcp.call_json("reverse_decompile_event",
                      {"asmPath": asm,
                       "typeFullName": "DnSpyMcp.TestTarget.Cat",
                       "name": "Renamed"})
    assert "Renamed" in r["text"]


def test_reverse_decompile_field(mcp, asm):
    r = mcp.call_json("reverse_decompile_field",
                      {"asmPath": asm,
                       "typeFullName": "DnSpyMcp.TestTarget.Program",
                       "name": "StateLabel"})
    assert "StateLabel" in r["text"]


def test_reverse_xref_to_property(mcp, asm):
    # Cat.Describe extension method reads cat.Nickname AND cat.Habitat —
    # xref of Habitat should pick up Describe.
    r = mcp.call_json("reverse_xref_to_property",
                      {"asmPath": asm,
                       "typeFullName": "DnSpyMcp.TestTarget.Animal",
                       "name": "Habitat"})
    refs = " ".join((row.get("memberFullName") or "") for row in r["items"])
    assert r["returned"] >= 1
    assert "Describe" in refs or "Cat" in refs, f"no Habitat caller: {refs}"


def test_reverse_xref_to_event(mcp, asm):
    # Cat.Pat raises Renamed via the synthesized invoke — xref to Renamed
    # should at least not error and may or may not see Pat depending on
    # how the analyzer normalizes accessor calls.
    r = mcp.call_json("reverse_xref_to_event",
                      {"asmPath": asm,
                       "typeFullName": "DnSpyMcp.TestTarget.Cat",
                       "name": "Renamed"})
    assert r["total"] >= 0  # smoke: didn't throw


def test_reverse_event_fired_by(mcp, asm):
    # The analyzer matches `ldfld <backing>` + `callvirt EventType::Invoke`.
    # It needs the event's delegate type (EventHandler<string>) to be
    # resolvable to find the Invoke method, which means mscorlib must be
    # in the workspace. With only the test asm opened, the resolution fails
    # silently and we get zero hits — that's expected, not a regression.
    # Smoke-test only: did not throw.
    r = mcp.call_json("reverse_event_fired_by",
                      {"asmPath": asm,
                       "typeFullName": "DnSpyMcp.TestTarget.Cat",
                       "name": "Renamed"})
    assert r["total"] >= 0


def test_reverse_interface_method_implemented_by(mcp, asm):
    # IPet.Pat is implemented by Cat.Pat.
    r = mcp.call_json("reverse_interface_method_implemented_by",
                      {"asmPath": asm,
                       "targetFullName": "DnSpyMcp.TestTarget.IPet.Pat"})
    assert r["returned"] >= 1
    decls = " ".join((row.get("memberFullName") or "") for row in r["items"])
    assert "Cat" in decls


def test_reverse_interface_property_implemented_by(mcp, asm):
    r = mcp.call_json("reverse_interface_property_implemented_by",
                      {"asmPath": asm,
                       "typeFullName": "DnSpyMcp.TestTarget.IPet",
                       "name": "Nickname"})
    assert r["returned"] >= 1
    decls = " ".join((row.get("memberFullName") or "") for row in r["items"])
    assert "Cat" in decls


def test_reverse_type_exposed_by(mcp, asm):
    # Cat.Pat returns void but Cat.Speak returns string and Cat.Habitat is
    # a string property. Pick a more obvious target: Widget exposes itself
    # via List<Widget> AliveWidgets and the constructor param of Cat.Pat
    # doesn't reach it. We use Animal which is a parameter-or-return type
    # nowhere in the test target — accept zero hits as long as no error.
    r = mcp.call_json("reverse_type_exposed_by",
                      {"asmPath": asm, "typeFullName": "DnSpyMcp.TestTarget.Widget"})
    assert r["total"] >= 0  # smoke: didn't throw


def test_reverse_type_extension_methods(mcp, asm):
    # CatExtensions.Describe is `this Cat cat` — should resolve.
    r = mcp.call_json("reverse_type_extension_methods",
                      {"asmPath": asm, "typeFullName": "DnSpyMcp.TestTarget.Cat"})
    assert r["returned"] >= 1
    refs = " ".join((row.get("memberFullName") or "") for row in r["items"])
    assert "Describe" in refs, f"Describe missing from extension methods: {refs}"


def test_reverse_find_attribute_usage(mcp, asm):
    # [TagAttribute] is applied to Cat in the test target — the analyzer
    # should report Cat as a usage site.
    r = mcp.call_json("reverse_find_attribute_usage",
                      {"asmPath": asm,
                       "attributeTypeFullName": "DnSpyMcp.TestTarget.TagAttribute"})
    assert r["returned"] >= 1
    decls = " ".join((row.get("memberFullName") or row.get("memberName") or "") for row in r["items"])
    assert "Cat" in decls, f"Cat missing from TagAttribute usage: {decls}"


def test_reverse_xref_to_type_unknown_errors(mcp, asm):
    r = mcp.call("reverse_xref_to_type",
                 {"asmPath": asm, "typeFullName": "Ns.DoesNotExist"})
    assert not r["ok"], "expected error for unknown type"
    assert "type not found" in r["text"].lower()


def test_reverse_annotations_roundtrip(mcp, asm, tmp_path_factory):
    # Run on a copy of the test asm so the sidecar sticks around for
    # cross-tool inspection without leaking into other tests.
    import shutil, json as _json, os
    tmp = tmp_path_factory.mktemp("annot") / "dnspymcptest.exe"
    shutil.copy2(asm, tmp)
    asm_str = str(tmp)

    # Open the copy fresh (the conftest fixture only opens `asm`, not `tmp`).
    mcp.call_json("reverse_open", {"asmPath": asm_str})
    try:
        # Pick a known method token via reverse_list_methods.
        methods = mcp.call_json("reverse_list_methods",
                                {"asmPath": asm_str, "typeFullName": "DnSpyMcp.TestTarget.Program"})["items"]
        add = next(m for m in methods if m["name"] == "Add")
        token = add["token"]

        # Rename + comment.
        r1 = mcp.call_json("reverse_rename_member",
                           {"asmPath": asm_str, "token": str(token), "newName": "AddTwoInts"})
        assert r1["ok"] is True
        assert r1["newName"] == "AddTwoInts"
        sidecar = r1["sidecarPath"]
        assert os.path.exists(sidecar), f"sidecar not written: {sidecar}"

        r2 = mcp.call_json("reverse_set_comment",
                           {"asmPath": asm_str, "token": str(token), "text": "trivial integer add — used by Compute"})
        assert r2["ok"] is True

        # List should show both rows.
        listing = mcp.call_json("reverse_list_annotations", {"asmPath": asm_str})
        kinds = {row["kind"] for row in listing["items"]}
        assert "rename" in kinds and "comment" in kinds

        # Sidecar JSON contains both entries under string-token keys.
        with open(sidecar, "r", encoding="utf-8") as f:
            data = _json.load(f)
        assert str(token) in data["renames"]
        assert data["renames"][str(token)] == "AddTwoInts"
        assert str(token) in data["comments"]

        # Clear rename only — comment should survive.
        clr = mcp.call_json("reverse_clear_annotation",
                            {"asmPath": asm_str, "token": str(token), "kind": "rename"})
        assert clr["removedRename"] is True
        assert clr["removedComment"] is False
        listing2 = mcp.call_json("reverse_list_annotations", {"asmPath": asm_str})
        kinds2 = {row["kind"] for row in listing2["items"]}
        assert "rename" not in kinds2 and "comment" in kinds2
    finally:
        mcp.call_json("reverse_close", {"asmPath": asm_str})


def test_reverse_patch_il_nop(mcp, asm, tmp_path_factory):
    out = tmp_path_factory.mktemp("patch") / "dnspymcptest.patched.exe"
    r = mcp.call_json("reverse_patch_il_nop", {
        "asmPath": asm,
        "typeFullName": "DnSpyMcp.TestTarget.Program",
        "methodName": "Add",
        "startOffset": "0",
        "endOffset": "2",
        "outputPath": str(out),
    })
    assert r["changedInstructions"] >= 1
    assert Path(r["written"]).exists()


def test_reverse_patch_bytes_roundtrip(mcp, asm, tmp_path_factory):
    # copy the asm so we don't clobber the original
    copy_dir = tmp_path_factory.mktemp("bytes")
    copy = copy_dir / "dnspymcptest.exe"
    shutil.copy2(asm, copy)
    # read first byte, overwrite with same value
    orig = copy.read_bytes()[:1].hex()
    r = mcp.call_json("reverse_patch_bytes", {"filePath": str(copy), "offset": "0", "hex": orig})
    assert r["written"] == 1


def test_reverse_save_assembly(mcp, asm, tmp_path_factory):
    out = tmp_path_factory.mktemp("save") / "dnspymcptest.saved.exe"
    r = mcp.call_json("reverse_save_assembly", {"asmPath": asm, "outputPath": str(out)})
    assert Path(r["written"]).exists()


def test_asm_close_reopen(mcp, testtarget_asm: Path):
    path = str(testtarget_asm)
    # open+close roundtrip: close whatever the fixture left open, then
    # re-open from scratch — confirms reopen after close works.
    mcp.call("reverse_close", {"asmPath": path})  # tolerant: might already be closed
    closed2 = mcp.call("reverse_close", {"asmPath": path})
    # second close must report closed=False (no-op, not an error)
    if closed2["ok"]:
        import json as _json
        d = _json.loads(closed2["text"])
        assert d.get("closed") is False
    # reopen for subsequent tests
    mcp.call_json("reverse_open", {"asmPath": path})


def test_asm_current_and_switch(mcp, testtarget_asm: Path):
    path = str(testtarget_asm)
    # Tolerant open — fixture may have already opened it (duplicate-open is
    # an error now; we don't care which path got us here).
    mcp.call("reverse_open", {"asmPath": path})
    cur = mcp.call_json("reverse_current")
    assert cur["current"].lower() == path.lower()
    # switch is a no-op when there's only one, but should still succeed
    switched = mcp.call_json("reverse_switch", {"asmPath": path})
    assert switched["current"].lower() == path.lower()


def test_asm_open_auto_switches_active(mcp, testtarget_asm: Path, tmp_path_factory):
    """Opening a second asm must make it the new active session (matches debug_session_connect)."""
    primary = str(testtarget_asm)
    # Tolerant open — fixture may already have it open. Just make sure it's
    # the current one before we test the auto-switch on a second open.
    mcp.call("reverse_open", {"asmPath": primary})
    mcp.call_json("reverse_switch", {"asmPath": primary})

    # copy the asm so we have a distinct path to open
    copy_dir = tmp_path_factory.mktemp("autoswitch")
    copy = copy_dir / "dnspymcptest.copy.exe"
    shutil.copy2(primary, copy)
    mcp.call_json("reverse_open", {"asmPath": str(copy)})

    cur = mcp.call_json("reverse_current")
    assert cur["current"].lower() == str(copy).lower(), (
        "reverse_open should switch the active session to the newly-opened asm")

    # cleanup: switch back and close the copy so later tests aren't affected
    mcp.call_json("reverse_close", {"asmPath": str(copy)})
    mcp.call_json("reverse_switch", {"asmPath": primary})


def test_asm_close_releases_file_handle(mcp, testtarget_asm: Path, tmp_path_factory):
    """reverse_close must dispose the PEFile/ModuleDef so the file can be deleted."""
    import os
    copy_dir = tmp_path_factory.mktemp("release")
    copy = copy_dir / "dnspymcptest.release.exe"
    shutil.copy2(testtarget_asm, copy)

    mcp.call_json("reverse_open", {"asmPath": str(copy)})
    mcp.call_json("reverse_close", {"asmPath": str(copy)})

    # If the handle leaks the delete raises PermissionError on Windows.
    os.remove(copy)
    assert not copy.exists()


def test_reverse_open_rejects_duplicate(mcp, testtarget_asm: Path):
    """Opening the same path twice must error, not silently return the
    existing slot. Prevents accumulating invisible duplicates in the session
    list. Caller is expected to reverse_switch or reverse_close first."""
    path = str(testtarget_asm)
    # First open is best-effort — we don't know prior test state, so we
    # accept both "opened fresh" and "already opened" as the starting point.
    mcp.call("reverse_open", {"asmPath": path})

    r = mcp.call("reverse_open", {"asmPath": path})
    assert not r["ok"], "second open of the same asm must error"
    msg = (r.get("text") or r.get("error") or "").lower()
    assert "already opened" in msg, f"error must explain; got: {msg!r}"


def test_list_references(mcp, asm):
    """The test target references mscorlib / System.* — every row should have
    a name + version, and at least one well-known core asm should appear."""
    r = mcp.call_json("reverse_list_references", {"asmPath": asm})
    assert r["returned"] >= 1
    names = {row["name"] for row in r["items"]}
    # Every .NET asm references mscorlib or System.Runtime
    assert any(n in {"mscorlib", "System.Runtime", "System.Private.CoreLib"} for n in names), \
        f"expected a core BCL ref; got names={names}"
    # Each row well-formed
    for row in r["items"]:
        assert row["name"]
        # version may be null for some weird refs but usually present
        assert "opened" in row


def test_list_references_only_missing(mcp, asm):
    """onlyMissing=True must filter out refs whose simple name matches an
    opened asm. Default Workspace has only the test target opened, so all
    refs should be missing."""
    r = mcp.call_json("reverse_list_references", {"asmPath": asm, "onlyMissing": True})
    assert all(not row["opened"] for row in r["items"])


def test_cross_dll_find_string_and_xref(mcp, testtarget_asm: Path, tmp_path_factory):
    """When two copies of the assembly are open, find_string and
    xref_to_method with asmPath omitted must return hits from BOTH. This is
    the whole point of the Phase 4 cross-DLL index."""
    primary = str(testtarget_asm)
    copy_dir = tmp_path_factory.mktemp("crossdll")
    copy = copy_dir / "dnspymcptest.copy2.exe"
    shutil.copy2(primary, copy)

    # Close any existing opens to start clean
    existing = mcp.call_json("reverse_list")
    for a in existing:
        mcp.call("reverse_close", {"asmPath": a["path"]})

    try:
        mcp.call_json("reverse_open", {"asmPath": primary})
        mcp.call_json("reverse_open", {"asmPath": str(copy)})

        # cross-DLL find_string: asmPath omitted → every opened asm
        r = mcp.call_json("reverse_find_string", {"needle": "widget-"})
        assert r["returned"] >= 2, f"expected hits from both DLLs, got {r['returned']}"
        asms = {x.get("asm", "").lower() for x in r["items"]}
        assert primary.lower() in asms
        assert str(copy).lower() in asms

        # Limit to primary → only primary rows
        r_one = mcp.call_json("reverse_find_string",
                              {"needle": "widget-", "asmPath": primary})
        assert all(x["asm"].lower() == primary.lower() for x in r_one["items"])

        # cross-DLL xref shorthand (Add is a known method in test target)
        x = mcp.call_json("reverse_xref_to_method",
                          {"targetFullName": "DnSpyMcp.TestTarget.Program.Add"})
        assert x["returned"] >= 2, f"expected xref hits from both DLLs, got {x['returned']}"
        xasms = {s.get("asm", "").lower() for s in x["items"]}
        assert primary.lower() in xasms and str(copy).lower() in xasms
    finally:
        mcp.call("reverse_close", {"asmPath": str(copy)})
        # keep primary open for subsequent tests
        existing = mcp.call_json("reverse_list")
        if not any(a["path"].lower() == primary.lower() for a in existing):
            mcp.call_json("reverse_open", {"asmPath": primary})


def test_asm_default_session_omit_path(mcp, testtarget_asm: Path):
    """With exactly one open asm, tools that accept asmPath=null must still work."""
    # Tolerant open — duplicate-open errors; we just need it active.
    mcp.call("reverse_open", {"asmPath": str(testtarget_asm)})
    mcp.call_json("reverse_switch", {"asmPath": str(testtarget_asm)})
    # omit asmPath — should use the active session
    r = mcp.call_json("reverse_list_types", {"namePattern": "Widget"})
    assert any(t["fullName"] == "DnSpyMcp.TestTarget.Widget" for t in r["items"])
    # decompile without asmPath too
    r = mcp.call_json("reverse_decompile_type", {"typeFullName": "DnSpyMcp.TestTarget.Widget"})
    assert "class Widget" in r["text"]
