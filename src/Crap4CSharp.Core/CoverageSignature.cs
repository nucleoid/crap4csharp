using System.Text;

namespace Crap4CSharp.Core;

internal static class CoverageSignature
{
    public static string StripCustomModifiers(string value)
    {
        if (!value.Contains("modreq(", StringComparison.Ordinal) &&
            !value.Contains("modopt(", StringComparison.Ordinal)) return value;
        var output = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length;)
        {
            if (!IsModifierStart(value, index))
            {
                output.Append(value[index++]);
                continue;
            }

            var cursor = index + 6;
            var depth = 0;
            do
            {
                if (value[cursor] == '(') depth++;
                else if (value[cursor] == ')') depth--;
                cursor++;
            } while (cursor < value.Length && depth > 0);
            if (depth != 0)
            {
                output.Append(value, index, value.Length - index);
                break;
            }
            index = cursor;
        }
        return output.ToString();
    }

    public static bool TryGetParameterContents(string signature, out string contents)
    {
        signature = StripCustomModifiers(signature);
        var open = signature.IndexOf('(');
        if (open < 0)
        {
            contents = string.Empty;
            return false;
        }
        var depth = 0;
        for (var index = open; index < signature.Length; index++)
        {
            if (signature[index] == '(') depth++;
            else if (signature[index] == ')') depth--;
            if (depth < 0) break;
            if (depth != 0) continue;
            if (signature.AsSpan(index + 1).Trim().Length > 0) break;
            contents = signature[(open + 1)..index];
            return true;
        }
        contents = string.Empty;
        return false;
    }

    private static bool IsModifierStart(string value, int index)
    {
        if (index == 0 || !char.IsWhiteSpace(value[index - 1])) return false;
        return value.AsSpan(index).StartsWith("modreq(", StringComparison.Ordinal) ||
            value.AsSpan(index).StartsWith("modopt(", StringComparison.Ordinal);
    }
}
