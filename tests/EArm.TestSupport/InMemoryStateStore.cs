using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine.State;

namespace EArm.TestSupport;

/// <summary>
/// An <see cref="IStateStore"/> that keeps states in memory, round-tripping every save through
/// <see cref="StateSerializer"/> (so the secret guard and the JSON mapping are exercised exactly as with the file
/// store) and recording each saved document for assertions ("saved after every resource").
/// </summary>
public sealed class InMemoryStateStore : IStateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StateLockInfo> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _saves = [];
    private readonly TimeProvider _time;

    public InMemoryStateStore(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Every serialised document, in save order.</summary>
    public IReadOnlyList<string> Saves
    {
        get
        {
            lock (_gate)
            {
                return _saves.ToList();
            }
        }
    }

    /// <summary>The current JSON of a deployment's state, or <c>null</c>.</summary>
    public string? DocumentOf(string deploymentName)
    {
        lock (_gate)
        {
            return _documents.TryGetValue(deploymentName, out var json) ? json : null;
        }
    }

    public Task<StateFile?> LoadAsync(string deploymentName, CancellationToken cancellationToken)
    {
        StateNames.Validate(deploymentName);
        lock (_gate)
        {
            return Task.FromResult(_documents.TryGetValue(deploymentName, out var json) ? StateSerializer.Deserialize(json, deploymentName) : null);
        }
    }

    public Task SaveAsync(StateFile state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        StateNames.Validate(state.DeploymentName);
        string json = StateSerializer.Serialize(state);
        lock (_gate)
        {
            _documents[state.DeploymentName] = json;
            _saves.Add(json);
        }

        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string deploymentName, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_documents.Remove(deploymentName));
        }
    }

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<string>>(_documents.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList());
        }
    }

    public Task<IAsyncDisposable> LockAsync(string deploymentName, TimeSpan wait, CancellationToken cancellationToken)
    {
        StateNames.Validate(deploymentName);
        lock (_gate)
        {
            if (_locks.TryGetValue(deploymentName, out var holder))
            {
                throw new StateLockedException(deploymentName, "memory://" + deploymentName + ".lock", holder);
            }

            _locks[deploymentName] = new StateLockInfo(Environment.ProcessId, Environment.MachineName, Environment.UserName, _time.GetUtcNow());
        }

        return Task.FromResult<IAsyncDisposable>(new Lease(this, deploymentName));
    }

    public Task<StateLockInfo?> GetLockAsync(string deploymentName, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_locks.TryGetValue(deploymentName, out var holder) ? holder : null);
        }
    }

    public Task<StateLockInfo?> ForceUnlockAsync(string deploymentName, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_locks.Remove(deploymentName, out var holder))
            {
                return Task.FromResult<StateLockInfo?>(holder);
            }

            return Task.FromResult<StateLockInfo?>(null);
        }
    }

    private sealed class Lease(InMemoryStateStore owner, string name) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            lock (owner._gate)
            {
                owner._locks.Remove(name);
            }

            return ValueTask.CompletedTask;
        }
    }
}
