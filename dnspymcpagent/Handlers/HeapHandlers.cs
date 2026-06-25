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
                        else if (f.ElementType == ClrElementType.Int32) val = obj.ReadField<int>(f.Name);
                        else if (f.ElementType == ClrElementType.UInt32) val = obj.ReadField<uint>(f.Name);
                        else if (f.ElementType == ClrElementType.Int64) val = obj.ReadField<long>(f.Name);
                        else if (f.ElementType == ClrElementType.UInt64) val = obj.ReadField<ulong>(f.Name);
                        else if (f.ElementType == ClrElementType.Boolean) val = obj.ReadField<bool>(f.Name);
                        else if (f.ElementType == ClrElementType.Int16) val = obj.ReadField<short>(f.Name);
                        else if (f.ElementType == ClrElementType.UInt16) val = obj.ReadField<ushort>(f.Name);
                        else if (f.ElementType == ClrElementType.Int8) val = obj.ReadField<sbyte>(f.Name);
                        else if (f.ElementType == ClrElementType.UInt8) val = obj.ReadField<byte>(f.Name);
                        else if (f.ElementType == ClrElementType.NativeInt) val = obj.ReadField<long>(f.Name);
                        else if (f.ElementType == ClrElementType.Pointer) val = obj.ReadField<long>(f.Name);
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
                var arr = obj.AsArray();
                int length = arr.Length;
                var comp = obj.Type.ComponentType;
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
                                default: val = $"<{et}>"; break; // struct/value-type elements: inspect via element fields
                            }
                        }
                    }
                    catch (Exception ex) { val = $"<read error: {ex.Message}>"; }
                    items.Add(new { index = i, value = val });
                }
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
    }
}
