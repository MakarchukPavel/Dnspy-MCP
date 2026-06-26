using System;
using System.Collections.Generic;
using Microsoft.Diagnostics.Runtime;

namespace DnSpyMcp.Agent.Services;

/// <summary>
/// Decodes managed value types (structs) into JSON-friendly values instead of
/// the opaque "&lt;Struct&gt;" / "&lt;ValueType&gt;" placeholders the raw readers
/// produce. Three layers, cheapest first:
///   1. Well-known BCL structs (Guid, DateTime, DateTimeOffset, TimeSpan,
///      decimal) — read straight off the target's memory as the real .NET type
///      via <see cref="IMemoryReader.Read{T}(ulong)"/> (their layout is a fixed
///      blob of unmanaged fields, so a raw reinterpret IS the value).
///   2. Enums — read the underlying integer and map it to the member name.
///   3. Any other struct — expand one level into its fields via ClrMD.
///
/// Used by both the heap readers (which already hold a <see cref="ClrType"/>)
/// and the frame readers (which only know the type *name* from dndbg and so
/// resolve the <see cref="ClrType"/> by name). All paths funnel through
/// <see cref="DecodeByAddress"/> so the two readers agree on shape.
/// </summary>
public static class ClrStructDecoder
{
    private const int MaxStructFields = 32;

    // Process-lifetime cache of name -> ClrType (non-null hits only). The agent
    // is one process per debug session, so this never has to be invalidated.
    private static readonly Dictionary<string, ClrType> _typeByName = new(StringComparer.Ordinal);

    private static IDataReader Reader => Program.Session.ClrRuntime.DataTarget.DataReader;

    /// <summary>
    /// When <paramref name="fieldType"/> is an enum, replace a raw numeric value
    /// with {enumValue,name,type}; otherwise return <paramref name="numericValue"/>
    /// unchanged. Lets the heap/array readers keep their fast primitive path and
    /// only pay for enum mapping when the field is actually an enum.
    /// </summary>
    public static object? DecorateEnum(ClrType? fieldType, object? numericValue)
    {
        if (fieldType == null || numericValue == null) return numericValue;
        if (!SafeIsEnum(fieldType)) return numericValue;
        // Same shape as the value-type enum path in DecodeByAddress so callers
        // see one consistent {kind:"enum", type, value, name} regardless of
        // whether the enum surfaced as a primitive field or an inline struct.
        return new { kind = "enum", type = fieldType.Name, value = numericValue, name = EnumName(fieldType, numericValue) };
    }

    /// <summary>Decode a struct already materialized as a ClrValueType (heap path).</summary>
    public static object DecodeValueType(ClrValueType vt, int depth = 0)
    {
        var t = vt.Type;
        if (t == null) return new { kind = "struct", note = "no type" };
        return DecodeByAddress(vt.Address, t.Name, t, depth);
    }

    /// <summary>
    /// Decode a value type given its address and (optionally) its type name and
    /// ClrType. <paramref name="typeName"/> drives the well-known fast path;
    /// <paramref name="clrType"/> (resolved by name if null) drives enum/struct
    /// expansion. The frame reader calls this with only a name from dndbg.
    /// </summary>
    public static object DecodeByAddress(ulong address, string? typeName, ClrType? clrType, int depth = 0)
    {
        if (address == 0) return new { kind = "struct", type = typeName, note = "null address" };

        // 1. Well-known BCL structs: raw reinterpret off target memory.
        var wk = TryWellKnown(typeName, address);
        if (wk != null) return wk;

        // 2/3 need a ClrType. Resolve by name if the caller didn't hand us one.
        clrType ??= ResolveType(typeName);
        if (clrType == null)
            return new { kind = "struct", type = typeName, address = (long)address, note = "type unresolved" };

        if (SafeIsEnum(clrType))
        {
            var num = ReadEnumNumeric(clrType, address);
            return new { kind = "enum", type = clrType.Name, value = num, name = num == null ? null : EnumName(clrType, num) };
        }

        return ExpandStruct(clrType, address, depth);
    }

