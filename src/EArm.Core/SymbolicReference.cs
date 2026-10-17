using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace EArm.Core;

/// <summary>
/// A languageVersion 2.0 reference to a resource by symbolic name, as <c>dependsOn</c>, <c>reference()</c> and <c>references()</c> take it:
/// <c>app</c> names the resource (or, for a copy loop, the whole collection) and <c>apps[2]</c> one iteration of the loop <c>apps</c>
/// (the form Bicep emits for <c>apps[i]</c>: <c>format('apps[{0}]', i)</c>). Symbolic names are case-sensitive.
/// </summary>
public static partial class SymbolicReference
{
    [GeneratedRegex(@"^(?<name>[A-Za-z0-9_]+)(\[(?<index>[0-9]{1,9})\])?$")]
    private static partial Regex Pattern();

    /// <summary>Splits <paramref name="text"/> into a symbolic name and an optional copy index; false when it is not of that shape.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out string? name, out long? index)
    {
        name = null;
        index = null;
        if (text is null)
        {
            return false;
        }

        var match = Pattern().Match(text);
        if (!match.Success)
        {
            return false;
        }

        name = match.Groups["name"].Value;
        if (match.Groups["index"].Success)
        {
            index = long.Parse(match.Groups["index"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        return true;
    }
}
