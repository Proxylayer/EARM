using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EArm.Graph.Auth;

/// <summary>
/// The files of the persistent <c>earm</c> token cache that <see cref="GraphAuthFactory.Logout"/> deletes, with the guard that keeps test runs away from
/// the platform cache directories (<see cref="GraphAuthFactory.ForbidPlatformCacheSwitch"/>).
/// </summary>
internal static class TokenCacheFiles
{
    internal static int Delete(IReadOnlyList<string>? explicitDirectories)
    {
        var deleted = 0;
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (explicitDirectories is not null)
        {
            directories.UnionWith(explicitDirectories);
        }

        var platformDirectories = PlatformCacheDirectories();
        if (explicitDirectories is null)
        {
            directories.UnionWith(platformDirectories);
        }

        if (AppContext.TryGetSwitch(GraphAuthFactory.ForbidPlatformCacheSwitch, out var forbidden) && forbidden)
        {
            var platform = new HashSet<string>(platformDirectories.Select(NormalizeDirectory), StringComparer.OrdinalIgnoreCase);
            var hit = directories.FirstOrDefault(d => platform.Contains(NormalizeDirectory(d)));
            if (hit is not null)
            {
                throw new InvalidOperationException($"Logout would delete the real '{GraphAuthFactory.CacheName}' token cache in '{hit}', and the switch '{GraphAuthFactory.ForbidPlatformCacheSwitch}' forbids that (test runs set it). Pass a temp directory as the cache directory.");
            }
        }

        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, GraphAuthFactory.CacheName + "*"))
            {
                var name = Path.GetFileName(file);
                if (!string.Equals(name, GraphAuthFactory.CacheName, StringComparison.OrdinalIgnoreCase) && !name.StartsWith(GraphAuthFactory.CacheName + ".", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best effort: the record is gone, so EARM no longer uses the account.
                }
            }
        }

        return deleted;
    }

    /// <summary>Where Azure.Identity persists the token cache on this platform: <c>.IdentityService</c> under the local application data and the user profile.</summary>
    private static List<string> PlatformCacheDirectories()
    {
        var result = new List<string>();
        foreach (var folder in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.UserProfile })
        {
            var root = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
            if (!string.IsNullOrEmpty(root))
            {
                result.Add(Path.Combine(root, ".IdentityService"));
            }
        }

        return result;
    }

    private static string NormalizeDirectory(string directory)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    }
}
