namespace EArm.Engine.Planning;

/// <summary>ARM deployment modes.</summary>
public enum DeploymentMode
{
    /// <summary>Create/update declared resources; resources that vanished from the template are detached from state with a warning.</summary>
    Incremental,

    /// <summary>As <see cref="Incremental"/>, plus state-managed resources absent from the template are deleted (WI-06).</summary>
    Complete,
}

/// <summary>What the planner is asked for.</summary>
public enum PlanMode
{
    /// <summary>Read every resource and compute changes; no writes. <c>reference()</c> of a to-be-created resource is a placeholder.</summary>
    WhatIf,

    /// <summary>Expansion and ordering only; the executor reads and plans each resource live just before applying it.</summary>
    Create,
}

/// <summary>The <c>copy.mode</c> of a resource loop.</summary>
public enum CopyMode
{
    Parallel,
    Serial,
}

/// <summary>One iteration of a resource <c>copy</c> loop.</summary>
public sealed record CopyIteration(string Name, long Index, long Count, CopyMode Mode, int BatchSize)
{
    /// <summary>For serial loops: the chunk (<c>batchSize</c> iterations) this iteration belongs to.</summary>
    public long Chunk => BatchSize <= 0 ? Index : Index / BatchSize;
}
