using System.Globalization;

namespace EntraArm.Core.Diagnostics;

/// <summary>
/// A 1-based line and column (in characters) inside a source file.
/// </summary>
public readonly record struct SourceLocation(int Line, int Column)
{
    public override string ToString()
    {
        return Line.ToString(CultureInfo.InvariantCulture) + ":" + Column.ToString(CultureInfo.InvariantCulture);
    }
}
