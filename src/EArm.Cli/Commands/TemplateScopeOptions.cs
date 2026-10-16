using System.CommandLine;

namespace EArm.Cli.Commands;

/// <summary>The <c>-f</c> / <c>-p</c> / <c>--verb</c> options shared by <c>auth login</c> and <c>auth status</c>.</summary>
internal sealed record TemplateScopeOptions(Option<string?> TemplateFile, Option<string[]> Parameters, Option<string> Verb)
{
    public static TemplateScopeOptions Create(string purpose)
    {
        var templateFile = new Option<string?>("--template-file", "-f")
        {
            Description = "A template (*.entra.json) whose delegated scopes to " + purpose + " (computed offline, as 'earm permissions --verb' lists them).",
        };
        var parameters = new Option<string[]>("--parameters", "-p")
        {
            Description = "Parameter values of the template: @file.json or name=value. Repeatable; later values win.",
            AllowMultipleArgumentsPerToken = false,
            DefaultValueFactory = _ => [],
        };
        var verb = new Option<string>("--verb")
        {
            Description = "create|whatif|delete: the run of the template whose scopes count (default create).",
            DefaultValueFactory = _ => "create",
        };
        verb.AcceptOnlyFromAmongIgnoreCase("create", "whatif", "delete");
        return new TemplateScopeOptions(templateFile, parameters, verb);
    }

    public void AddTo(Command command)
    {
        command.Options.Add(TemplateFile);
        command.Options.Add(Parameters);
        command.Options.Add(Verb);
    }

    public AuthInvocation Apply(AuthInvocation invocation, ParseResult parseResult)
    {
        return invocation with
        {
            TemplateFile = parseResult.GetValue(TemplateFile),
            Parameters = parseResult.GetValue(Parameters) ?? [],
            Verb = TemplateRunScopes.ParseVerb(parseResult.GetValue(Verb)),
        };
    }
}
