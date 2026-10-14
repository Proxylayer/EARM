using System;
using System.IO;

namespace EntraArm.Tests.Cli;

/// <summary>
/// A private directory under the system temp path (outside the repository, so a "schemas/2026-10-01" checkout is never found by
/// walking up from it), removed on dispose. Used by the tests that must not touch the real user profile.
/// </summary>
internal sealed class IsolatedDirectory : IDisposable
{
    public IsolatedDirectory(string label)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "entraarm-tests-" + label + "-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts)
    {
        return System.IO.Path.Combine([Path, .. parts]);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
