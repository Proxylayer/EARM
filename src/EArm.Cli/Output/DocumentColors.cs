namespace EArm.Cli.Output;

/// <summary>
/// The ANSI colours of <c>-o jsonc</c> and <c>-o yamlc</c> (keys cyan, strings green, numbers, booleans and null magenta); each is one of the sequences
/// <see cref="ConsoleSanitizer"/> lets through. Used only when standard output is a terminal and colours are not switched off; otherwise the plain
/// <c>json</c> / <c>yaml</c> is written, as the Azure CLI does.
/// </summary>
internal static class DocumentColors
{
    public const string Key = "\u001b[36m";

    public const string String = "\u001b[32m";

    public const string Literal = "\u001b[35m";

    public const string Reset = "\u001b[0m";

    public static string Paint(string text, string colour, bool enabled)
    {
        return enabled ? colour + text + Reset : text;
    }
}
