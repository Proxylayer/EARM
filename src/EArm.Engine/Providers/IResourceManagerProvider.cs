namespace EArm.Engine.Providers;

/// <summary>
/// Marks a provider whose calls go to Azure Resource Manager (<c>https://management.azure.com</c> in the public cloud) instead of Microsoft Graph, for example
/// the PIM settings of an Azure role at a subscription, resource group or management group scope. Such a run needs the separate Resource Manager token
/// (<see cref="EArm.Graph.GraphCloud.ResourceManagerScopes"/>), and a delegated sign-in its one-time consent
/// (<see cref="EArm.Graph.GraphCloud.ResourceManagerConsentScopes"/>, <c>user_impersonation</c>): <see cref="Permissions.RunScopes"/> sets
/// <see cref="Permissions.RunScopeSet.NeedsResourceManager"/> when a declared resource's provider implements it, which <c>earm auth login -f</c>,
/// <c>earm auth status -f</c> and <c>earm permissions</c> report like the Key Vault need.
/// </summary>
public interface IResourceManagerProvider
{
}
