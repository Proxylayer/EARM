using System;

namespace EntraArm.Providers;

/// <summary>
/// Binds a provider class to the resource type it implements, so <see cref="ProviderDiscovery"/> can register it
/// without a shared registration file: <c>[EntraProvider("Microsoft.Entra/applications")]</c>. A class may carry the
/// attribute more than once when one implementation serves several catalog types.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class EntraProviderAttribute : Attribute
{
    public EntraProviderAttribute(string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        TypeName = typeName;
    }

    /// <summary>The full resource type, for example <c>Microsoft.Entra/applications</c>.</summary>
    public string TypeName { get; }
}
