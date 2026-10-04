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

    private static bool IsModifierStart(string value, int index)
    {
        if (index > 0 && (char.IsLetterOrDigit(value[index - 1]) || value[index - 1] == '_')) return false;
        return value.AsSpan(index).StartsWith("modreq(", StringComparison.Ordinal) ||
            value.AsSpan(index).StartsWith("modopt(", StringComparison.Ordinal);
    }
}
