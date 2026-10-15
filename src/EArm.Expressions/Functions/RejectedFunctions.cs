using System.Collections.Generic;

namespace EArm.Expressions.Functions;

/// <summary>
/// ARM functions with no Entra meaning, registered as <see cref="FunctionKind.Rejected"/>: calling one is an
/// <c>UnsupportedArmFunction</c> error whose message names the alternative.
/// </summary>
internal static class RejectedFunctions
{
    /// <summary>Rejected ARM functions and the message explaining the Entra alternative.</summary>
    public static readonly IReadOnlyDictionary<string, string> Rejected = new Dictionary<string, string>
    {
        ["resourceGroup"] = "Entra objects are tenant-scoped and have no resource group; use tenant() for tenant information.",
        ["subscription"] = "Entra objects are tenant-scoped and have no subscription; use tenant() for tenant information.",
        ["managementGroup"] = "Entra objects are tenant-scoped; there are no management groups. Use tenant().",
        ["extensionResourceId"] = "EARM resources have no extension scope; use resourceId(type, name[, childName]).",
        ["subscriptionResourceId"] = "EARM resource ids are tenant-scoped; use resourceId(type, name[, childName]).",
        ["tenantResourceId"] = "EARM resource ids are already tenant-scoped; use resourceId(type, name[, childName]).",
        ["listKeys"] = "Entra objects expose no listable keys; secrets are created through Microsoft.Entra/applications/passwordCredentials and read through reference(...).secretText in the creating run.",
        ["listSecrets"] = "Entra objects expose no listable secrets; use Microsoft.Entra/applications/passwordCredentials and reference(...).secretText.",
        ["providers"] = "EARM has a single provider namespace (Microsoft.Entra); the supported types are fixed by the catalog.",
        ["pickZones"] = "Entra objects have no availability zones.",
    };

    public static void Register(FunctionRegistry registry)
    {
        foreach (var pair in Rejected)
        {
            registry.Register(new FunctionDescriptor { Name = pair.Key, Group = "rejected", Kind = FunctionKind.Rejected, Note = pair.Value });
        }
    }
}
