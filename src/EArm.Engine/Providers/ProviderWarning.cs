using System;
using System.Collections.Generic;
using EArm.Core.Diagnostics;

namespace EArm.Engine.Providers;

/// <summary>
/// A non-fatal observation a provider made while applying a resource (a probe it had to skip, a leftover it found):
/// <see cref="Code"/> is a stable diagnostic code, <see cref="Message"/> the text for the user. Carried on
/// <see cref="AppliedResource.Warnings"/>; the executor reports each one as a warning diagnostic against the resource.
/// </summary>
public sealed record ProviderWarning(string Code, string Message);

/// <summary>
/// A what-if row carries some provider warnings as plan reasons of the form <c>Code: message</c> (the planning phase has no
/// <see cref="AppliedResource"/> to attach them to). This turns those reasons into warning diagnostics, so what-if and create report
/// them like every other warning.
/// </summary>
public static class ProviderWarningReasons
{
    /// <summary>The codes a provider may put at the start of a plan reason to have it reported as a warning diagnostic.</summary>
    private static readonly string[] ReasonCodes = [EngineDiagnosticCodes.SecretExpiresSoon, EngineDiagnosticCodes.RotationTagNotStamped, EngineDiagnosticCodes.DeprecatedAuthenticationMethodProperty, EngineDiagnosticCodes.PimPolicyNotAvailable, EngineDiagnosticCodes.GroupAdoptedByDisplayName, EngineDiagnosticCodes.ConditionalAccessEnforcementNotAllowed, EngineDiagnosticCodes.PimGroupNotOnboarded];

    /// <summary>The warning diagnostics for the reasons of <paramref name="change"/> that start with a known <c>Code:</c> prefix.</summary>
    public static IReadOnlyList<Diagnostic> DiagnosticsOf(ResourceInstance instance, ResourceChange change)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(change);
        List<Diagnostic>? found = null;
        foreach (string reason in change.Reasons)
        {
            foreach (string code in ReasonCodes)
            {
                if (reason.StartsWith(code + ":", StringComparison.Ordinal))
                {
                    (found ??= []).Add(Diagnostic.Warning(code, $"'{instance.Id.Id}': {reason[(code.Length + 1)..].Trim()}"));
                    break;
                }
            }
        }

        return found ?? [];
    }
}
