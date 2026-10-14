using System.Threading;
using System.Threading.Tasks;
using EntraArm.Az;
using EntraArm.Core;

namespace EntraArm.Engine.Providers;

/// <summary>
/// Optional provider capability behind <c>reference(resourceId(...), apiVersion)</c> (the ARM "existing resource"
/// idiom): read an object that is not declared in the template by the key its resource id implies.
/// <see cref="ProviderBase"/> implements it for every name-keyed type.
/// </summary>
public interface IExistingResourceLookup
{
    /// <summary>Returns the live object or <c>null</c>; throws <see cref="ProviderException"/> when the id cannot be turned into a key.</summary>
    Task<ExistingResource?> ReadExistingAsync(ResourceIdentifier id, string apiVersion, IGraphClient graph, CancellationToken cancellationToken);
}
