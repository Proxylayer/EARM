using System.Threading;
using System.Threading.Tasks;

namespace EArm.Az;

/// <summary>Executes Azure CLI invocations. The production implementation is <see cref="PwshAzRunner"/>.</summary>
public interface IAzRunner
{
    /// <summary>
    /// Runs one az invocation and returns its raw outcome. Throws <see cref="ToolchainException"/> when pwsh is
    /// unusable and <see cref="System.OperationCanceledException"/> on caller cancellation; a non-zero az exit is
    /// reported through <see cref="AzResult"/>, not by throwing.
    /// </summary>
    Task<AzResult> RunAsync(AzRequest request, CancellationToken cancellationToken);
}
