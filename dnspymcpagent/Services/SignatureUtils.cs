using dndbg.COM.MetaData;
using dndbg.Engine;

namespace DnSpyMcp.Agent.Services;

/// <summary>
/// Minimal method-signature blob reader — enough to select an overload by
/// parameter count and generic arity (ECMA-335 II.23.2.1). Used by func-eval
/// (eval.call) to pick the right overload for a given (#args, #typeArgs).
/// </summary>
public static class SignatureUtils
{
    /// <summary>
    /// Read the generic arity and parameter count of a method from its signature
    /// blob. paramCount excludes the implicit `this`. Returns false if the blob
    /// is unavailable/malformed.
    /// </summary>
    public static bool TryGetMethodArity(IMetaDataImport mdi, uint methodToken, out int genArity, out int paramCount)
    {
        genArity = 0;
        paramCount = 0;
        var blob = MDAPI.GetMethodSignatureBlob(mdi, methodToken);
        if (blob == null || blob.Length < 2) return false;
        int pos = 0;
        byte callConv = blob[pos++];
        // GENERIC (0x10): a compressed generic-parameter count follows the calling convention.
        if ((callConv & 0x10) != 0)
        {
            if (!TryReadCompressed(blob, ref pos, out genArity)) return false;
        }
        if (!TryReadCompressed(blob, ref pos, out paramCount)) return false;
        return true;
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
