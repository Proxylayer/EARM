using System;
using System.Collections.Generic;
using System.Text;
using EArm.Az;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>
/// Collects what a Graph behaviour probe observed and which assumptions did not hold. Observations go to the test
/// output as diagnostic messages; every failed expectation names the file to change, and <see cref="Complete"/> fails the
/// test with all of them at once.
/// </summary>
internal sealed class ProbeReport
{
    private readonly string _name;
    private readonly List<string> _failures = [];

    public ProbeReport(string name)
    {
        _name = name;
    }

    /// <summary>Records an observation (a behaviour the code does not depend on, or the raw answer to a binary question).</summary>
    public void Observe(string message)
    {
        TestContext.Current.SendDiagnosticMessage($"[probe {_name}] {message}");
    }

    /// <summary>Asserts an assumption of the code; <paramref name="fixHint"/> says what to change when it does not hold.</summary>
    public void Expect(bool condition, string fixHint)
    {
        if (condition)
        {
            return;
        }

        _failures.Add(fixHint);
        Observe("ASSUMPTION FAILED: " + fixHint);
    }

    /// <summary>Fails the test when any expectation failed.</summary>
    public void Complete()
    {
        if (_failures.Count == 0)
        {
            return;
        }

        var message = new StringBuilder();
        message.Append("Probe ").Append(_name).Append(": ").Append(_failures.Count).AppendLine(" assumption(s) of the code do not hold against this tenant. Fix:");
        foreach (string failure in _failures)
        {
            message.Append("  - ").AppendLine(failure);
        }

        Assert.Fail(message.ToString());
    }

    /// <summary><c>400 Request_BadRequest: …</c> for an observation or a fix hint.</summary>
    public static string Describe(GraphException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return $"{(int)ex.Status} {ex.Code}: {ex.GraphMessage}";
    }
}
