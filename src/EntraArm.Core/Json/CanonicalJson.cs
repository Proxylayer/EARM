using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using EntraArm.Core.Secrets;

namespace EntraArm.Core.Json;

/// <summary>
/// Canonical JSON for hashing and order-insensitive comparison: object keys sorted ordinally, no whitespace,
/// UTF-8 with non-ASCII characters written literally, integral numbers without fraction/exponent, other numbers in
/// shortest round-trip invariant form. Secret leaves are written as <c>***</c>, so a hash never depends on a secret.
/// </summary>
public static class CanonicalJson
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = false,
    };

    public static string Serialize(JsonNode? node)
    {
        return Serialize(node, []);
    }

    /// <summary>
    /// Serialises <paramref name="node"/> omitting every property or element whose path (relative to
    /// <paramref name="node"/>) is listed in <paramref name="excludePaths"/>.
    /// </summary>
    public static string Serialize(JsonNode? node, IEnumerable<JsonPath> excludePaths)
    {
        ArgumentNullException.ThrowIfNull(excludePaths);
        return Encoding.UTF8.GetString(SerializeToUtf8(node, excludePaths));
    }

    public static byte[] SerializeToUtf8(JsonNode? node, IEnumerable<JsonPath> excludePaths)
    {
        ArgumentNullException.ThrowIfNull(excludePaths);
        var excluded = new HashSet<JsonPath>(excludePaths);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            Write(writer, node, JsonPath.Root, excluded);
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Lowercase hexadecimal SHA-256 of the UTF-8 bytes of <paramref name="canonical"/>.</summary>
    public static string Sha256(string canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>Convenience: <c>Sha256(Serialize(node, excludePaths))</c>.</summary>
    public static string Hash(JsonNode? node, IEnumerable<JsonPath>? excludePaths = null)
    {
        return Sha256(Serialize(node, excludePaths ?? []));
    }

    private static void Write(Utf8JsonWriter writer, JsonNode? node, JsonPath path, HashSet<JsonPath> excluded)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;

            case JsonObject obj:
                {
                    writer.WriteStartObject();
                    var keys = new List<string>(obj.Count);
                    foreach (var property in obj)
                    {
                        keys.Add(property.Key);
                    }

                    keys.Sort(StringComparer.Ordinal);
                    foreach (var key in keys)
                    {
                        var childPath = path.Append(key);
                        if (excluded.Contains(childPath))
                        {
                            continue;
                        }

                        writer.WritePropertyName(LoneSurrogates.Scrub(key));
                        Write(writer, obj[key], childPath, excluded);
                    }

                    writer.WriteEndObject();
                    break;
                }

            case JsonArray array:
                {
                    writer.WriteStartArray();
                    for (int i = 0; i < array.Count; i++)
                    {
                        var childPath = path.Append(i);
                        if (excluded.Contains(childPath))
                        {
                            continue;
                        }

                        Write(writer, array[i], childPath, excluded);
                    }

                    writer.WriteEndArray();
                    break;
                }

            case JsonValue value:
                WriteValue(writer, value);
                break;

            default:
                node.WriteTo(writer);
                break;
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, JsonValue value)
    {
        if (SecretJson.IsSecret(value))
        {
            writer.WriteStringValue(SecretValue.Mask);
            return;
        }

        if (value.TryGetValue<JsonElement>(out var element))
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Number:
                    WriteNumber(writer, element);
                    return;
                case JsonValueKind.String:
                    writer.WriteStringValue(LoneSurrogates.GetStringOrReplacement(element));
                    return;
                default:
                    element.WriteTo(writer);
                    return;
            }
        }

        // CLR-backed strings: an unpaired surrogate is written as U+FFFD, the same text a parsed lone surrogate escape gives (and
        // the same canonical form, so equal-looking data hashes equal whichever way it was built).
        if (value.TryGetValue<string>(out var text))
        {
            writer.WriteStringValue(LoneSurrogates.Scrub(text));
            return;
        }

        // Other CLR-backed primitives: System.Text.Json already writes numbers with the invariant culture.
        value.WriteTo(writer);
    }

    private static void WriteNumber(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.TryGetInt64(out long l))
        {
            writer.WriteNumberValue(l);
            return;
        }

        if (element.TryGetDecimal(out decimal m))
        {
            // "G29" drops trailing zeros (1.50 -> 1.5, 2.0 -> 2) and stays a valid JSON number.
            writer.WriteRawValue(m.ToString("G29", CultureInfo.InvariantCulture), skipInputValidation: true);
            return;
        }

        if (element.TryGetDouble(out double d))
        {
            writer.WriteRawValue(d.ToString("R", CultureInfo.InvariantCulture), skipInputValidation: true);
            return;
        }

        element.WriteTo(writer);
    }
}
