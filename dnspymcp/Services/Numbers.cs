using System.Globalization;
using ModelContextProtocol;

namespace DnSpyMcp.Services;

/// <summary>
/// Parses a string-form number with auto base detection. Recognised prefixes:
///   0x / 0X  → hex
///   0o / 0O  → octal
///   0b / 0B  → binary
///   (none)   → decimal
/// A leading <c>-</c> sign is honoured for signed parses. Whitespace is
/// trimmed; <c>_</c> separators are stripped. Throws <see cref="McpException"/>
/// with the parameter name baked into the message so the LLM sees exactly
/// which field went wrong.
///
/// MCP tool parameters that carry addresses, metadata tokens, or file/IL
/// offsets take a plain <c>string</c> and call into here. We deliberately
/// don't wrap in a struct with a JsonConverter — System.Text.Json's schema
/// exporter then emits <c>true</c> (any-of) for the type, and the Anthropic
/// API rejects the whole tool catalog when it sees a non-object schema in
/// inputSchema.properties.
/// </summary>
public static class Numbers
{
    public static ulong ParseUInt64(string raw, string field)
    {
        var (body, neg, baseN) = Strip(raw, field);
        if (neg) throw new McpException($"{field}: negative value not allowed for an unsigned 64-bit number ('{raw}').");
        return baseN switch
        {
            10 => ulong.TryParse(body, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                  ? v
                  : throw new McpException($"{field}: cannot parse '{raw}' as a decimal unsigned integer."),
            16 => ulong.TryParse(body, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)
                  ? h
                  : throw new McpException($"{field}: cannot parse '{raw}' as a hex unsigned integer."),
            _  => ParseFromDigits(body, baseN, field, raw),
        };
    }

    public static long ParseInt64(string raw, string field)
    {
        var (body, neg, baseN) = Strip(raw, field);
        if (baseN == 10)
        {
            if (!long.TryParse((neg ? "-" : "") + body, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                throw new McpException($"{field}: cannot parse '{raw}' as a decimal signed integer.");
            return v;
        }
        // For non-decimal bases parse unsigned bits then re-sign-bit the result.
        var bits = baseN == 16
            ? (ulong.TryParse(body, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)
                ? h
                : throw new McpException($"{field}: cannot parse '{raw}' as a hex signed integer."))
            : ParseFromDigits(body, baseN, field, raw);
        long signed = unchecked((long)bits);
        return neg ? -signed : signed;
    }

    public static uint ParseUInt32(string raw, string field)
    {
        var v = ParseUInt64(raw, field);
        if (v > uint.MaxValue) throw new McpException($"{field}: '{raw}' overflows 32-bit unsigned range.");
        return (uint)v;
    }

    public static int ParseInt32(string raw, string field)
    {
        var v = ParseInt64(raw, field);
        if (v is < int.MinValue or > int.MaxValue) throw new McpException($"{field}: '{raw}' overflows 32-bit signed range.");
        return (int)v;
    }

    private static (string body, bool negative, int baseN) Strip(string raw, string field)
    {
        if (raw is null) throw new McpException($"{field}: value is null.");
        var s = raw.Trim().Replace("_", "");
        if (s.Length == 0) throw new McpException($"{field}: value is empty.");
        bool neg = false;
        if (s[0] is '+' or '-')
        {
            neg = s[0] == '-';
            s = s[1..];
        }
        int baseN = 10;
        if (s.Length >= 2 && s[0] == '0')
        {
            switch (s[1])
            {
                case 'x' or 'X': baseN = 16; s = s[2..]; break;
                case 'o' or 'O': baseN = 8;  s = s[2..]; break;
                case 'b' or 'B': baseN = 2;  s = s[2..]; break;
            }
        }
        if (s.Length == 0) throw new McpException($"{field}: '{raw}' has a base prefix but no digits.");
        return (s, neg, baseN);
    }

    private static ulong ParseFromDigits(string digits, int baseN, string field, string raw)
    {
        ulong value = 0;
        foreach (var ch in digits)
        {
            int d = ch switch
            {
                >= '0' and <= '9' => ch - '0',
                >= 'a' and <= 'f' => 10 + (ch - 'a'),
                >= 'A' and <= 'F' => 10 + (ch - 'A'),
                _ => -1,
            };
            if (d < 0 || d >= baseN)
                throw new McpException($"{field}: invalid digit '{ch}' for {DescBase(baseN)} ('{raw}').");
            // overflow check: value * baseN + d must fit ulong
            if (value > (ulong.MaxValue - (ulong)d) / (ulong)baseN)
                throw new McpException($"{field}: '{raw}' overflows 64-bit unsigned range.");
            value = value * (ulong)baseN + (ulong)d;
        }
        return value;
    }

    private static string DescBase(int b) => b switch
    {
        2 => "binary",
        8 => "octal",
        10 => "decimal",
        16 => "hex",
        _ => $"base-{b}",
    };
}
