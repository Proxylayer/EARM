using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using EArm.Graph;
using EArm.Graph.Auth;

namespace EArm.Cli.Hosting;

/// <summary>
/// The test seam of the composition root: in-process CLI tests replace the HTTP handler, the token provider, the retry delays, the cloud and the
/// sign-in store, either per invocation (<see cref="ConnectionSettings.Overrides"/>) or for the current async flow (<see cref="UseAmbient"/>, which is what
/// a test uses when it drives the whole command line). Nothing here is reachable from the command line or the environment.
/// </summary>
internal sealed class ServiceOverrides
{
    private static readonly AsyncLocal<ServiceOverrides?> AmbientValue = new();

    /// <summary>Handler behind the Graph and Key Vault <see cref="HttpClient"/>; null uses the real one.</summary>
    public HttpMessageHandler? Handler { get; init; }

    /// <summary>Token provider used instead of the credentials of <see cref="GraphAuthFactory"/>.</summary>
    public IGraphTokenProvider? Tokens { get; init; }

    /// <summary>Replaces the factory that builds credentials and performs <c>auth login</c> / <c>logout</c>.</summary>
    public GraphAuthFactory? AuthFactory { get; init; }

    /// <summary>Where the sign-in record lives (a temp path in tests); also given to the default factory.</summary>
    public AuthRecordStore? RecordStore { get; init; }

    /// <summary>
    /// The token-cache directories the default factory's <c>auth logout</c> deletes <c>earm</c> cache files from (a temp folder in tests); null means
    /// the platform locations of the real cache.
    /// </summary>
    public IReadOnlyList<string>? TokenCacheDirectories { get; init; }

    /// <summary>Waits between retries; tests pass a zero-delay one.</summary>
    public IDelayProvider? Delay { get; init; }

    public TimeProvider? Time { get; init; }

    public GraphCloud? Cloud { get; init; }

    /// <summary>The overrides installed for the current async flow, or null.</summary>
    public static ServiceOverrides? Ambient => AmbientValue.Value;

    /// <summary>Installs <paramref name="overrides"/> for the current async flow until the returned scope is disposed.</summary>
    public static IDisposable UseAmbient(ServiceOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        var previous = AmbientValue.Value;
        AmbientValue.Value = overrides;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly ServiceOverrides? _previous;

        public Scope(ServiceOverrides? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            AmbientValue.Value = _previous;
        }
    }
}
