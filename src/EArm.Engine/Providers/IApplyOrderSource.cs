using System.Collections.Generic;

namespace EArm.Engine.Providers;

/// <summary>
/// Optional provider capability: declared instances that must be applied after one instance of the provider's type although the template does not say so (no
/// <c>dependsOn</c>, no reference): a PIM role management policy before the schedule requests of the same role or group, so a request is judged by the
/// declared policy. The dependency graph asks every instance of such a provider once the template's own edges are known (references resolved, before any
/// Graph call) and adds an <c>Ordering</c> edge per instance returned, unless the template already orders the two the other way. Only the immutable parts of
/// the instances may be read (type, ids, declaration, reference occurrences); properties are not evaluated yet.
/// </summary>
public interface IApplyOrderSource
{
    /// <summary>The instances of <paramref name="declared"/> (every expanded instance, <paramref name="instance"/> included) that are applied after <paramref name="instance"/>.</summary>
    IEnumerable<ResourceInstance> AppliesBefore(ResourceInstance instance, IReadOnlyList<ResourceInstance> declared);
}
