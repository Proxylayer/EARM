using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using EArm.Core;

namespace EArm.Expressions.Evaluation;

/// <summary>
/// The resources a template declares, as far as their identities are known: used by <c>reference(name)</c>
/// (bare-name lookup), by same-template checks of <c>reference(resourceId(...))</c>, and by <c>dependsOn</c>
/// resolution. An <see cref="Entry.Id"/> may be symbolic (a name segment that is an
/// <see cref="Values.UnknownValue.Symbol"/>) when the name is only known at deploy time.
/// <para>
/// A <c>copy</c> loop is registered by its literal <c>copy.name</c> (<see cref="CopyLoops"/>) so that <c>dependsOn</c> can
/// name it. When its count is computable from parameters and variables the static evaluator also indexes every instance
/// (the name evaluated with a concrete <c>copyIndex()</c>), so <c>reference()</c> can name one instance from anywhere in
/// the template, as ARM allows; otherwise the loop is listed in <see cref="PendingCopyLoops"/> and the engine adds the
/// concrete iteration ids once the loop is expanded.
/// </para>
/// </summary>
public sealed class TemplateResourceIndex
{
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<ResourceIdentifier, Entry> _byId = [];
    private readonly List<CopyLoop> _loops = [];

    public static TemplateResourceIndex Empty { get; } = new();

    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>The copy loops of the template by literal <c>copy.name</c>, whether or not their iterations have ids yet.</summary>
    public IReadOnlyList<CopyLoop> CopyLoops => _loops;

    /// <summary>One indexed resource. <see cref="CopyName"/> is the resource's <c>copy.name</c> when it has one.</summary>
    public sealed record Entry(ResourceIdentifier Id, bool IsSymbolic, ResourceDeclaration? Declaration, string? CopyName = null);

    /// <summary>One <c>copy</c> loop: its literal name and the declaration that carries it.</summary>
    public sealed record CopyLoop(string Name, ResourceDeclaration Declaration);

    /// <summary>
    /// A copy loop whose instance ids could not be computed statically because its count depends on a value that is not known
    /// yet (<see cref="Origins"/>, with the deploy-time value <see cref="Symbol"/>); a name that matches no indexed resource may
    /// be one of its instances.
    /// </summary>
    public sealed record PendingCopyLoop(string Name, ResourceDeclaration Declaration, Values.UnknownOrigins Origins, string Symbol);

    private readonly List<PendingCopyLoop> _pending = [];

    /// <summary>The copy loops whose instances are not indexed (see <see cref="PendingCopyLoop"/>).</summary>
    public IReadOnlyList<PendingCopyLoop> PendingCopyLoops => _pending;

    /// <summary>Records a copy loop whose instances could not be indexed.</summary>
    public void AddPendingCopyLoop(PendingCopyLoop loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        _pending.Add(loop);
    }

    /// <summary>Adds an entry; a second entry with the same id is ignored (duplicates are reported elsewhere).</summary>
    public void Add(Entry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_byId.TryAdd(entry.Id, entry))
        {
            _entries.Add(entry);
        }
    }

    /// <summary>Registers a copy loop; a second loop with the same name (case-insensitive) is ignored (duplicates are reported elsewhere).</summary>
    public void AddCopyLoop(CopyLoop loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        if (!TryFindCopyLoop(loop.Name, out _))
        {
            _loops.Add(loop);
        }
    }

    /// <summary>Finds a copy loop by name (case-insensitive).</summary>
    public bool TryFindCopyLoop(string copyName, [NotNullWhen(true)] out CopyLoop? loop)
    {
        ArgumentNullException.ThrowIfNull(copyName);
        foreach (var candidate in _loops)
        {
            if (string.Equals(candidate.Name, copyName, StringComparison.OrdinalIgnoreCase))
            {
                loop = candidate;
                return true;
            }
        }

        loop = null;
        return false;
    }

    public bool Contains(ResourceIdentifier id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _byId.ContainsKey(id);
    }

    public bool TryGet(ResourceIdentifier id, out Entry entry)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _byId.TryGetValue(id, out entry!);
    }

    /// <summary>
    /// Finds resources by ARM-style name (<c>parent/child</c> for children). An exact match wins; otherwise names
    /// compare case-insensitively. Returns every match so the caller can report ambiguity.
    /// </summary>
    public IReadOnlyList<Entry> FindByName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var exact = _entries.Where(e => !e.IsSymbolic && string.Equals(e.Id.Name, name, StringComparison.Ordinal)).ToList();
        if (exact.Count > 0)
        {
            return exact;
        }

        return _entries.Where(e => !e.IsSymbolic && string.Equals(e.Id.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Finds indexed resources whose <c>copy.name</c> is <paramref name="copyName"/> (ARM: depending on a loop
    /// name). Empty before expansion for a loop whose iteration names depend on <c>copyIndex()</c>; see
    /// <see cref="TryFindCopyLoop"/> for the loop itself.
    /// </summary>
    public IReadOnlyList<Entry> FindByCopyName(string copyName)
    {
        ArgumentNullException.ThrowIfNull(copyName);
        return _entries.Where(e => e.CopyName is not null && string.Equals(e.CopyName, copyName, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>The ids of the indexed resources, for change detection between static passes.</summary>
    public IReadOnlySet<ResourceIdentifier> Ids => _byId.Keys.ToHashSet();
}
