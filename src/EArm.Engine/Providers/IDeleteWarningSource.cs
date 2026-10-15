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
    /// reversible.
    /// </summary>
    string? DescribeDeleteRisk(ResourceKey key, ExistingResource live);
}
