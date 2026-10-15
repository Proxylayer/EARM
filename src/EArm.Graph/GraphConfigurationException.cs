using System;

namespace EArm.Graph;

/// <summary>
/// An invalid EARM_* variable or command-line value that selects how the transport behaves: an unknown cloud, authentication mode, a tenant
/// or client id that is not one. The message is written for the user (it names the setting and lists the valid values) and never carries
/// the ".NET argument-exception text" ("(Parameter 'value')").
/// <para>
/// It derives from <see cref="ArgumentException"/> (without a parameter name) so every existing catch site that maps a bad option to exit code 1
/// keeps working; catch this type first to tell a configuration problem apart from a programming error.
/// </para>
/// </summary>
public sealed class GraphConfigurationException : ArgumentException
{
    public GraphConfigurationException(string message)
        : base(message)
    {
    }

    public GraphConfigurationException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
