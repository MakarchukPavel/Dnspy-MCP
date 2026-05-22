using System.ComponentModel;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using DnSpyMcp.Services;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Analyzer.TreeNodes;
using ICSharpCode.Decompiler.TypeSystem;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DnSpyMcp.Tools;

[McpServerToolType]
public static class AsmFileTools
{
    [McpServerTool(Name = "reverse_open")]
    [Description("[REVERSE] Open a .NET assembly from disk for static analysis (decompile / IL / xref / find). Becomes the active session (subsequent FILE tools may omit asmPath). Path is absolute.")]
    public static object AsmOpen(Workspace ws, string asmPath)
    {
        var a = ws.Open(asmPath);
        return new
        {
            path = a.Path,
            name = a.Module.Name?.String,
            assembly = a.Module.Assembly?.FullName,
            types = a.Module.GetTypes().Count(),
            current = ws.Current,
        };
    }

    [McpServerTool(Name = "reverse_close")]
    [Description("[REVERSE] Close a previously opened assembly and free its metadata.")]
    public static object AsmClose(Workspace ws, string asmPath)
        => new { closed = ws.Close(asmPath), current = ws.Current };

    [McpServerTool(Name = "reverse_list")]
    [Description("[REVERSE] List every assembly currently opened in the workspace (multi-session). Marks the active one.")]
    public static object AsmList(Workspace ws)
        => ws.All.Select(a => new {
            path = a.Path,
            name = a.Module.Name?.String,
            types = a.Module.GetTypes().Count(),
            active = string.Equals(a.Path, ws.Current, StringComparison.OrdinalIgnoreCase),
        }).ToArray();

    [McpServerTool(Name = "reverse_current")]
    [Description("[REVERSE] Return the currently-active asm_file path (used by other FILE tools when asmPath is omitted).")]
    public static object AsmCurrent(Workspace ws) => new { current = ws.Current };

    [McpServerTool(Name = "reverse_switch")]
    [Description("[REVERSE] Switch the active asm_file session. Subsequent FILE tools can omit asmPath and act on this one.")]
    public static object AsmSwitch(Workspace ws, string asmPath)
    {
        var a = ws.Switch(asmPath);
        return new { current = a.Path };
    }

    [McpServerTool(Name = "reverse_list_types")]
    [Description("[REVERSE] List types in an opened assembly (paginated). Filters: namePattern (case-insensitive substring on FullName), namespacePattern (exact namespace, or 'Foo.Bar' matches nested 'Foo.Bar.*'). Params: asmPath (optional), namePattern (optional), namespacePattern (optional), offset=0, max=100. Response: {total, offset, returned, truncated, items}.")]
    public static object ListTypes(Workspace ws, string? asmPath = null, string? namePattern = null, string? namespacePattern = null, int offset = 0, int max = 100)
    {
        var a = ws.Get(asmPath);
        var filtered = a.Module.GetTypes()
            .Where(t => namePattern == null || (t.FullName ?? "").Contains(namePattern, StringComparison.OrdinalIgnoreCase))
            .Where(t =>
            {
                if (namespacePattern == null) return true;
                var ns = t.Namespace?.String ?? "";
                return ns.Equals(namespacePattern, StringComparison.OrdinalIgnoreCase)
                    || ns.StartsWith(namespacePattern + ".", StringComparison.OrdinalIgnoreCase);
            })
            .Select(t => new { fullName = t.FullName, ns = t.Namespace?.String, token = t.MDToken.Raw, methodCount = t.Methods.Count });
        return Paging.Page(filtered, offset, max);
    }

    [McpServerTool(Name = "reverse_list_methods")]
    [Description("[REVERSE] List methods of a type (paginated). Params: typeFullName, asmPath (optional), offset=0, max=200.")]
    public static object ListMethods(Workspace ws, string typeFullName, string? asmPath = null, int offset = 0, int max = 100)
    {
        var a = ws.Get(asmPath);
        var t = ResolveTypeOrThrow(a, typeFullName);
        var rows = t.Methods.Select(m => new {
            name = m.Name.String,
            fullName = m.FullName,
            token = m.MDToken.Raw,
            hasBody = m.HasBody,
            attributes = m.Attributes.ToString(),
        });
        return Paging.Page(rows, offset, max);
    }

    [McpServerTool(Name = "reverse_list_references")]
    [Description("[REVERSE] List the assembly references declared by an opened module's manifest, marking which are currently opened in the workspace and which are still missing. Use this BEFORE reverse_xref_to_method on a target whose callers might live in dependent assemblies — open the missing ones first so the cross-DLL scan actually has them in scope. Each row: {name, version, publicKeyToken (hex), culture, opened (bool), openedAsmPath?}. Params: asmPath (optional, defaults to active session), onlyMissing=false, offset=0, max=100.")]
    public static object ListReferences(Workspace ws, string? asmPath = null, bool onlyMissing = false, int offset = 0, int max = 100)
    {
        var a = ws.Get(asmPath);
        // Pre-build a name->path lookup of what's opened. dnlib AssemblyRef
        // matching is by simple name; version-strict matching is rarely what
        // RE callers want (you usually have one version of mscorlib and want
        // it counted regardless of declared rev).
        var openedByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in ws.All)
        {
            var name = o.Module.Assembly?.Name.String;
            if (!string.IsNullOrEmpty(name) && !openedByName.ContainsKey(name))
                openedByName[name] = o.Path;
        }

