using System;
using EArm.Az;
using Microsoft.Extensions.Logging.Abstractions;

namespace EArm.Az.Tests.Infrastructure;

/// <summary>
/// One <see cref="PwshLocator"/> for the whole test run: it probes pwsh's version once per instance, so sharing it
/// saves one pwsh launch per runner.
/// </summary>
internal static class SharedPwsh
{
    private static readonly Lazy<PwshLocator> LocatorInstance = new(static () => new PwshLocator(new AzOptions(), NullLogger<PwshLocator>.Instance));

    public static PwshLocator Locator => LocatorInstance.Value;
}
