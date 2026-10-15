using System;
using System.CommandLine;
using System.Linq;

namespace EArm.Cli;

/// <summary>
/// Restricts a string option to a fixed set of values compared case-insensitively, as the Azure CLI does (<c>--mode complete</c>,
/// <c>-o JSON</c>, <c>--result-format resourceidonly</c>). System.CommandLine's <c>AcceptOnlyFromAmong</c> compares ordinally;
/// the readers of these options already map the value case-insensitively.
/// </summary>
internal static class ChoiceOptions
{
    public static TOption AcceptOnlyFromAmongIgnoreCase<TOption>(this TOption option, params string[] choices)
        where TOption : Option
    {
        ArgumentNullException.ThrowIfNull(option);
        ArgumentNullException.ThrowIfNull(choices);
        option.Validators.Add(result =>
        {
            foreach (var token in result.Tokens)
            {
                if (!choices.Contains(token.Value, StringComparer.OrdinalIgnoreCase))
                {
                    result.AddError($"Argument '{token.Value}' not recognized for {option.Name}. Must be one of: {string.Join(", ", choices.Select(c => $"'{c}'"))}.");
                }
            }
        });
        option.CompletionSources.Add(choices);
        return option;
    }
}
