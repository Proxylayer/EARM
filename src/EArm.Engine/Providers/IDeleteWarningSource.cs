using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;

namespace EArm.Engine.Providers;

/// <summary>
/// Optional provider capability: describes why deleting an object is risky (a security group is deleted
/// permanently, with no 30-day soft-delete window). The planner adds the text to the Complete-mode what-if row and
/// the executor logs it as a warning before the delete.
/// </summary>
public interface IDeleteWarningSource
{
    /// <summary>
    /// A warning for deleting the live object <paramref name="live"/> found under <paramref name="key"/>, or <c>null</c> when the delete is
    /// reversible. Only what the key read returned is available.
    /// </summary>
    string? DescribeDeleteRisk(ResourceKey key, ExistingResource live);

    /// <summary>
    /// The warning when a delete is actually planned (<c>deployment delete</c>, the delete half of a recreate): a provider may read what only the warning
    /// needs here (an access package's delivered assignments), instead of on every key read. Defaults to <see cref="DescribeDeleteRisk"/>.
    /// </summary>
    Task<string?> DescribeDeleteRiskAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken)
    {
        return Task.FromResult(DescribeDeleteRisk(key, live));
    }
}
