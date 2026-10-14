using System;

namespace EntraArm.Core;

/// <summary>
/// A parameter-file Key Vault reference (<c>reference.keyVault.id</c> + <c>secretName</c>). Resolution
/// (<c>az keyvault secret show</c>) is a later work item; the loader only parses it.
/// </summary>
public sealed record KeyVaultReference(string KeyVaultId, string SecretName, string? SecretVersion = null)
{
    /// <summary>The vault name: the last segment of <see cref="KeyVaultId"/>, or the id itself when it is a bare name.</summary>
    public string VaultName
    {
        get
        {
            string trimmed = KeyVaultId.Trim().TrimEnd('/');
            int slash = trimmed.LastIndexOf('/');
            return slash < 0 ? trimmed : trimmed[(slash + 1)..];
        }
    }

    public override string ToString()
    {
        return SecretVersion is null
            ? $"keyVault '{VaultName}' secret '{SecretName}'"
            : $"keyVault '{VaultName}' secret '{SecretName}' version '{SecretVersion}'";
    }

    public static KeyVaultReference Create(string keyVaultId, string secretName, string? secretVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyVaultId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);
        return new KeyVaultReference(keyVaultId, secretName, string.IsNullOrWhiteSpace(secretVersion) ? null : secretVersion);
    }
}
