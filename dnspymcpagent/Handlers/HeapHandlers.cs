using System;
using System.Collections.Generic;
using System.Linq;
using DnSpyMcp.Agent.Services;
using Microsoft.Diagnostics.Runtime;

namespace DnSpyMcp.Agent.Handlers;

public static class HeapHandlers
{
    public static void Register(Dispatcher d)
    {
        d.Register("heap.find_instances",
            "[DEBUG] Walk the managed heap via ClrMD and return addresses of objects whose type name matches. Params: {typeName:string, max?:int=256}. typeName can be a substring or full name.",
            p =>
            {
                var typeName = Dispatcher.Req<string>(p, "typeName");
                var max = Dispatcher.Opt<int>(p, "max", 256);
                var clr = Program.Session.ClrRuntime;
                var heap = clr.Heap;
                if (!heap.CanWalkHeap) throw new InvalidOperationException("heap not walkable in current state");
                var rows = new List<object>();
                foreach (var obj in heap.EnumerateObjects())
                {
                    if (obj.Type == null) continue;
                    if (obj.Type.Name != null && obj.Type.Name.IndexOf(typeName, StringComparison.Ordinal) >= 0)
                    {
                        rows.Add(new { address = (long)obj.Address, type = obj.Type.Name, size = (long)obj.Size });
                        if (rows.Count >= max) break;
                    }
                }
                return rows;
            });

        d.Register("heap.read_object",
            "[DEBUG] Read the fields of a managed object (address required). Returns per-field [{name, typeName, value}]. Primitives resolved; references returned as address. Params: {address:ulong, maxFields?:int=64}.",
            p =>
            {
                var address = Dispatcher.Req<ulong>(p, "address");
                var maxFields = Dispatcher.Opt<int>(p, "maxFields", 64);
                var heap = Program.Session.ClrRuntime.Heap;
                var obj = heap.GetObject(address);
                if (obj.Type == null) throw new ArgumentException($"no type at 0x{address:X}");
                var fields = new List<object>();
                int count = 0;
                foreach (var f in obj.Type.Fields)
                {
                    if (count >= maxFields) break;
                    object? val = null;
                    try
                    {
                        if (f.IsObjectReference) val = $"0x{obj.ReadObjectField(f.Name).Address:X}";
                        else if (f.ElementType == ClrElementType.String) val = obj.ReadStringField(f.Name);
                        else if (f.ElementType == ClrElementType.Int32) val = ClrStructDecoder.DecorateEnum(f.Type, obj.ReadField<int>(f.Name));
                        else if (f.ElementType == ClrElementType.UInt32) val = ClrStructDecoder.DecorateEnum(f.Type, obj.ReadField<uint>(f.Name));
                        else if (f.ElementType == ClrElementType.Int64) val = ClrStructDecoder.DecorateEnum(f.Type, obj.ReadField<long>(f.Name));
                        else if (f.ElementType == ClrElementType.UInt64) val = ClrStructDecoder.DecorateEnum(f.Type, obj.ReadField<ulong>(f.Name));
                        else if (f.ElementType == ClrElementType.Boolean) val = obj.ReadField<bool>(f.Name);
                        else if (f.ElementType == ClrElementType.Char) val = (int)obj.ReadField<char>(f.Name);
                        else if (f.ElementType == ClrElementType.Int16) val = ClrStructDecoder.DecorateEnum(f.Type, obj.ReadField<short>(f.Name));
                        else if (f.ElementType == ClrElementType.UInt16) val = ClrStructDecoder.DecorateEnum(f.Type, obj.ReadField<ushort>(f.Name));
                        else if (f.ElementType == ClrElementType.Int8) val = ClrStructDecoder.DecorateEnum(f.Type, obj.ReadField<sbyte>(f.Name));
                        else if (f.ElementType == ClrElementType.UInt8) val = ClrStructDecoder.DecorateEnum(f.Type, obj.ReadField<byte>(f.Name));
                        else if (f.ElementType == ClrElementType.Float) val = obj.ReadField<float>(f.Name);
                        else if (f.ElementType == ClrElementType.Double) val = obj.ReadField<double>(f.Name);
                        else if (f.ElementType == ClrElementType.NativeInt) val = obj.ReadField<long>(f.Name);
                        else if (f.ElementType == ClrElementType.Pointer) val = obj.ReadField<long>(f.Name);
                        // Value-type (struct) field: decode Guid/DateTime/enum/decimal or expand fields
                        // instead of the old "<Struct>" placeholder.
                        else if (f.IsValueType) val = ClrStructDecoder.DecodeValueType(obj.ReadValueTypeField(f.Name));
                        else val = $"<{f.ElementType}>";
                    }
                    catch (Exception ex) { val = $"<read error: {ex.Message}>"; }
                    fields.Add(new { name = f.Name, typeName = f.Type?.Name, element = f.ElementType.ToString(), offset = f.Offset, value = val });
                    count++;
                }
                return new
                {
                    address = (long)address,
                    type = obj.Type.Name,
                    size = (long)obj.Size,
                    fields,
                };
            });

        d.Register("heap.read_array",
            "[DEBUG] Read elements of a managed single-dimension array (e.g. a List<T> backing _items, or a T[] field). Primitives are decoded; reference elements return {address,type}; string elements return text; null elements return null. Paged. Params: {address:ulong, offset?:int=0, count?:int=128}.",
            p =>
            {
                var address = Dispatcher.Req<ulong>(p, "address");
                var offset = Dispatcher.Opt<int>(p, "offset", 0);
                var count = Dispatcher.Opt<int>(p, "count", 128);
                if (offset < 0) offset = 0;
                if (count < 0) count = 0;
                var heap = Program.Session.ClrRuntime.Heap;
                var obj = heap.GetObject(address);
                if (obj.Type == null) throw new ArgumentException($"no type at 0x{address:X}");
                if (!obj.Type.IsArray) throw new ArgumentException($"object at 0x{address:X} is not an array (type {obj.Type.Name})");
                var (items, length, et, end) = ReadArrayItems(obj, offset, count);
                return new
                {
                    address = (long)address,
                    type = obj.Type.Name,
                    elementType = et.ToString(),
                    length,
                    offset,
                    returned = items.Count,
                    truncated = end < length,
                    items,
                };
            });

        d.Register("heap.read_collection",
            "[DEBUG] Read the contents of a generic List<T> or Dictionary<K,V> at a managed address — decoded as elements / {key,value} pairs, instead of manually walking _items / entries. Primitives/enums/strings/Guid/DateTime/structs decoded inline; references as {kind:object,type,address}; Dictionary skips removed slots. Paged. Params: {address:ulong, offset?:int=0, count?:int=128}. Other collection types: use heap.read_object + heap.read_array.",
            p =>
            {
                var address = Dispatcher.Req<ulong>(p, "address");
                var offset = Dispatcher.Opt<int>(p, "offset", 0);
                var count = Dispatcher.Opt<int>(p, "count", 128);
                if (offset < 0) offset = 0;
                if (count < 0) count = 0;
                var heap = Program.Session.ClrRuntime.Heap;
                var obj = heap.GetObject(address);
                if (obj.Type == null) throw new ArgumentException($"no type at 0x{address:X}");
                var name = obj.Type.Name ?? "";

                if (name.StartsWith("System.Collections.Generic.List<", StringComparison.Ordinal))
                {
                    var itemsF = FirstField(obj.Type, "_items", "items");
                    var sizeF = FirstField(obj.Type, "_size", "size");
                    if (itemsF == null || sizeF == null) throw new InvalidOperationException($"unrecognized List<T> layout on {name}");
                    int size = obj.ReadField<int>(sizeF.Name);
                    var arrObj = obj.ReadObjectField(itemsF.Name);
                    if (arrObj.IsNull || arrObj.Type == null)
                        return new { address = (long)address, kind = "list", type = name, count = size, offset, returned = 0, truncated = false, items = new List<object>() };
                    var (items, length, et, end) = ReadArrayItems(arrObj, offset, count, logicalLength: size);
                    return new
                    {
                        address = (long)address,
                        kind = "list",
                        type = name,
                        elementType = et.ToString(),
                        count = size,
                        offset,
                        returned = items.Count,
                        truncated = end < size,
                        items,
                    };
                }

                if (name.StartsWith("System.Collections.Generic.Dictionary<", StringComparison.Ordinal))
                {
                    var entriesF = FirstField(obj.Type, "entries", "_entries");
                    var countF = FirstField(obj.Type, "count", "_count");
                    if (entriesF == null || countF == null) throw new InvalidOperationException($"unrecognized Dictionary<K,V> layout on {name}");
                    int hwm = obj.ReadField<int>(countF.Name); // high-water mark of used entry slots
                    var freeF = FirstField(obj.Type, "freeCount", "_freeCount");
                    int freeCount = freeF != null ? obj.ReadField<int>(freeF.Name) : 0;
                    int liveCount = hwm - freeCount;

                    var entriesObj = obj.ReadObjectField(entriesF.Name);
                    var entries = new List<object>();
                    bool truncated = false;
                    if (!entriesObj.IsNull && entriesObj.Type?.ComponentType != null)
                    {
                        var entryType = entriesObj.Type.ComponentType;
                        var hashF = FirstField(entryType, "hashCode", "_hashCode");
                        var keyF = FirstField(entryType, "key", "_key") ?? throw new InvalidOperationException("Dictionary Entry has no 'key' field");
                        var valF = FirstField(entryType, "value", "_value") ?? throw new InvalidOperationException("Dictionary Entry has no 'value' field");
                        int seen = 0, max = count == 0 ? int.MaxValue : count;
                        for (int i = 0; i < hwm; i++)
                        {
                            var elemAddr = entriesObj.Type.GetArrayElementAddress(entriesObj.Address, i);
                            var entryVt = Microsoft.Diagnostics.Runtime.ClrValueType.FromAddress(elemAddr, entryType);
                            if (hashF != null) { try { if (entryVt.ReadField<int>(hashF.Name) < 0) continue; } catch { } } // skip removed/free slots
                            if (seen < offset) { seen++; continue; }
                            if (entries.Count >= max) { truncated = true; break; }
                            entries.Add(new
                            {
                                key = ClrStructDecoder.DecodeValueTypeMember(entryVt, keyF),
                                value = ClrStructDecoder.DecodeValueTypeMember(entryVt, valF),
                            });
                            seen++;
                        }
                    }
                    return new
                    {
                        address = (long)address,
                        kind = "dictionary",
                        type = name,
                        count = liveCount,
                        offset,
                        returned = entries.Count,
                        truncated,
                        entries,
                    };
                }

                throw new ArgumentException($"heap.read_collection supports List<T> and Dictionary<K,V>; got {name}. Use heap.read_object / heap.read_array instead.");
            });

        d.Register("heap.read_string",
            "[DEBUG] Read a System.String at the given managed address. Params: {address:ulong}.",
            p =>
            {
                var address = Dispatcher.Req<ulong>(p, "address");
                var heap = Program.Session.ClrRuntime.Heap;
                var obj = heap.GetObject(address);
                if (obj.Type == null) throw new ArgumentException($"no type at 0x{address:X}");
                return new { address = (long)address, value = obj.AsString(int.MaxValue) };
            });

        d.Register("heap.stats",
            "[DEBUG] Per-type aggregate stats (count + total size) over the managed heap. Params: {top?:int=25}.",
            p =>
            {
                var top = Dispatcher.Opt<int>(p, "top", 25);
                var heap = Program.Session.ClrRuntime.Heap;
                if (!heap.CanWalkHeap) throw new InvalidOperationException("heap not walkable in current state");
                var agg = new Dictionary<string, (int count, ulong size)>();
                foreach (var obj in heap.EnumerateObjects())
                {
                    if (obj.Type == null) continue;
                    var n = obj.Type.Name ?? "<unknown>";
                    if (!agg.TryGetValue(n, out var cur)) cur = (0, 0);
                    agg[n] = (cur.count + 1, cur.size + obj.Size);
                }
                var rows = agg.Select(kv => new { type = kv.Key, count = kv.Value.count, totalSize = (long)kv.Value.size })
                              .OrderByDescending(r => r.totalSize)
                              .Take(top)
                              .ToList();
                return rows;
            });

        d.Register("heap.static_field",
            "[DEBUG] Read a STATIC field of a type via ClrMD — the entry point into singletons (e.g. AppManager.Instance, feature/cache statics). Statics are per-AppDomain. Params: {typeName:string (full type name, e.g. 'Terrasoft.Core.AppConnection'), fieldName:string, appDomainIndex?:int=-1 (default: first AppDomain where the field is initialized)}. Decoded like heap.read_object: primitive/enum/string/Guid/DateTime/struct inline; a reference returns {kind:object,type,address} to drill into with debug_heap_read_object. Returns {type, field, fieldType, appDomain, appDomainIndex, initialized, value}.",
            p =>
            {
                var typeName = Dispatcher.Req<string>(p, "typeName");
                var fieldName = Dispatcher.Req<string>(p, "fieldName");
                var adIndex = Dispatcher.Opt<int>(p, "appDomainIndex", -1);

                var clr = Program.Session.ClrRuntime;
                var type = ClrStructDecoder.ResolveType(typeName)
                    ?? throw new ArgumentException($"type not found (use the full type name): {typeName}");
                var sf = type.GetStaticFieldByName(fieldName)
                    ?? throw new ArgumentException($"static field '{fieldName}' not found on {type.Name}");

                var domains = clr.AppDomains;
                if (domains.Length == 0) throw new InvalidOperationException("no AppDomains");
                Microsoft.Diagnostics.Runtime.ClrAppDomain ad;
                if (adIndex >= 0)
                {
                    if (adIndex >= domains.Length) throw new ArgumentException($"appDomainIndex {adIndex} out of range (0..{domains.Length - 1})");
                    ad = domains[adIndex];
                }
                else
                {
                    ad = domains.FirstOrDefault(d => { try { return sf.IsInitialized(d); } catch { return false; } }) ?? domains[0];
                    adIndex = System.Array.IndexOf(domains.ToArray(), ad);
                }

                bool initialized;
                try { initialized = sf.IsInitialized(ad); } catch { initialized = false; }
                object? value = ReadStaticValue(sf, ad);
                return new
                {
                    type = type.Name,
                    field = fieldName,
                    fieldType = sf.Type?.Name,
                    appDomain = ad.Name,
                    appDomainIndex = adIndex,
                    initialized,
                    value,
                };
            });

        d.Register("heap.references",
            "[DEBUG] List the objects an object points TO (outbound references), with the field/array-element each came from. Use to walk an object graph forward. Params: {address:ulong, max?:int=128}. Returns {address, type, count, references:[{field, isArrayElement, offset, target:{type,address}}]}.",
            p =>
            {
                var address = Dispatcher.Req<ulong>(p, "address");
                var max = Dispatcher.Opt<int>(p, "max", 128);
                var heap = Program.Session.ClrRuntime.Heap;
                var obj = heap.GetObject(address);
                if (obj.Type == null) throw new ArgumentException($"no type at 0x{address:X}");
                var refs = new List<object>();
                foreach (var r in obj.EnumerateReferencesWithFields(true, true))
                {
                    if (refs.Count >= max) break;
                    var o = r.Object;
                    refs.Add(new
                    {
                        field = r.Field?.Name,
                        isArrayElement = r.IsArrayElement,
                        offset = r.Offset,
                        target = o.IsNull ? null : new { type = o.Type?.Name, address = $"0x{o.Address:X}" },
                    });
                }
                return new { address = (long)address, type = obj.Type.Name, count = refs.Count, references = refs };
            });

        d.Register("heap.referencing",
            "[DEBUG] Find which objects REFERENCE a given object (inbound references) — the 'who is keeping this alive / why isn't it collected' tool. Walks the whole managed heap (can be slow on a large process; capped by `max`). Reports the referring object + the field/array-element that points at the target. Params: {address:ulong, max?:int=50}. Returns {target, scanned, returned, truncated, referrers:[{address, type, field, isArrayElement}]}. Pair with heap.references (outbound) to trace retention toward a GC root.",
            p =>
            {
                var target = Dispatcher.Req<ulong>(p, "address");
                var max = Dispatcher.Opt<int>(p, "max", 50);
                if (max <= 0) max = 50;
                var heap = Program.Session.ClrRuntime.Heap;
                if (!heap.CanWalkHeap) throw new InvalidOperationException("heap not walkable in current state");
                var referrers = new List<object>();
                long scanned = 0;
                foreach (var obj in heap.EnumerateObjects())
                {
                    scanned++;
                    if (obj.Type == null || obj.Address == target) continue;
                    foreach (var r in obj.EnumerateReferencesWithFields(false, true))
                    {
                        if (r.Object.Address != target) continue;
                        referrers.Add(new
                        {
                            address = $"0x{obj.Address:X}",
                            type = obj.Type.Name,
                            field = r.Field?.Name,
                            isArrayElement = r.IsArrayElement,
                        });
                        break; // one record per referring object
                    }
                    if (referrers.Count >= max) break;
                }
                return new
                {
                    target = $"0x{target:X}",
                    scanned,
                    returned = referrers.Count,
                    truncated = referrers.Count >= max,
                    referrers,
                };
            });

        d.Register("heap.roots",
            "[DEBUG] Enumerate GC roots — the anchors that keep objects alive: GC handles (Strong/Pinned/Weak/Dependent/AsyncPinned/RefCounted/SizedRef), stack locals of live threads, and the finalizer queue. The starting point for leak analysis: a growing StrongHandle count = a handle leak; many PinnedHandle = heap fragmentation; a static-held graph is rooted here too. Params: {kind?:string (substring on root kind, e.g. 'handle','pinned','stack','strong','finalizer'), typeFilter?:string (substring on the rooted object's type), max?:int=200}. Returns {summary:{kind->count} over ALL roots, scanned, returned, truncated, roots:[{kind, isPinned, isInterior, rootAddress, object:{type,address}}]}. The summary is always complete (counts every root); roots[] is the filtered+capped sample. Use heap.retention_path to see the full chain from a root down to one object.",
            p =>
            {
                var kindFilter = Dispatcher.Opt<string?>(p, "kind", null);
                var typeFilter = Dispatcher.Opt<string?>(p, "typeFilter", null);
                var max = Dispatcher.Opt<int>(p, "max", 200);
                if (max <= 0) max = 200;
                var heap = Program.Session.ClrRuntime.Heap;
                if (!heap.CanWalkHeap) throw new InvalidOperationException("heap not walkable in current state");

                var summary = new Dictionary<string, long>();
                var roots = new List<object>();
                long scanned = 0;
                foreach (var root in heap.EnumerateRoots())
                {
                    scanned++;
                    var kindName = root.RootKind.ToString();
                    summary[kindName] = summary.TryGetValue(kindName, out var c) ? c + 1 : 1;
                    if (roots.Count >= max) continue; // keep tallying the summary, stop collecting rows
                    if (kindFilter != null && kindName.IndexOf(kindFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var o = root.Object;
                    var tn = o.Type?.Name;
                    if (typeFilter != null && (tn == null || tn.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                    roots.Add(new
                    {
                        kind = kindName,
                        isPinned = root.IsPinned,
                        isInterior = root.IsInterior,
                        rootAddress = $"0x{root.Address:X}",
                        @object = o.IsNull ? null : new { type = tn, address = $"0x{o.Address:X}" },
                    });
                }
                return new { summary, scanned, returned = roots.Count, truncated = roots.Count >= max, roots };
            });

        d.Register("heap.retention_path",
            "[DEBUG] Answer 'why is this object still alive?' — find a chain of references from a GC root down to the target object (the managed equivalent of SOS !gcroot / a dotMemory retention path). Heavyweight: builds a reverse-reachability index over the whole heap, so it can be slow on a large process. Params: {address:ulong}. Returns {target, rooted:bool, rootKind?, depth, path:[{address, type, field}]} ordered ROOT -> ... -> target (each hop's `field` is the member on the previous object that points to this one, when resolvable). rooted=false means no root path was found (the object is unreachable / eligible for collection). Typical use: heap.find_instances/stats spots a type whose count keeps growing across snapshots, then retention_path on one instance shows what holds it (e.g. a static cache or an un-removed event handler).",
            p =>
            {
                var addr = Dispatcher.Req<ulong>(p, "address");
                var heap = Program.Session.ClrRuntime.Heap;
                if (!heap.CanWalkHeap) throw new InvalidOperationException("heap not walkable in current state");
                var target = heap.GetObject(addr);
                if (target.Type == null) throw new ArgumentException($"no type at 0x{addr:X}");

                // GCRoot is configured with the target(s); EnumerateRootPaths yields
                // (root, chain) pairs for every GC root that reaches the target. The
                // first path is enough to answer "why is this alive"; the ClrRoot
                // gives us the anchoring root kind directly.
                var gcroot = new GCRoot(heap, new ulong[] { addr });
                string? rootKind = null;
                List<ulong>? addrs = null;
                foreach (var pair in gcroot.EnumerateRootPaths())
                {
                    rootKind = pair.Item1.RootKind.ToString();
                    addrs = new List<ulong>();
                    for (var n = pair.Item2; n != null; n = n.Next) addrs.Add(n.Object);
                    break;
                }
                if (addrs == null || addrs.Count == 0)
                    return new { target = $"0x{addr:X}", rooted = false, rootKind = (string?)null, depth = 0, path = (object)Array.Empty<object>() };

                // Normalize so the path reads root -> ... -> target.
                if (addrs[0] == addr) addrs.Reverse();

                // Resolve each hop's type and the field on the previous object that points here.
                var path = new List<object>();
                for (int i = 0; i < addrs.Count; i++)
                {
                    string? field = null;
                    if (i > 0)
                    {
                        var prev = heap.GetObject(addrs[i - 1]);
                        foreach (var rf in prev.EnumerateReferencesWithFields(false, true))
                            if (rf.Object.Address == addrs[i]) { field = rf.Field?.Name ?? (rf.IsArrayElement ? "[]" : null); break; }
                    }
                    path.Add(new { address = $"0x{addrs[i]:X}", type = heap.GetObject(addrs[i]).Type?.Name, field });
                }
                return new { target = $"0x{addr:X}", rooted = true, rootKind, depth = path.Count, path = (object)path };
            });
    }

    /// <summary>First field on a type matching any of the candidate names (BCL layouts differ across Framework/Core).</summary>
    private static ClrInstanceField? FirstField(ClrType t, params string[] names)
    {
        foreach (var n in names) { var f = t.GetFieldByName(n); if (f != null) return f; }
        return null;
    }

    /// <summary>
    /// Decode elements [offset, offset+count) of a single-dimension array object.
    /// logicalLength caps the effective length (e.g. a List's _size below the
    /// backing array's physical length). Returns (items, length, elementType, end).
    /// </summary>
    private static (List<object> items, int length, ClrElementType et, int end) ReadArrayItems(ClrObject obj, int offset, int count, int? logicalLength = null)
    {
        var arr = obj.AsArray();
        int length = logicalLength ?? arr.Length;
        if (length > arr.Length) length = arr.Length;
        var comp = obj.Type!.ComponentType;
        var et = comp?.ElementType ?? ClrElementType.Unknown;
        bool isRef = et == ClrElementType.Class || et == ClrElementType.Object
                  || et == ClrElementType.Array || et == ClrElementType.SZArray;
        int end = count == 0 ? length : Math.Min(length, offset + count);
        var items = new List<object>();
        for (int i = offset; i < end; i++)
        {
            object? val;
            try
            {
                if (et == ClrElementType.String)
                {
                    var e = arr.GetObjectValue(i);
                    val = e.IsNull ? null : e.AsString(int.MaxValue);
                }
                else if (isRef)
                {
                    var e = arr.GetObjectValue(i);
                    val = e.IsNull ? (object?)null : new { kind = "object", type = e.Type?.Name, address = $"0x{e.Address:X}" };
                }
                else
                {
                    switch (et)
                    {
                        case ClrElementType.Boolean: val = arr.GetValue<bool>(i); break;
                        case ClrElementType.Int8:     val = arr.GetValue<sbyte>(i); break;
                        case ClrElementType.UInt8:    val = arr.GetValue<byte>(i); break;
                        case ClrElementType.Int16:    val = arr.GetValue<short>(i); break;
                        case ClrElementType.UInt16:   val = arr.GetValue<ushort>(i); break;
                        case ClrElementType.Char:     val = (int)arr.GetValue<char>(i); break;
                        case ClrElementType.Int32:    val = arr.GetValue<int>(i); break;
                        case ClrElementType.UInt32:   val = arr.GetValue<uint>(i); break;
                        case ClrElementType.Int64:    val = arr.GetValue<long>(i); break;
                        case ClrElementType.UInt64:   val = arr.GetValue<ulong>(i); break;
                        case ClrElementType.Float:    val = arr.GetValue<float>(i); break;
                        case ClrElementType.Double:   val = arr.GetValue<double>(i); break;
                        case ClrElementType.NativeInt: val = arr.GetValue<long>(i); break;
                        case ClrElementType.Pointer:   val = arr.GetValue<long>(i); break;
                        default:
                            // Struct / value-type elements (incl. enums): decode in place from the element address.
                            if (comp != null && (et == ClrElementType.Struct || comp.IsValueType))
                                val = ClrStructDecoder.DecodeByAddress(obj.Type!.GetArrayElementAddress(obj.Address, i), comp.Name, comp);
                            else val = $"<{et}>";
                            break;
                    }
                }
            }
            catch (Exception ex) { val = $"<read error: {ex.Message}>"; }
            items.Add(new { index = i, value = val });
        }
        return (items, length, et, end);
    }

    /// <summary>Decode a static field's value in an AppDomain, mirroring heap.read_object field decoding.</summary>
    private static object? ReadStaticValue(ClrStaticField sf, Microsoft.Diagnostics.Runtime.ClrAppDomain ad)
    {
        try
        {
            if (sf.IsObjectReference)
            {
                var o = sf.ReadObject(ad);
                if (o.IsNull) return new { kind = "null" };
                if (o.Type?.IsString == true) return new { kind = "string", value = o.AsString(int.MaxValue) };
                return new { kind = "object", type = o.Type?.Name, address = $"0x{o.Address:X}" };
            }
            if (sf.ElementType == ClrElementType.String)
                return new { kind = "string", value = sf.ReadString(ad) };
            if (ClrStructDecoder.IsPrimitive(sf.ElementType))
                return new { kind = "primitive", elementType = sf.ElementType.ToString(), value = ClrStructDecoder.DecorateEnum(sf.Type, ReadStaticPrimitive(sf, ad)) };
            if (sf.IsValueType)
                return ClrStructDecoder.DecodeValueType(sf.ReadStruct(ad));
            return new { kind = "raw", elementType = sf.ElementType.ToString() };
        }
        catch (Exception ex) { return new { kind = "error", error = ex.Message }; }
    }

    private static object? ReadStaticPrimitive(ClrStaticField sf, Microsoft.Diagnostics.Runtime.ClrAppDomain ad) => sf.ElementType switch
    {
        ClrElementType.Boolean   => sf.Read<bool>(ad),
        ClrElementType.Char      => (int)sf.Read<char>(ad),
        ClrElementType.Int8      => sf.Read<sbyte>(ad),
        ClrElementType.UInt8     => sf.Read<byte>(ad),
        ClrElementType.Int16     => sf.Read<short>(ad),
        ClrElementType.UInt16    => sf.Read<ushort>(ad),
        ClrElementType.Int32     => sf.Read<int>(ad),
        ClrElementType.UInt32    => sf.Read<uint>(ad),
        ClrElementType.Int64     => sf.Read<long>(ad),
        ClrElementType.UInt64    => sf.Read<ulong>(ad),
        ClrElementType.Float     => sf.Read<float>(ad),
        ClrElementType.Double    => sf.Read<double>(ad),
        ClrElementType.NativeInt => sf.Read<long>(ad),
        ClrElementType.Pointer   => sf.Read<long>(ad),
        _ => $"<{sf.ElementType}>",
    };
}
