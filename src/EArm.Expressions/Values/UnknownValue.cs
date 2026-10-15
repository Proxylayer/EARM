using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using EArm.Core;

namespace EArm.Expressions.Values;

/// <summary>Why a value is unknown before deployment; flags combine as the value flows through functions.</summary>
[Flags]
public enum UnknownOrigins
{
    None = 0,

    /// <summary>Depends on <c>reference()</c> of a resource that does not exist yet (or was not looked up offline).</summary>
    Reference = 1,

    /// <summary>Depends on <c>copyIndex()</c> evaluated outside a concrete copy iteration.</summary>
    Copy = 2,

    /// <summary>Depends on <c>tenant()</c>/<c>environment()</c> data that is only available online.</summary>
    Deployment = 4,

    /// <summary>Depends on a parameter whose value is not available offline (missing, or a Key Vault reference).</summary>
    Parameter = 8,

    /// <summary>Depends on a function registered as <c>FunctionKind.NotImplementedYet</c> (none in the default registry since WI-16).</summary>
    NotImplemented = 16,
}

/// <summary>
/// The payload of a deploy-time-unknown leaf: the result of <c>reference()</c> for a resource that is not created
/// yet, anything computed from it, <c>copyIndex()</c> outside a concrete iteration, and so on. It renders as
/// <c>(deploy-time)</c> and propagates like taint: a function receiving an unknown input yields an unknown result.
/// <para>
/// <see cref="Symbol"/> is a canonical rendering of the computation that produced the value
/// (<c>ref(/providers/Microsoft.Entra/applications/x).appId</c>). Two unknowns with equal symbols denote the same
/// deploy-time value, which lets the static evaluator match a resource whose name is
/// <c>[reference('x').appId]</c> against <c>resourceId('...', reference('x').appId)</c> elsewhere in the template.
/// </para>
/// <para>
/// <see cref="ResourceId"/> is set when the unknown came out of <c>resourceId()</c> with a known type: the identity
/// whose name segments are symbols. <see cref="Source"/> is the referenced resource when the unknown came out of
/// <c>reference()</c> (property access extends <see cref="Symbol"/>, keeps <see cref="Source"/>).
/// </para>
/// </summary>
[JsonConverter(typeof(UnknownJsonConverter))]
public sealed record UnknownValue(string Symbol, UnknownOrigins Origins, bool Tainted = false, ResourceIdentifier? Source = null, ResourceIdentifier? ResourceId = null)
{
    /// <summary>The text written wherever an unknown is rendered.</summary>
    public const string Display = "(deploy-time)";

    public override string ToString()
    {
        return Display;
    }
}

/// <summary>Writes <see cref="UnknownValue.Display"/>; reads a string back as an opaque unknown.</summary>
public sealed class UnknownJsonConverter : JsonConverter<UnknownValue>
{
    public override UnknownValue? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => new UnknownValue(reader.GetString() ?? UnknownValue.Display, UnknownOrigins.Reference),
            _ => throw new JsonException("An unknown value must be a JSON string."),
        };
    }

    public override void Write(Utf8JsonWriter writer, UnknownValue value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(UnknownValue.Display);
    }
}

/// <summary>Source-generated metadata so an <see cref="UnknownValue"/> can be embedded in a <see cref="System.Text.Json.Nodes.JsonNode"/> tree.</summary>
[JsonSerializable(typeof(UnknownValue))]
internal sealed partial class UnknownJsonContext : JsonSerializerContext
{
}
