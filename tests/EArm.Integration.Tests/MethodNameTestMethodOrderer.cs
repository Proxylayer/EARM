using System;
using System.Collections.Generic;
using System.Linq;
using Xunit.Sdk;
using Xunit.v3;

namespace EArm.Integration.Tests;

/// <summary>
/// Runs the test methods of a class in ordinal method-name order (xUnit's default method order is not the declaration order). Used by
/// <see cref="DelegatedAuthSpikeProbes"/>, whose probes build on each other's sign-in and consent (S1, S2, S3, ...).
/// </summary>
public sealed class MethodNameTestMethodOrderer : ITestMethodOrderer
{
    public IReadOnlyCollection<TTestMethod?> OrderTestMethods<TTestMethod>(IReadOnlyCollection<TTestMethod?> testMethods)
        where TTestMethod : notnull, ITestMethod
    {
        return testMethods.OrderBy(testMethod => testMethod?.MethodName, StringComparer.Ordinal).ToArray();
    }
}