    // ---- well-known structs -------------------------------------------------

    private static object? TryWellKnown(string? typeName, ulong addr)
    {
        if (!IsWellKnown(typeName)) return null;
        try
        {
            var kind = typeName switch
            {
                "System.Guid" => "Guid",
                "System.DateTime" => "DateTime",
                "System.TimeSpan" => "TimeSpan",
                "System.DateTimeOffset" => "DateTimeOffset",
                "System.Decimal" => "decimal",
                _ => "struct",
            };
            return new { kind, type = typeName, value = TryWellKnownText(typeName, addr) };
        }
        catch (Exception ex)
        {
            return new { kind = "struct", type = typeName, address = (long)addr, note = $"read error: {ex.Message}" };
        }
    }

    /// <summary>True for the BCL structs we decode by raw reinterpret.</summary>
    public static bool IsWellKnown(string? typeName) => typeName is
        "System.Guid" or "System.DateTime" or "System.TimeSpan"
        or "System.DateTimeOffset" or "System.Decimal";

    /// <summary>
    /// Render a well-known BCL struct at <paramref name="addr"/> as its text
    /// value (Guid/DateTime/TimeSpan/DateTimeOffset/decimal), or null if the
    /// type isn't one of them. Shared by the heap/frame decoders and by
    /// value-based breakpoint conditions (e.g. arg0.UId == "&lt;guid&gt;").
    /// </summary>
    public static string? TryWellKnownText(string? typeName, ulong addr)
    {
        var r = Reader;
        return typeName switch
        {
            "System.Guid"           => r.Read<Guid>(addr).ToString(),
            "System.DateTime"       => r.Read<DateTime>(addr).ToString("o"),
            "System.TimeSpan"       => r.Read<TimeSpan>(addr).ToString(),
            "System.DateTimeOffset" => r.Read<DateTimeOffset>(addr).ToString("o"),
            "System.Decimal"        => r.Read<decimal>(addr).ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    // ---- generic struct expansion -------------------------------------------

    private static object ExpandStruct(ClrType t, ulong address, int depth)
    {
        var vt = ClrValueType.FromAddress(address, t);
        var fields = new List<object>();
        int n = 0;
        foreach (var f in t.Fields)
        {
            if (n >= MaxStructFields) break;
            object? val;
            try
            {
                if (f.IsObjectReference)
                {
                    var o = vt.ReadObjectField(f.Name);
                    val = o.IsNull ? null : new { kind = "object", type = o.Type?.Name, address = $"0x{o.Address:X}" };
                }
                else if (f.ElementType == ClrElementType.String)
                {
                    val = vt.ReadStringField(f.Name);
                }
                else if (IsPrimitive(f.ElementType))
                {
                    val = DecorateEnum(f.Type, ReadPrimitiveField(vt, f));
                }
                else if (depth < 1 && f.Type != null)
                {
                    // One level of nesting (e.g. DateTimeOffset._dateTime).
                    val = DecodeByAddress(vt.ReadValueTypeField(f.Name).Address, f.Type.Name, f.Type, depth + 1);
                }
                else
                {
                    val = $"<{f.ElementType}>";
                }
            }
            catch (Exception ex) { val = $"<read error: {ex.Message}>"; }
            fields.Add(new { name = f.Name, typeName = f.Type?.Name, value = val });
            n++;
        }
        return new { kind = "struct", type = t.Name, fields };
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>
    /// Resolve a field by either its raw name or its property name —
    /// auto-properties are backed by <c>&lt;Name&gt;k__BackingField</c>, so this
    /// lets callers (conditions, eval) use the natural <c>obj.Value</c> rather
    /// than the mangled backing-field name. Searches the type (incl. inherited).
    /// </summary>
    public static ClrInstanceField? FindField(ClrType t, string name)
        => t.GetFieldByName(name) ?? t.GetFieldByName($"<{name}>k__BackingField");

    public static bool IsPrimitive(ClrElementType et) => et switch
    {
        ClrElementType.Boolean or ClrElementType.Char
        or ClrElementType.Int8 or ClrElementType.UInt8
        or ClrElementType.Int16 or ClrElementType.UInt16
        or ClrElementType.Int32 or ClrElementType.UInt32
        or ClrElementType.Int64 or ClrElementType.UInt64
        or ClrElementType.Float or ClrElementType.Double
        or ClrElementType.NativeInt or ClrElementType.NativeUInt
        or ClrElementType.Pointer => true,
        _ => false,
    };

    private static object? ReadPrimitiveField(ClrValueType vt, ClrInstanceField f) => f.ElementType switch
    {
        ClrElementType.Boolean   => vt.ReadField<bool>(f.Name),
        ClrElementType.Char      => (int)vt.ReadField<char>(f.Name),
        ClrElementType.Int8      => vt.ReadField<sbyte>(f.Name),
        ClrElementType.UInt8     => vt.ReadField<byte>(f.Name),
        ClrElementType.Int16     => vt.ReadField<short>(f.Name),
        ClrElementType.UInt16    => vt.ReadField<ushort>(f.Name),
        ClrElementType.Int32     => vt.ReadField<int>(f.Name),
        ClrElementType.UInt32    => vt.ReadField<uint>(f.Name),
        ClrElementType.Int64     => vt.ReadField<long>(f.Name),
        ClrElementType.UInt64    => vt.ReadField<ulong>(f.Name),
        ClrElementType.Float     => vt.ReadField<float>(f.Name),
        ClrElementType.Double    => vt.ReadField<double>(f.Name),
        ClrElementType.NativeInt => vt.ReadField<long>(f.Name),
        ClrElementType.NativeUInt => vt.ReadField<ulong>(f.Name),
        ClrElementType.Pointer   => vt.ReadField<long>(f.Name),
        _ => $"<{f.ElementType}>",
    };

    private static long? ReadEnumNumeric(ClrType enumType, ulong addr)
    {
        try
        {
            var r = Reader;
            var et = SafeEnumElementType(enumType);
            return et switch
            {
                ClrElementType.Boolean => r.Read<byte>(addr),
                ClrElementType.Char    => r.Read<ushort>(addr),
                ClrElementType.Int8    => r.Read<sbyte>(addr),
                ClrElementType.UInt8   => r.Read<byte>(addr),
                ClrElementType.Int16   => r.Read<short>(addr),
                ClrElementType.UInt16  => r.Read<ushort>(addr),
                ClrElementType.Int32   => r.Read<int>(addr),
                ClrElementType.UInt32  => r.Read<uint>(addr),
                ClrElementType.Int64   => r.Read<long>(addr),
                ClrElementType.UInt64  => unchecked((long)r.Read<ulong>(addr)),
                _ => r.Read<int>(addr),
            };
        }
        catch { return null; }
    }

    public static string? EnumName(ClrType enumType, object numericValue)
    {
        try
        {
            long target = Convert.ToInt64(numericValue);
            foreach (var (name, value) in enumType.AsEnum().EnumerateValues())
            {
                try { if (Convert.ToInt64(value) == target) return name; }
                catch { /* incomparable underlying type — skip */ }
            }
        }
        catch { /* AsEnum/EnumerateValues unavailable */ }
        return null; // unmatched (e.g. [Flags] combination) -> caller keeps the numeric value
    }

    private static bool SafeIsEnum(ClrType t)
    {
        try { return t.IsEnum; } catch { return false; }
    }

    private static ClrElementType SafeEnumElementType(ClrType t)
    {
        try { return t.AsEnum().ElementType; } catch { return ClrElementType.Int32; }
    }

    /// <summary>Resolve a full type name to a ClrType by scanning loaded modules (cached).</summary>
    public static ClrType? ResolveType(string? fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return null;
        if (_typeByName.TryGetValue(fullName!, out var cached)) return cached;
        try
        {
            foreach (var module in Program.Session.ClrRuntime.EnumerateModules())
            {
                ClrType? t = null;
                try { t = module.GetTypeByName(fullName!); } catch { }
                if (t != null) { _typeByName[fullName!] = t; return t; }
            }
        }
        catch { }
        return null;
    }
}
