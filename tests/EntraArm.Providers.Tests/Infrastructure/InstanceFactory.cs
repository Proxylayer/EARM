using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EntraArm.Core;
using EntraArm.Engine.Providers;

namespace EntraArm.Providers.Tests.Infrastructure;

/// <summary>
/// Evaluated <see cref="ResourceInstance"/>s for tests that call a provider's lifecycle methods directly with input
/// the offline validators would stop first (wrong shapes, odd names): a valid declaration is evaluated through a
/// what-if run, then its name and / or properties are replaced.
/// </summary>
internal static class InstanceFactory
{
    /// <summary>The first instance of the what-if run of <paramref name="validResource"/>, with its properties replaced by <paramref name="propertiesJson"/>.</summary>
    public static async Task<ResourceInstance> WithPropertiesAsync(ProviderHost host, string validResource, string propertiesJson, string? name = null)
    {
        var run = await host.WhatIfAsync(host.Resources(validResource));
        var instance = run.Plan.Instances.First();
        instance.Properties = (JsonObject)JsonNode.Parse(propertiesJson)!;
        if (name is not null)
        {
            instance.Id = ResourceIdentifier.Create(instance.Type, name);
        }

        return instance;
    }
}
