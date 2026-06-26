using dndbg.COM.CorDebug;
using dndbg.COM.MetaData;
using dndbg.Engine;

namespace DnSpyMcp.Agent.Services;

/// <summary>
/// Minimal method-signature blob reader (ECMA-335 II.23.2.1) — enough to select
/// an overload by generic arity, parameter count, and each parameter's leading
/// element type. Used by func-eval (eval.call) to pick the right overload for a
/// given (#args, #typeArgs, arg literal kinds).
/// </summary>
public static class SignatureUtils
{
    /// <summary>
    /// Generic arity + parameter count of a method (paramCount excludes the
    /// implicit `this`). Returns false if the blob is unavailable/malformed.
    /// </summary>
    public static bool TryGetMethodArity(IMetaDataImport mdi, uint methodToken, out int genArity, out int paramCount)
    {
        genArity = 0;
        paramCount = 0;
        var blob = MDAPI.GetMethodSignatureBlob(mdi, methodToken);
        if (blob == null || blob.Length < 2) return false;
        int pos = 0;
        byte callConv = blob[pos++];
        if ((callConv & 0x10) != 0 && !TryReadCompressed(blob, ref pos, out genArity)) return false;
        return TryReadCompressed(blob, ref pos, out paramCount);
    }

    /// <summary>
    /// Generic arity + the leading <see cref="CorElementType"/> of each parameter
    /// (after custom modifiers / BYREF). Lets the caller score overloads by
    /// argument type (e.g. a string literal prefers a STRING parameter over a
    /// CLASS one). Returns false if the signature can't be fully parsed (the
    /// caller then falls back to arity-only matching).
    /// </summary>
    public static bool TryGetMethodSig(IMetaDataImport mdi, uint methodToken, out int genArity, out CorElementType[] paramTypes)
    {
        genArity = 0;
        paramTypes = System.Array.Empty<CorElementType>();
        var blob = MDAPI.GetMethodSignatureBlob(mdi, methodToken);
        if (blob == null || blob.Length < 2) return false;
        int pos = 0;
        byte callConv = blob[pos++];
        if ((callConv & 0x10) != 0 && !TryReadCompressed(blob, ref pos, out genArity)) return false;
        if (!TryReadCompressed(blob, ref pos, out int paramCount)) return false;

        // Return type.
        if (!SkipCustomMods(blob, ref pos) || !SkipType(blob, ref pos)) return false;

        var list = new CorElementType[paramCount];
        for (int i = 0; i < paramCount; i++)
        {
            if (!SkipCustomMods(blob, ref pos)) return false;
            int lead = pos;
            if (lead < blob.Length && (CorElementType)blob[lead] == CorElementType.ByRef) lead++;
            if (lead >= blob.Length) return false;
            list[i] = (CorElementType)blob[lead];
            if (!SkipType(blob, ref pos)) return false;
        }
        paramTypes = list;
        return true;
    }

    // ---- signature-blob walking ---------------------------------------------

    private static bool SkipCustomMods(byte[] b, ref int pos)
    {
        while (pos < b.Length)
        {
            var et = (CorElementType)b[pos];
            if (et != CorElementType.CModReqd && et != CorElementType.CModOpt) break;
            pos++;
            if (!TryReadCompressed(b, ref pos, out _)) return false;
        }
        return true;
    }

    // Advance pos past one Type (ECMA-335 II.23.2.12). Returns false on a shape
    // we don't model (ARRAY w/ shape, FNPTR) so the caller can fall back.
    private static bool SkipType(byte[] b, ref int pos)
    {
        if (pos >= b.Length) return false;
        var et = (CorElementType)b[pos++];
        switch (et)
        {
            case CorElementType.Void:
            case CorElementType.Boolean:
            case CorElementType.Char:
            case CorElementType.I1: case CorElementType.U1:
            case CorElementType.I2: case CorElementType.U2:
            case CorElementType.I4: case CorElementType.U4:
            case CorElementType.I8: case CorElementType.U8:
            case CorElementType.R4: case CorElementType.R8:
            case CorElementType.I: case CorElementType.U:
            case CorElementType.String:
            case CorElementType.Object:
            case CorElementType.TypedByRef:
                return true;
            case CorElementType.ValueType:
            case CorElementType.Class:
            case CorElementType.Var:
            case CorElementType.MVar:
                return TryReadCompressed(b, ref pos, out _);
            case CorElementType.ByRef:
            case CorElementType.Ptr:
            case CorElementType.SZArray:
            case CorElementType.Pinned:
                return SkipCustomMods(b, ref pos) && SkipType(b, ref pos);
            case CorElementType.CModReqd:
            case CorElementType.CModOpt:
                return TryReadCompressed(b, ref pos, out _) && SkipType(b, ref pos);
            case CorElementType.GenericInst:
            {
                if (pos >= b.Length) return false;
                pos++; // CLASS or VALUETYPE marker
                if (!TryReadCompressed(b, ref pos, out _)) return false;      // generic type token
                if (!TryReadCompressed(b, ref pos, out int n)) return false;  // arg count
                for (int i = 0; i < n; i++) if (!SkipType(b, ref pos)) return false;
                return true;
            }
            default:
                return false; // ARRAY (shape), FNPTR, anything unmodeled
        }
    }

    // ECMA-335 II.23.2 compressed unsigned integer.
    private static bool TryReadCompressed(byte[] b, ref int pos, out int value)
    {
        value = 0;
        if (pos >= b.Length) return false;
        byte b0 = b[pos++];
        if ((b0 & 0x80) == 0) { value = b0; return true; }
        if ((b0 & 0xC0) == 0x80)
        {
            if (pos >= b.Length) return false;
            value = ((b0 & 0x3F) << 8) | b[pos++];
            return true;
        }
        if ((b0 & 0xE0) == 0xC0)
        {
            if (pos + 2 >= b.Length) return false;
            value = ((b0 & 0x1F) << 24) | (b[pos++] << 16) | (b[pos++] << 8) | b[pos++];
            return true;
        }
        return false;
    }
}