        var rows = a.Module.GetAssemblyRefs()
            .Select(r =>
            {
                var name = r.Name.String;
                bool opened = openedByName.TryGetValue(name, out var openPath);
                return new
                {
                    name,
                    version = r.Version?.ToString(),
                    publicKeyToken = FormatPublicKeyToken(r.PublicKeyOrToken),
                    culture = string.IsNullOrEmpty(r.Culture?.String) ? null : r.Culture.String,
                    opened,
                    openedAsmPath = opened ? openPath : null,
                };
            })
            .Where(row => !onlyMissing || !row.opened);
        return Paging.Page(rows, offset, max);
    }

    private static string? FormatPublicKeyToken(PublicKeyBase? pk)
    {
        if (pk is null) return null;
        // PublicKey full form is large — collapse to its 8-byte token for
        // display (matches how AssemblyRef strings appear in IL listings).
        var tok = pk is PublicKey full ? full.Token : pk as PublicKeyToken;
        var bytes = tok?.Data;
        if (bytes is null || bytes.Length == 0) return null;
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    // ---- Phase 6a: type member listing -----------------------------------

    /// <summary>
    /// Resolve a type by full name on an opened module. Both code paths use
    /// dnlib (which is also what dnSpy's analyzer/decompiler uses internally).
    ///
    /// dnlib exposes <c>ModuleDef.FindReflection(string)</c> as the canonical
    /// reflection-style lookup, but it silently misses a handful of real
    /// types — confirmed in SP 19725 against
    /// <c>Microsoft.SharePoint.SPSite</c>: <c>FindReflection</c> returns null
    /// while <c>reverse_xref_to_type</c> (which iterates <c>GetTypes()</c>)
    /// reports 3917 references to that exact type. The fallback iterates
    /// <c>Module.GetTypes()</c> and matches FullName, normalising the
    /// nested-type separator both ways (<c>+</c> reflection style vs
    /// <c>/</c> dnlib FullName style) so callers can spell nested types
    /// whichever way they prefer.
    ///
    /// We don't delegate to dnSpy.Analyzer because none of its accessible
    /// helpers (Helpers.IsReferencedBy / GetOriginalCodeLocation / the
    /// FilterSearcher live in the WPF main project) does "find type by
    /// name", so a dnlib-only fallback is the lowest-friction fix.
    /// </summary>
    internal static TypeDef ResolveTypeOrThrow(Workspace.OpenedAsm a, string typeFullName)
    {
        var t = a.Module.FindReflection(typeFullName);
        if (t != null) return t;
        var alt = typeFullName.Replace('+', '/');
        foreach (var td in a.Module.GetTypes())
        {
            if (td.FullName == typeFullName || td.FullName == alt)
                return td;
        }
        throw new McpException($"type not found: {typeFullName}");
    }

    [McpServerTool(Name = "reverse_list_fields")]
    [Description("[REVERSE] List every field declared on a type. Params: typeFullName, asmPath (optional), offset=0, max=200. Rows: {name, fullName, type, attributes, token, isStatic, isReadonly}.")]
    public static object ListFields(Workspace ws, string typeFullName, string? asmPath = null, int offset = 0, int max = 200)
    {
        var a = ws.Get(asmPath);
        var t = ResolveTypeOrThrow(a, typeFullName);
        var rows = t.Fields.Select(f => new
        {
            name = f.Name.String,
            fullName = f.FullName,
            type = f.FieldType?.FullName,
            attributes = f.Attributes.ToString(),
            token = f.MDToken.Raw,
            isStatic = f.IsStatic,
            isReadonly = f.IsInitOnly,
        });
        return Paging.Page(rows, offset, max);
    }

    [McpServerTool(Name = "reverse_list_properties")]
    [Description("[REVERSE] List every property declared on a type. Params: typeFullName, asmPath (optional), offset=0, max=200. Rows: {name, fullName, type, hasGetter, hasSetter, attributes, token}.")]
    public static object ListProperties(Workspace ws, string typeFullName, string? asmPath = null, int offset = 0, int max = 200)
    {
        var a = ws.Get(asmPath);
        var t = ResolveTypeOrThrow(a, typeFullName);
        var rows = t.Properties.Select(p => new
        {
            name = p.Name.String,
            fullName = p.FullName,
            type = p.PropertySig?.RetType?.FullName,
            hasGetter = p.GetMethod != null,
            hasSetter = p.SetMethod != null,
            attributes = p.Attributes.ToString(),
            token = p.MDToken.Raw,
        });
        return Paging.Page(rows, offset, max);
    }

    [McpServerTool(Name = "reverse_list_events")]
    [Description("[REVERSE] List every event declared on a type. Params: typeFullName, asmPath (optional), offset=0, max=200. Rows: {name, fullName, type, hasAdd, hasRemove, hasInvoke, attributes, token}.")]
    public static object ListEvents(Workspace ws, string typeFullName, string? asmPath = null, int offset = 0, int max = 200)
    {
        var a = ws.Get(asmPath);
        var t = ResolveTypeOrThrow(a, typeFullName);
        var rows = t.Events.Select(e => new
        {
            name = e.Name.String,
            fullName = e.FullName,
            type = e.EventType?.FullName,
            hasAdd = e.AddMethod != null,
            hasRemove = e.RemoveMethod != null,
            hasInvoke = e.InvokeMethod != null,
            attributes = e.Attributes.ToString(),
            token = e.MDToken.Raw,
        });
        return Paging.Page(rows, offset, max);
    }

    [McpServerTool(Name = "reverse_list_nested_types")]
    [Description("[REVERSE] List every nested type declared on a type. Params: typeFullName, asmPath (optional), offset=0, max=200. Rows: {name, fullName, attributes, token, isAbstract, isSealed}.")]
    public static object ListNestedTypes(Workspace ws, string typeFullName, string? asmPath = null, int offset = 0, int max = 200)
    {
        var a = ws.Get(asmPath);
        var t = ResolveTypeOrThrow(a, typeFullName);
        var rows = t.NestedTypes.Select(n => new
        {
            name = n.Name.String,
            fullName = n.FullName,
            attributes = n.Attributes.ToString(),
            token = n.MDToken.Raw,
            isAbstract = n.IsAbstract,
            isSealed = n.IsSealed,
        });
        return Paging.Page(rows, offset, max);
    }

    [McpServerTool(Name = "reverse_type_info")]
    [Description("[REVERSE] Summarise a type: base type, implemented interfaces, member counts, generic parameters, attributes. One-shot replacement for separately calling reverse_list_fields/properties/events/methods/nested_types when you only need a quick overview. Params: typeFullName, asmPath (optional).")]
    public static object TypeInfo(Workspace ws, string typeFullName, string? asmPath = null)
    {
        var a = ws.Get(asmPath);
        var t = ResolveTypeOrThrow(a, typeFullName);
        return new
        {
            fullName = t.FullName,
            @namespace = t.Namespace?.String,
            name = t.Name?.String,
            token = t.MDToken.Raw,
            attributes = t.Attributes.ToString(),
            isAbstract = t.IsAbstract,
            isSealed = t.IsSealed,
            isInterface = t.IsInterface,
            isEnum = t.IsEnum,
            baseType = t.BaseType?.FullName,
            interfaces = t.Interfaces.Select(i => i.Interface?.FullName).Where(s => s != null).ToArray(),
            genericParameters = t.GenericParameters.Select(g => new { name = g.Name.String, number = g.Number, attributes = g.Flags.ToString() }).ToArray(),
            counts = new
            {
                fields = t.Fields.Count,
                properties = t.Properties.Count,
                events = t.Events.Count,
                methods = t.Methods.Count,
                nestedTypes = t.NestedTypes.Count,
            },
        };
    }

    [McpServerTool(Name = "reverse_decompile_type")]
    [Description("[REVERSE] Decompile a whole type to C# (truncatable — big types blow up context). Params: typeFullName, asmPath (optional), offsetChars=0, maxChars=64000. Response: {totalChars, offsetChars, returnedChars, truncated, text}.")]
    public static object DecompileType(Workspace ws, string typeFullName, string? asmPath = null, int offsetChars = 0, int maxChars = 32_000)
    {
        var a = ws.Get(asmPath);
        var full = a.Decompiler.DecompileTypeAsString(new FullTypeName(typeFullName));
        return Paging.ClampText(full, offsetChars, maxChars);
    }

    [McpServerTool(Name = "reverse_decompile_method")]
    [Description("[REVERSE] Decompile a single method to C# (truncatable). Overload selection: pass `signature` (e.g. \"(System.String,System.Int32)\" — lenient, also accepts shorthand like \"(string,int)\"); OR pass `overloadIndex` (zero-based). When neither is given and the method has multiple overloads, the call fails with a list of available signatures. Use `reverse_list_overloads` to enumerate. Params: typeFullName, methodName, asmPath (optional), signature (optional), overloadIndex (optional), offsetChars=0, maxChars=64000.")]
    public static object DecompileMethod(Workspace ws, string typeFullName, string methodName,
                                         string? asmPath = null, string? signature = null, int? overloadIndex = null,
                                         int offsetChars = 0, int maxChars = 64_000)
    {
        var a = ws.Get(asmPath);
        var m = ResolveOverload(a.Module, typeFullName, methodName, signature, overloadIndex);
        var handle = MetadataTokens.MethodDefinitionHandle((int)m.MDToken.Rid);
        var full = a.Decompiler.DecompileAsString(handle);
        return Paging.ClampText(full, offsetChars, maxChars);
    }

    [McpServerTool(Name = "reverse_decompile_property")]
    [Description("[REVERSE] Decompile a property to C# (truncatable). Returns the get/set accessor pair with attributes and modifiers. Params: typeFullName, name, asmPath (optional), offsetChars=0, maxChars=64000.")]
    public static object DecompileProperty(Workspace ws, string typeFullName, string name, string? asmPath = null, int offsetChars = 0, int maxChars = 64_000)
    {
        var a = ws.Get(asmPath);
        var t = ResolveTypeOrThrow(a, typeFullName);
        var p = t.Properties.FirstOrDefault(x => x.Name == name) ?? throw new McpException($"property not found: {typeFullName}::{name}");
        var handle = MetadataTokens.PropertyDefinitionHandle((int)p.MDToken.Rid);
        var full = a.Decompiler.DecompileAsString(handle);
        return Paging.ClampText(full, offsetChars, maxChars);
    }

    [McpServerTool(Name = "reverse_decompile_event")]
    [Description("[REVERSE] Decompile an event to C# (truncatable). Returns the add/remove accessor pair with attributes. Params: typeFullName, name, asmPath (optional), offsetChars=0, maxChars=64000.")]
    public static object DecompileEvent(Workspace ws, string typeFullName, string name, string? asmPath = null, int offsetChars = 0, int maxChars = 64_000)
    {
        var a = ws.Get(asmPath);
        var t = ResolveTypeOrThrow(a, typeFullName);
        var e = t.Events.FirstOrDefault(x => x.Name == name) ?? throw new McpException($"event not found: {typeFullName}::{name}");
        var handle = MetadataTokens.EventDefinitionHandle((int)e.MDToken.Rid);
        var full = a.Decompiler.DecompileAsString(handle);
        return Paging.ClampText(full, offsetChars, maxChars);
    }

    [McpServerTool(Name = "reverse_decompile_field")]
    [Description("[REVERSE] Decompile a field to C# (truncatable). Returns the field declaration plus initializer (for const / readonly static / explicit-layout) when present. Params: typeFullName, name, asmPath (optional), offsetChars=0, maxChars=32000.")]
    public static object DecompileField(Workspace ws, string typeFullName, string name, string? asmPath = null, int offsetChars = 0, int maxChars = 32_000)
    {
        var a = ws.Get(asmPath);
        var t = ResolveTypeOrThrow(a, typeFullName);
        var f = t.Fields.FirstOrDefault(x => x.Name == name) ?? throw new McpException($"field not found: {typeFullName}::{name}");
        var handle = MetadataTokens.FieldDefinitionHandle((int)f.MDToken.Rid);
        var full = a.Decompiler.DecompileAsString(handle);
        return Paging.ClampText(full, offsetChars, maxChars);
    }

    [McpServerTool(Name = "reverse_il_method")]
    [Description("[REVERSE] Return raw IL for a method (paginated by instruction). Overload selection: see reverse_decompile_method. Params: typeFullName, methodName, asmPath (optional), signature (optional), overloadIndex (optional), offset=0, max=500.")]
    public static object IlMethod(Workspace ws, string typeFullName, string methodName,
                                  string? asmPath = null, string? signature = null, int? overloadIndex = null,
                                  int offset = 0, int max = 200)
    {
        var a = ws.Get(asmPath);
        var m = ResolveOverload(a.Module, typeFullName, methodName, signature, overloadIndex);
        if (!m.HasBody) return new { total = 0, offset, returned = 0, truncated = false, items = Array.Empty<object>() };
        var rows = m.Body.Instructions.Select(i => new { offset = i.Offset, opCode = i.OpCode.Name, operand = i.Operand?.ToString() });
        return Paging.Page(rows, offset, max);
    }

    [McpServerTool(Name = "reverse_list_overloads")]
    [Description("[REVERSE] Enumerate every overload of methodName on typeFullName. Each row gives the index, full signature, parameter list, return type, and metadata token — feed them back as `overloadIndex` or `signature` to reverse_decompile_method / reverse_il_method. Params: typeFullName, methodName, asmPath (optional), offset=0, max=200.")]
    public static object ListOverloads(Workspace ws, string typeFullName, string methodName,
                                       string? asmPath = null, int offset = 0, int max = 200)
    {
        var a = ws.Get(asmPath);
        var t = ResolveTypeOrThrow(a, typeFullName);
        var overloads = t.Methods.Where(mm => mm.Name == methodName).ToList();
        if (overloads.Count == 0) throw new McpException($"method not found: {typeFullName}::{methodName}");
        var rows = overloads.Select((m, i) => new
        {
            index = i,
            fullName = m.FullName,
            signature = ParamSignature(m),
            parameters = m.Parameters.Where(p => p.IsNormalMethodParameter).Select(p => new { name = p.Name, type = p.Type?.FullName }).ToArray(),
            returnType = m.ReturnType?.FullName,
            token = m.MDToken.Raw,
            isStatic = m.IsStatic,
        });
        return Paging.Page(rows, offset, max);
    }

    // Resolve a method by name + optional signature/overloadIndex. Signature
    // matching is lenient: full "(System.String,System.Int32)" wins, but
    // shorthand like "(string,int)" also matches by basename. When ambiguous
    // and no selector given, the error lists every available signature so the
    // caller can re-issue with overloadIndex or signature.
    private static MethodDef ResolveOverload(ModuleDef module, string typeFullName, string methodName,
                                             string? signature, int? overloadIndex)
    {
        // Same fallback as ResolveTypeOrThrow but standalone since this
        // helper takes a raw ModuleDef (not an OpenedAsm). Tries dnlib
        // FindReflection first, then iterates GetTypes() with FullName
        // separator normalisation.
        var t = module.FindReflection(typeFullName);
        if (t == null)
        {
            var alt = typeFullName.Replace('+', '/');
            foreach (var td in module.GetTypes())
                if (td.FullName == typeFullName || td.FullName == alt) { t = td; break; }
        }
        if (t == null) throw new McpException($"type not found: {typeFullName}");
        var overloads = t.Methods.Where(m => m.Name == methodName).ToList();
        if (overloads.Count == 0)
            throw new McpException($"method not found: {typeFullName}::{methodName}");

        if (overloadIndex.HasValue)
        {
            if (signature != null)
                throw new McpException("pass either signature or overloadIndex, not both");
            int idx = overloadIndex.Value;
            if (idx < 0 || idx >= overloads.Count)
                throw new McpException($"overloadIndex out of range (have {overloads.Count})");
            return overloads[idx];
        }

        if (signature != null)
        {
            var matches = overloads.Where(m => SignatureMatches(m, signature)).ToList();
            if (matches.Count == 0)
                throw AmbiguityError(typeFullName, methodName, overloads, $"signature '{signature}' did not match any overload");
            if (matches.Count > 1)
                throw AmbiguityError(typeFullName, methodName, matches, $"signature '{signature}' matched {matches.Count} overloads (be more specific)");
            return matches[0];
        }

        if (overloads.Count == 1) return overloads[0];
        throw AmbiguityError(typeFullName, methodName, overloads, $"{overloads.Count} overloads — pass `signature` or `overloadIndex`");
    }

    private static McpException AmbiguityError(string typeFullName, string methodName, IList<MethodDef> overloads, string reason)
    {
        var lines = overloads.Select((m, i) => $"  [{i}] {ParamSignature(m)}");
        return new McpException(
            $"{typeFullName}::{methodName} — {reason}.\nAvailable:\n" + string.Join("\n", lines));
    }

    private static bool SignatureMatches(MethodDef m, string signature)
    {
        var sig = NormalizeParams(signature);
        var have = ParamList(m);
        if (sig.SequenceEqual(have, StringComparer.Ordinal)) return true;
        // Lenient: compare by short name only (last "." segment, strip generic
        // arity). Lets callers pass "string" / "int" instead of fully qualified.
        var shortHave = have.Select(ShortTypeName).ToArray();
        var shortSig = sig.Select(ShortTypeName).ToArray();
        return shortSig.SequenceEqual(shortHave, StringComparer.OrdinalIgnoreCase);
    }

    private static string[] ParamList(MethodDef m)
        => m.Parameters.Where(p => p.IsNormalMethodParameter)
            .Select(p => p.Type?.FullName ?? "")
            .ToArray();

    private static string ParamSignature(MethodDef m)
    {
        var ret = m.ReturnType?.FullName ?? "void";
        var ps = string.Join(",", ParamList(m));
        return $"{ret} {m.Name}({ps})";
    }

    private static string[] NormalizeParams(string signature)
    {
        var s = signature.Trim();
        // Accept "(a,b)" or "a,b". Strip outer parens.
        if (s.StartsWith("(") && s.EndsWith(")")) s = s[1..^1];
        if (string.IsNullOrWhiteSpace(s)) return Array.Empty<string>();
        return s.Split(',').Select(p => p.Trim()).ToArray();
    }

    private static string ShortTypeName(string fullName)
    {
        var s = fullName;
        // strip backtick generic arity
        var tick = s.IndexOf('`');
        if (tick >= 0) s = s[..tick];
        var dot = s.LastIndexOf('.');
        if (dot >= 0) s = s[(dot + 1)..];
        // canonical aliases (System.String -> String -> string)
        return s switch
        {
            "String" => "string",
            "Int32" => "int",
            "Int64" => "long",
            "UInt32" => "uint",
            "UInt64" => "ulong",
            "Int16" => "short",
            "UInt16" => "ushort",
            "Byte" => "byte",
            "SByte" => "sbyte",
            "Boolean" => "bool",
            "Char" => "char",
            "Single" => "float",
            "Double" => "double",
            "Decimal" => "decimal",
            "Object" => "object",
            "Void" => "void",
            _ => s.ToLowerInvariant(),
        };
    }

    [McpServerTool(Name = "reverse_il_method_by_token")]
    [Description("[REVERSE] Return IL for a method identified by its metadata token (paginated). Params: token (uint — decimal or hex string e.g. '0x06000123'), asmPath (optional), offset=0, max=500.")]
    public static object IlByToken(Workspace ws, JsonNum token, string? asmPath = null, int offset = 0, int max = 200)
    {
        var a = ws.Get(asmPath);
        var tok = token.AsUInt32("token");
        var md = a.Module.ResolveToken(tok) as MethodDef
            ?? throw new McpException($"method not found for token 0x{tok:X8}");
        if (!md.HasBody) return new { total = 0, offset, returned = 0, truncated = false, items = Array.Empty<object>() };
        var rows = md.Body.Instructions.Select(i => new { offset = i.Offset, opCode = i.OpCode.Name, operand = i.Operand?.ToString() });
        return Paging.Page(rows, offset, max);
    }

    [McpServerTool(Name = "reverse_find_string")]
    [Description("[REVERSE] Find methods whose IL loads a string literal (ldstr) matching the pattern. Default scope: EVERY currently-opened assembly (cross-DLL). Pass asmPath to limit to a single one. Matching is case-sensitive substring by default; pass regex=true for .NET regex. Always returns the full matched literal in 'value' plus the assembly it lives in. Paginated. Params: needle (required), regex=false, asmPath (optional, defaults to all-opened), offset=0, max=100.")]
    public static object FindString(Workspace ws, string needle, bool regex = false, string? asmPath = null, int offset = 0, int max = 100)
    {
        if (string.IsNullOrEmpty(needle)) throw new McpException("needle must be non-empty");
        // asmPath semantics: null / empty = cross-DLL (all opened); otherwise
        // limited to that one. We validate the limit path exists so a typo
        // doesn't silently fall through to "found nothing".
        string? scope = null;
        if (!string.IsNullOrEmpty(asmPath))
            scope = ws.Get(asmPath).Path;
        // Compile regex here so the caller gets a descriptive error rather
        // than an opaque RegexParseException from the enumerator.
        if (regex)
        {
            try { _ = new Regex(needle, RegexOptions.Compiled); }
            catch (ArgumentException ex) { throw new McpException($"invalid regex '{needle}': {ex.Message}"); }
        }
        var rows = ws.Index.FindString(needle, regex, scope)
            .Select(h => new { asm = h.AsmPath, type = h.TypeFullName, method = h.MethodName, fullName = h.MethodFullName, ilOffset = h.IlOffset, value = h.Value });
        return Paging.Page(rows, offset, max);
    }

    [McpServerTool(Name = "reverse_xref_to_method")]
    [Description("[REVERSE] Find every member that calls the given method, using dnSpy's MethodUsedByNode + ScopedWhereUsedAnalyzer engine (cross-DLL, accessibility-aware: Private/Internal/Public scoping, TypeRef pre-filter, friend-assembly handling, type-equivalence, virtual-dispatch awareness). Scope: every currently-opened assembly. targetFullName accepts full signature ('System.Int32 Ns.Type::Method(System.Int32)') OR shorthand 'Ns.Type.Method' (matches any overload, AND any per-module instance when the same DLL is opened from multiple paths). Response rows include the assembly each caller lives in. Paginated. Params: targetFullName (required), asmPath (optional — if given, only resolve the target method definition within this one asm; scope of caller search still spans all opened asms), offset=0, max=200.")]
    public static object XrefToMethod(Workspace ws, string targetFullName, string? asmPath = null, int offset = 0, int max = 200)
    {
        if (string.IsNullOrEmpty(targetFullName))
            throw new McpException("targetFullName must be non-empty");
        // Each resolved MethodDef is run through MethodUsedByNode separately;
        // RunAnalyzer dedupes callers across every node by IMemberRef.
        // isSetter=false because the analyzer uses that flag only for
        // property/event accessor disambiguation — irrelevant for plain
        // method targets.
        return RunAnalyzer(ws, ResolveMethods(ws, targetFullName, asmPath),
            $"method not found: {targetFullName}",
            m => new[] { (SearchNode)new MethodUsedByNode(m, isSetter: false) }, offset, max);
    }

    // Look up methods matching either a full signature ('T Ns.Type::M(args)')
    // or shorthand ('Ns.Type.M' / 'M'). Yields every match — the caller
    // (xref) feeds each to the analyzer separately, so multiple per-module
    // instances or multiple overloads under the same shorthand all get
    // analyzed and the result-set is unioned + deduped.
    private static IEnumerable<MethodDef> ResolveMethods(Workspace ws, string target, string? onlyAsmPath)
    {
        bool isSignature = target.Contains("::");
        string? declTypeName = null;
        string? methodName = null;
        if (!isSignature)
        {
            int dot = target.LastIndexOf('.');
            if (dot > 0)
            {
                declTypeName = target.Substring(0, dot);
                methodName = target.Substring(dot + 1);
            }
            else
            {
                methodName = target;
            }
        }

        IEnumerable<Workspace.OpenedAsm> scope = ws.All;
        if (!string.IsNullOrEmpty(onlyAsmPath))
            scope = new[] { ws.Get(onlyAsmPath) };

        foreach (var asm in scope)
        {
            foreach (var t in asm.Module.GetTypes())
            {
                if (declTypeName != null && t.FullName != declTypeName) continue;
                foreach (var m in t.Methods)
                {
                    if (isSignature ? m.FullName == target : m.Name == methodName)
                        yield return m;
                }
            }
        }
    }


    // ---- shared helpers for analyzer-node tools (Phase 5+) -----------------

    /// <summary>Resolve every TypeDef matching <paramref name="typeFullName"/> across the workspace (or only inside <paramref name="onlyAsmPath"/>).</summary>
    private static IEnumerable<TypeDef> ResolveTypes(Workspace ws, string typeFullName, string? onlyAsmPath)
    {
        IEnumerable<Workspace.OpenedAsm> scope = ws.All;
        if (!string.IsNullOrEmpty(onlyAsmPath))
            scope = new[] { ws.Get(onlyAsmPath) };
        foreach (var asm in scope)
            foreach (var t in asm.Module.GetTypes())
                if (t.FullName == typeFullName) yield return t;
    }

    /// <summary>Resolve fields matching either a full sig (<c>T A.B::F</c>) or shorthand (<c>A.B.F</c>).</summary>
    private static IEnumerable<FieldDef> ResolveFields(Workspace ws, string target, string? onlyAsmPath)
    {
        bool isSignature = target.Contains("::");
        string? declTypeName = null;
        string? fieldName = null;
        if (!isSignature)
        {
            int dot = target.LastIndexOf('.');
            if (dot > 0) { declTypeName = target.Substring(0, dot); fieldName = target.Substring(dot + 1); }
            else fieldName = target;
        }
        IEnumerable<Workspace.OpenedAsm> scope = ws.All;
        if (!string.IsNullOrEmpty(onlyAsmPath))
            scope = new[] { ws.Get(onlyAsmPath) };
        foreach (var asm in scope)
            foreach (var t in asm.Module.GetTypes())
            {
                if (declTypeName != null && t.FullName != declTypeName) continue;
                foreach (var f in t.Fields)
                    if (isSignature ? f.FullName == target : f.Name == fieldName)
                        yield return f;
            }
    }

    /// <summary>Resolve properties on a given type (<c>typeFullName</c> + <c>name</c>).</summary>
    private static IEnumerable<PropertyDef> ResolveProperties(Workspace ws, string typeFullName, string name, string? onlyAsmPath)
    {
        foreach (var t in ResolveTypes(ws, typeFullName, onlyAsmPath))
            foreach (var p in t.Properties)
                if (p.Name == name) yield return p;
    }

    /// <summary>Resolve events on a given type.</summary>
    private static IEnumerable<EventDef> ResolveEvents(Workspace ws, string typeFullName, string name, string? onlyAsmPath)
    {
        foreach (var t in ResolveTypes(ws, typeFullName, onlyAsmPath))
            foreach (var e in t.Events)
                if (e.Name == name) yield return e;
    }

    /// <summary>
    /// Map a dnSpy <see cref="EntityNode"/> result row into the JSON shape
    /// every analyzer-driven MCP tool returns. EntityNode.Member is the
    /// site that uses the target; SourceRef (when present) carries the
    /// containing method + IL offset of the actual instruction.
    /// </summary>
    internal static object MapEntityRow(EntityNode en)
    {
        var member = en.Member;
        var src = en.SourceRef;
        var declType = member?.DeclaringType?.FullName;
        var asmPath = member?.Module?.Location ?? src?.Method?.Module?.Location ?? "";
        return new
        {
            asm = asmPath,
            declaringType = declType,
            memberName = member?.Name?.ToString(),
            memberFullName = member?.FullName,
            token = member?.MDToken.Raw,
            inMethod = src?.Method?.FullName,
            ilOffset = src?.ILOffset,
        };
    }

    /// <summary>
    /// Run dnSpy's analyzer node(s) for every resolved target, dedupe hits by
    /// reference, and return a paged response. Centralises the boilerplate
    /// shared by every analyzer-backed reverse_* tool: resolution, error
    /// when nothing matched, drive, dedupe, map, page.
    /// </summary>
    private static object RunAnalyzer<T>(
        Workspace ws,
        IEnumerable<T> resolved,
        string notFoundLabel,
        Func<T, IEnumerable<SearchNode>> nodeFactory,
        int offset, int max)
    {
        var list = resolved.ToList();
        if (list.Count == 0) throw new McpException(notFoundLabel);
        var hits = new List<object>();
        var seen = new HashSet<IMemberRef>(MemberRefComparer.Instance);
        foreach (var target in list)
        {
            foreach (var node in nodeFactory(target))
                foreach (var en in AnalyzerDriver.Drive(node, ws, CancellationToken.None))
                {
                    if (en.Member is null || !seen.Add(en.Member)) continue;
                    hits.Add(MapEntityRow(en));
                }
        }
        return Paging.Page(hits, offset, max);
    }

    [McpServerTool(Name = "reverse_xref_to_type")]
    [Description("[REVERSE] Find every member (method/field/property/event/type) that references the given type — base/interface/field-type/parameter/return/local/catch/typeof/cast/CustomAttribute. Powered by dnSpy's TypeUsedByNode + ScopedWhereUsedAnalyzer engine across all opened assemblies. Params: typeFullName (e.g. 'Ns.MyClass'), asmPath (optional), offset=0, max=200.")]
    public static object XrefToType(Workspace ws, string typeFullName, string? asmPath = null, int offset = 0, int max = 200)
        => RunAnalyzer(ws, ResolveTypes(ws, typeFullName, asmPath),
            $"type not found: {typeFullName}",
            t => new[] { (SearchNode)new TypeUsedByNode(t) }, offset, max);

    [McpServerTool(Name = "reverse_xref_to_field")]
    [Description("[REVERSE] Find every method that reads or writes a field. Powered by dnSpy's FieldAccessNode + ScopedWhereUsedAnalyzer engine. Pass writesOnly=true to restrict to write sites (stfld/stsfld); default returns both reads and writes. fieldFullName accepts full sig 'T A.B::F' or shorthand 'A.B.F'. Params: fieldFullName, writesOnly=false, asmPath (optional), offset=0, max=200.")]
    public static object XrefToField(Workspace ws, string fieldFullName, bool writesOnly = false, string? asmPath = null, int offset = 0, int max = 200)
        => RunAnalyzer(ws, ResolveFields(ws, fieldFullName, asmPath),
            $"field not found: {fieldFullName}",
            f => writesOnly
                ? new[] { (SearchNode)new FieldAccessNode(f, showWrites: true) }
                : new[] { (SearchNode)new FieldAccessNode(f, showWrites: true), new FieldAccessNode(f, showWrites: false) },
            offset, max);

    [McpServerTool(Name = "reverse_xref_type_instantiations")]
    [Description("[REVERSE] Find every newobj / .ctor invocation that constructs an instance of the given type. Powered by dnSpy's TypeInstantiationsNode. Distinct from xref_to_type which catches all references. Params: typeFullName, asmPath (optional), offset=0, max=200.")]
    public static object XrefTypeInstantiations(Workspace ws, string typeFullName, string? asmPath = null, int offset = 0, int max = 200)
        => RunAnalyzer(ws, ResolveTypes(ws, typeFullName, asmPath),
            $"type not found: {typeFullName}",
            t => new[] { (SearchNode)new TypeInstantiationsNode(t) }, offset, max);

    // ---- Phase 5c: subtypes + override chains ------------------------------

    [McpServerTool(Name = "reverse_subtypes")]
    [Description("[REVERSE] Find every type that derives from the given base type or implements the given interface. Powered by dnSpy's SubtypesNode. Params: typeFullName, asmPath (optional), offset=0, max=200.")]
    public static object Subtypes(Workspace ws, string typeFullName, string? asmPath = null, int offset = 0, int max = 200)
        => RunAnalyzer(ws, ResolveTypes(ws, typeFullName, asmPath),
            $"type not found: {typeFullName}",
            t => new[] { (SearchNode)new SubtypesNode(t) }, offset, max);

    [McpServerTool(Name = "reverse_method_overrides")]
    [Description("[REVERSE] Find every method that overrides the given virtual method (i.e., methods in derived types that this method's overrides). Powered by dnSpy's MethodOverridesNode. Params: targetFullName (full sig or shorthand), asmPath (optional), offset=0, max=200.")]
    public static object MethodOverrides(Workspace ws, string targetFullName, string? asmPath = null, int offset = 0, int max = 200)
        => RunAnalyzer(ws, ResolveMethods(ws, targetFullName, asmPath),
            $"method not found: {targetFullName}",
            m => new[] { (SearchNode)new MethodOverridesNode(m) }, offset, max);

    [McpServerTool(Name = "reverse_method_overridden_by_base")]
    [Description("[REVERSE] Find every base-class virtual method that the given method overrides (the method's `base.X` chain). Powered by dnSpy's MethodOverriddenNode. Params: targetFullName, asmPath (optional), offset=0, max=200.")]
    public static object MethodOverriddenBy(Workspace ws, string targetFullName, string? asmPath = null, int offset = 0, int max = 200)
        => RunAnalyzer(ws, ResolveMethods(ws, targetFullName, asmPath),
            $"method not found: {targetFullName}",
            m => new[] { (SearchNode)new MethodOverriddenNode(m) }, offset, max);

    [McpServerTool(Name = "reverse_property_overrides")]
    [Description("[REVERSE] Find every property that overrides the given virtual property. Powered by dnSpy's PropertyOverridesNode. Params: typeFullName, name, asmPath (optional), offset=0, max=200.")]
    public static object PropertyOverrides(Workspace ws, string typeFullName, string name, string? asmPath = null, int offset = 0, int max = 200)
        => RunAnalyzer(ws, ResolveProperties(ws, typeFullName, name, asmPath),
            $"property not found: {typeFullName}::{name}",
            p => new[] { (SearchNode)new PropertyOverridesNode(p) }, offset, max);

    [McpServerTool(Name = "reverse_property_overridden_by_base")]
    [Description("[REVERSE] Find the base-class virtual property the given property overrides. Powered by dnSpy's PropertyOverriddenNode. Params: typeFullName, name, asmPath (optional), offset=0, max=200.")]
    public static object PropertyOverriddenBy(Workspace ws, string typeFullName, string name, string? asmPath = null, int offset = 0, int max = 200)
        => RunAnalyzer(ws, ResolveProperties(ws, typeFullName, name, asmPath),
            $"property not found: {typeFullName}::{name}",
            p => new[] { (SearchNode)new PropertyOverriddenNode(p) }, offset, max);

    [McpServerTool(Name = "reverse_event_overrides")]
    [Description("[REVERSE] Find every event that overrides the given virtual event. Powered by dnSpy's EventOverridesNode. Params: typeFullName, name, asmPath (optional), offset=0, max=200.")]
    public static object EventOverrides(Workspace ws, string typeFullName, string name, string? asmPath = null, int offset = 0, int max = 200)
        => RunAnalyzer(ws, ResolveEvents(ws, typeFullName, name, asmPath),
            $"event not found: {typeFullName}::{name}",
            e => new[] { (SearchNode)new EventOverridesNode(e) }, offset, max);

    [McpServerTool(Name = "reverse_event_overridden_by_base")]
    [Description("[REVERSE] Find the base-class virtual event the given event overrides. Powered by dnSpy's EventOverriddenNode. Params: typeFullName, name, asmPath (optional), offset=0, max=200.")]
    public static object EventOverriddenBy(Workspace ws, string typeFullName, string name, string? asmPath = null, int offset = 0, int max = 200)
        => RunAnalyzer(ws, ResolveEvents(ws, typeFullName, name, asmPath),
            $"event not found: {typeFullName}::{name}",
            e => new[] { (SearchNode)new EventOverriddenNode(e) }, offset, max);

    // ---- Phase 5d: outgoing calls + attribute applied-to ------------------

    [McpServerTool(Name = "reverse_method_calls")]
    [Description("[REVERSE] List every method/field/property/event the given method REFERENCES from its body — the inverse of xref_to_method (this is calls FROM the target). Powered by dnSpy's MethodUsesNode. Params: targetFullName (full sig or shorthand), asmPath (optional), offset=0, max=200.")]
    public static object MethodCalls(Workspace ws, string targetFullName, string? asmPath = null, int offset = 0, int max = 200)
        => RunAnalyzer(ws, ResolveMethods(ws, targetFullName, asmPath),
            $"method not found: {targetFullName}",
            m => new[] { (SearchNode)new MethodUsesNode(m) }, offset, max);

    // ---- Phase 5e: property/event/interface-impl/type-exposed/extensions --

    [McpServerTool(Name = "reverse_xref_to_property")]
    [Description("[REVERSE] Find every member that calls the getter or setter of a property. Internally runs MethodUsedByNode on each accessor (matching dnSpy's 'Analyze' panel for properties). Params: typeFullName, name, asmPath (optional), offset=0, max=200.")]
    public static object XrefToProperty(Workspace ws, string typeFullName, string name, string? asmPath = null, int offset = 0, int max = 200)
        => RunAnalyzer(ws, ResolveProperties(ws, typeFullName, name, asmPath),
            $"property not found: {typeFullName}::{name}",
            p =>
            {
                var nodes = new List<SearchNode>();
                if (p.GetMethod != null) nodes.Add(new MethodUsedByNode(p.GetMethod, isSetter: false));
                if (p.SetMethod != null) nodes.Add(new MethodUsedByNode(p.SetMethod, isSetter: true));
                return nodes;
            }, offset, max);

    [McpServerTool(Name = "reverse_xref_to_event")]
    [Description("[REVERSE] Find every member that subscribes/unsubscribes/raises a given event. Internally runs MethodUsedByNode on add / remove / raise accessors. Params: typeFullName, name, asmPath (optional), offset=0, max=200.")]
    public static object XrefToEvent(Workspace ws, string typeFullName, string name, string? asmPath = null, int offset = 0, int max = 200)
        => RunAnalyzer(ws, ResolveEvents(ws, typeFullName, name, asmPath),
            $"event not found: {typeFullName}::{name}",
            e =>
            {
                var nodes = new List<SearchNode>();
                if (e.AddMethod != null) nodes.Add(new MethodUsedByNode(e.AddMethod, isSetter: false));
                if (e.RemoveMethod != null) nodes.Add(new MethodUsedByNode(e.RemoveMethod, isSetter: false));
                if (e.InvokeMethod != null) nodes.Add(new MethodUsedByNode(e.InvokeMethod, isSetter: false));
                return nodes;
            }, offset, max);

    [McpServerTool(Name = "reverse_event_fired_by")]
    [Description("[REVERSE] Find every method that raises the given event (callvirt on its Invoke / synthesized backing-delegate Invoke). Powered by dnSpy's EventFiredByNode. Params: typeFullName, name, asmPath (optional), offset=0, max=200.")]
    public static object EventFiredBy(Workspace ws, string typeFullName, string name, string? asmPath = null, int offset = 0, int max = 200)
        => RunAnalyzer(ws, ResolveEvents(ws, typeFullName, name, asmPath),
            $"event not found: {typeFullName}::{name}",
            e => new[] { (SearchNode)new EventFiredByNode(e) }, offset, max);

    [McpServerTool(Name = "reverse_interface_method_implemented_by")]
    [Description("[REVERSE] Given an interface method, find every concrete method implementing it. Powered by dnSpy's InterfaceMethodImplementedByNode. Params: targetFullName, asmPath (optional), offset=0, max=200.")]
    public static object InterfaceMethodImplementedBy(Workspace ws, string targetFullName, string? asmPath = null, int offset = 0, int max = 200)
        => RunAnalyzer(ws, ResolveMethods(ws, targetFullName, asmPath),
            $"method not found: {targetFullName}",
            m => new[] { (SearchNode)new InterfaceMethodImplementedByNode(m) }, offset, max);

    [McpServerTool(Name = "reverse_interface_property_implemented_by")]
    [Description("[REVERSE] Given an interface property, find every concrete property implementing it. Powered by dnSpy's InterfacePropertyImplementedByNode. Params: typeFullName, name, asmPath (optional), offset=0, max=200.")]
    public static object InterfacePropertyImplementedBy(Workspace ws, string typeFullName, string name, string? asmPath = null, int offset = 0, int max = 200)
        => RunAnalyzer(ws, ResolveProperties(ws, typeFullName, name, asmPath),
            $"property not found: {typeFullName}::{name}",
            p => new[] { (SearchNode)new InterfacePropertyImplementedByNode(p) }, offset, max);

    [McpServerTool(Name = "reverse_interface_event_implemented_by")]
    [Description("[REVERSE] Given an interface event, find every concrete event implementing it. Powered by dnSpy's InterfaceEventImplementedByNode. Params: typeFullName, name, asmPath (optional), offset=0, max=200.")]
    public static object InterfaceEventImplementedBy(Workspace ws, string typeFullName, string name, string? asmPath = null, int offset = 0, int max = 200)
        => RunAnalyzer(ws, ResolveEvents(ws, typeFullName, name, asmPath),
            $"event not found: {typeFullName}::{name}",
            e => new[] { (SearchNode)new InterfaceEventImplementedByNode(e) }, offset, max);

    [McpServerTool(Name = "reverse_type_exposed_by")]
    [Description("[REVERSE] Find every method/field/property/event whose PUBLIC SURFACE (parameter / return / property type / field type) exposes the given type — useful for API-surface auditing. Powered by dnSpy's TypeExposedByNode. Params: typeFullName, asmPath (optional), offset=0, max=200.")]
    public static object TypeExposedBy(Workspace ws, string typeFullName, string? asmPath = null, int offset = 0, int max = 200)
        => RunAnalyzer(ws, ResolveTypes(ws, typeFullName, asmPath),
            $"type not found: {typeFullName}",
            t => new[] { (SearchNode)new TypeExposedByNode(t) }, offset, max);

    [McpServerTool(Name = "reverse_type_extension_methods")]
    [Description("[REVERSE] Find every extension method declared `this T x, ...` for the given type. Powered by dnSpy's TypeExtensionMethodsNode. Params: typeFullName, asmPath (optional), offset=0, max=200.")]
    public static object TypeExtensionMethods(Workspace ws, string typeFullName, string? asmPath = null, int offset = 0, int max = 200)
        => RunAnalyzer(ws, ResolveTypes(ws, typeFullName, asmPath),
            $"type not found: {typeFullName}",
            t => new[] { (SearchNode)new TypeExtensionMethodsNode(t) }, offset, max);

    [McpServerTool(Name = "reverse_find_attribute_usage")]
    [Description("[REVERSE] Find every type/method/field/property/event/parameter/return/assembly/module decorated with the given attribute type (and instances of attribute fields/properties). Powered by dnSpy's AttributeAppliedToNode. Use it for surveys like '[Obsolete] usage' or '[Serializable] usage'. Params: attributeTypeFullName (e.g. 'System.ObsoleteAttribute'), asmPath (optional), offset=0, max=200.")]
    public static object FindAttributeUsage(Workspace ws, string attributeTypeFullName, string? asmPath = null, int offset = 0, int max = 200)
        => RunAnalyzer(ws, ResolveTypes(ws, attributeTypeFullName, asmPath),
            $"attribute type not found: {attributeTypeFullName}",
            t => new[] { (SearchNode)new AttributeAppliedToNode(t) }, offset, max);

    // ---- Phase 7: annotation store -----------------------------------------
    // Sidecar-JSON store of user annotations (renames + free-form comments)
    // keyed by metadata token. The store lives at <asm_path>.dnspymcp.json
    // and is refreshed every mutation. Annotations are MCP-visible only —
    // they don't rewrite decompiler output or the on-disk PE/MD; treat
    // them as a curator's notebook bound to a particular DLL.

    [McpServerTool(Name = "reverse_rename_member")]
    [Description("[REVERSE] Record a user-facing rename for a metadata token (type / method / field / property / event). Persists to a sidecar JSON next to the assembly. Useful for tracking 'this method does X' notes across long sessions. Resolves the original name for the response. Params: token (uint — decimal or hex string e.g. '0x06000123'), newName:string, asmPath (optional).")]
    public static object RenameMember(Workspace ws, JsonNum token, string newName, string? asmPath = null)
    {
        if (string.IsNullOrWhiteSpace(newName)) throw new McpException("newName must be non-empty");
        var a = ws.Get(asmPath);
        var tok = token.AsUInt32("token");
        var resolved = a.Module.ResolveToken(tok) as IMemberDef
            ?? a.Module.ResolveToken(tok) as TypeDef
            ?? throw new McpException($"no resolvable member for token 0x{tok:X8}");
        var oldName = (resolved as IFullName)?.FullName ?? resolved.ToString();
        a.Annotations.SetRename(tok, newName);
        return new { ok = true, token = tok, oldName, newName, sidecarPath = a.Annotations.SidecarPath };
    }

    [McpServerTool(Name = "reverse_set_comment")]
    [Description("[REVERSE] Attach a free-form text comment to a metadata token. Persists to the sidecar JSON. Use for 'why does this dispatch on X' notes during reverse-engineering. Pass empty string to clear. Params: token (uint — decimal or hex string), text:string, asmPath (optional).")]
    public static object SetComment(Workspace ws, JsonNum token, string text, string? asmPath = null)
    {
        var a = ws.Get(asmPath);
        var tok = token.AsUInt32("token");
        if (string.IsNullOrEmpty(text))
        {
            var removed = a.Annotations.ClearComment(tok);
            return new { ok = true, token = tok, cleared = removed, sidecarPath = a.Annotations.SidecarPath };
        }
        a.Annotations.SetComment(tok, text);
        return new { ok = true, token = tok, length = text.Length, sidecarPath = a.Annotations.SidecarPath };
    }

    [McpServerTool(Name = "reverse_list_annotations")]
    [Description("[REVERSE] List every recorded rename + comment for an assembly. Each row resolves the token back to a member full name where possible so the listing is self-explanatory. Params: asmPath (optional), offset=0, max=200.")]
    public static object ListAnnotations(Workspace ws, string? asmPath = null, int offset = 0, int max = 200)
    {
        var a = ws.Get(asmPath);
        var rows = new List<object>();
        foreach (var (tok, name) in a.Annotations.AllRenames())
        {
            var member = a.Module.ResolveToken(tok);
            rows.Add(new
            {
                kind = "rename",
                token = tok,
                originalFullName = (member as IFullName)?.FullName ?? member?.ToString(),
                value = name,
            });
        }
        foreach (var (tok, txt) in a.Annotations.AllComments())
        {
            var member = a.Module.ResolveToken(tok);
            rows.Add(new
            {
                kind = "comment",
                token = tok,
                originalFullName = (member as IFullName)?.FullName ?? member?.ToString(),
                value = txt,
            });
        }
        return Paging.Page(rows, offset, max);
    }

    [McpServerTool(Name = "reverse_clear_annotation")]
    [Description("[REVERSE] Remove a single annotation (rename or comment) by token. Pass kind='rename'/'comment'/'all' to choose what to clear. Params: token (uint — decimal or hex string), kind='all', asmPath (optional).")]
    public static object ClearAnnotation(Workspace ws, JsonNum token, string kind = "all", string? asmPath = null)
    {
        var a = ws.Get(asmPath);
        var tok = token.AsUInt32("token");
        bool removedRename = false, removedComment = false;
        if (kind == "rename" || kind == "all") removedRename = a.Annotations.ClearRename(tok);
        if (kind == "comment" || kind == "all") removedComment = a.Annotations.ClearComment(tok);
        return new { ok = true, token = tok, removedRename, removedComment };
    }

    private sealed class MemberRefComparer : IEqualityComparer<IMemberRef>
    {
        public static readonly MemberRefComparer Instance = new();
        public bool Equals(IMemberRef? x, IMemberRef? y) => ReferenceEquals(x, y);
        public int GetHashCode(IMemberRef obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
