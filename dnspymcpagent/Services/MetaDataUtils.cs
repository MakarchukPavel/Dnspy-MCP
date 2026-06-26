using System;
using dndbg.COM.MetaData;
using dndbg.Engine;

namespace DnSpyMcp.Agent.Services;

/// <summary>
/// Helpers that combine several MDAPI calls to answer common questions
/// (full method name, token lookup by name, etc.).
/// </summary>
public static class MetaDataUtils
{
    /// <summary>Return "Namespace.Type::Method" for a method token.</summary>
    public static string FullMethodName(IMetaDataImport mdi, uint methodToken)
    {
        var m = MDAPI.GetMethodName(mdi, methodToken) ?? "?";
        var typeToken = 0x02000000u | MDAPI.GetMethodOwnerRid(mdi, methodToken);
        if (typeToken == 0x02000000u) return m;
        var typeName = FullTypeName(mdi, typeToken);
        return typeName + "::" + m;
    }

    /// <summary>Return "Namespace.Type" (handles nested types with '+' separator).</summary>
    public static string FullTypeName(IMetaDataImport mdi, uint typeToken)
    {
        var name = MDAPI.GetTypeDefName(mdi, typeToken) ?? "?";
        var encl = MDAPI.GetTypeDefEnclosingType(mdi, typeToken);
        if (encl != 0) return FullTypeName(mdi, encl) + "+" + name;
        return name;
    }

    /// <summary>Find a TypeDef token by full name ("Namespace.Type"). Returns 0 if not found.</summary>
    public static uint FindTypeDefByName(IMetaDataImport mdi, string fullName)
    {
        foreach (var token in MDAPI.GetTypeDefTokens(mdi))
        {
            if (token == 0) continue;
            if (FullTypeName(mdi, token) == fullName) return token;
        }
        return 0;
    }

    /// <summary>
    /// Find the first method token on a type whose simple name matches.
    /// If overloadIndex is positive, skip to the Nth overload (0=first).
    /// Returns 0 on failure.
    /// </summary>
    public static uint FindMethodByName(IMetaDataImport mdi, uint typeToken, string methodName, int overloadIndex = 0)
    {
        int seen = 0;
        foreach (var mt in MDAPI.GetMethodTokens(mdi, typeToken))
        {
            var name = MDAPI.GetMethodName(mdi, mt);
            if (!string.Equals(name, methodName, StringComparison.Ordinal)) continue;
            if (seen == overloadIndex) return mt;
            seen++;
        }
        return 0;
    }

    /// <summary>
    /// Find a field token by name on a type. Accepts either the raw field name
    /// or the property name (auto-properties are backed by
    /// <c>&lt;Name&gt;k__BackingField</c>). Returns 0 if not found.
    /// </summary>
    public static uint FindFieldByName(IMetaDataImport mdi, uint typeToken, string name)
    {
        uint backing = 0;
        var backingName = $"<{name}>k__BackingField";
        foreach (var ft in MDAPI.GetFieldTokens(mdi, typeToken))
        {
            var n = MDAPI.GetFieldName(mdi, ft);
            if (string.Equals(n, name, StringComparison.Ordinal)) return ft;
            if (backing == 0 && string.Equals(n, backingName, StringComparison.Ordinal)) backing = ft;
        }
        return backing;
    }
}
