using EntraArm.Engine.State;

namespace EntraArm.Engine.Providers;

/// <summary>
/// Optional provider capability: describes why deleting an object is risky (a security group is deleted
/// permanently, with no 30-day soft-delete window). The planner adds the text to the Complete-mode what-if row and
/// the executor logs it as a warning before the delete.
/// </summary>
public interface IDeleteWarningSource
{
    /// <summary>
    /// A warning for deleting the object recorded by <paramref name="entry"/>, or <c>null</c> when the delete is
    /// reversible. <paramref name="existing"/> is the live object when the caller has read it (what-if), else
    /// <c>null</c>; implementations fall back to the entry's <see cref="StateEntry.Extra"/>.
    /// </summary>
    string? DescribeDeleteRisk(StateEntry entry, ExistingResource? existing);
}
