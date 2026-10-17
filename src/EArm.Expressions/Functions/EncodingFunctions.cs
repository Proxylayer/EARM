using System;
using System.Text;
using System.Text.Json.Nodes;

namespace EArm.Expressions.Functions;

/// <summary>
/// String encodings beyond <c>base64</c>/<c>base64ToString</c> (registered with the string group): <c>base64ToJson</c>, <c>dataUri</c> and
/// <c>dataUriToString</c>. Text is UTF-8 throughout, as in ARM.
/// </summary>
internal static class EncodingFunctions
{
    /// <summary>The prefix ARM's <c>dataUri()</c> writes.</summary>
    public const string DataUriPrefix = "data:text/plain;charset=utf8;base64,";

    public static void Register(FunctionRegistry registry, string group)
    {
        registry.Register(new FunctionDescriptor { Name = "base64ToJson", Group = group, MinArgs = 1, MaxArgs = 1, Implementation = Base64ToJson });
        registry.Register(new FunctionDescriptor { Name = "dataUri", Group = group, MinArgs = 1, MaxArgs = 1, Implementation = DataUri });
        registry.Register(new FunctionDescriptor { Name = "dataUriToString", Group = group, MinArgs = 1, MaxArgs = 1, Implementation = DataUriToString });
    }

    /// <summary><c>base64ToJson(base64Value)</c>: the base64 text decoded as UTF-8 and parsed like <c>json()</c>.</summary>
    private static JsonNode? Base64ToJson(FunctionCall call)
    {
        return CollectionFunctions.ParseJsonText(call, DecodeBase64(call, call.String(0)));
    }

    /// <summary><c>dataUri(stringToConvert)</c>: <c>data:text/plain;charset=utf8;base64,</c> followed by the UTF-8 bytes in base64.</summary>
    private static JsonNode? DataUri(FunctionCall call)
    {
        string text = call.String(0);
        call.Context.Budget.ChargeText(text.Length, call.Offset);
        long bytes = Encoding.UTF8.GetByteCount(text);
        call.Context.Budget.CheckStringLength(DataUriPrefix.Length + ((bytes + 2) / 3 * 4), call.Offset);
        return JsonValue.Create(DataUriPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>
    /// <c>dataUriToString(dataUri)</c>: the data of an RFC 2397 data URI (<c>data:[mediatype][;base64],data</c>) as UTF-8 text. The data is
    /// base64-decoded when the media type part ends with <c>;base64</c>, otherwise percent-decoded.
    /// </summary>
    private static JsonNode? DataUriToString(FunctionCall call)
    {
        string uri = call.String(0);
        call.Context.Budget.ChargeText(uri.Length, call.Offset);
        int comma = uri.IndexOf(',', StringComparison.Ordinal);
        if (!uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || comma < 0)
        {
            throw call.Failure(call.ArgumentIsTainted(0) ? "the secure value is not a data URI." : $"'{uri}' is not a data URI (data:[mediatype][;base64],data).");
        }

        string mediaType = uri[5..comma];
        string data = uri[(comma + 1)..];
        if (mediaType.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return JsonValue.Create(DecodeBase64(call, data));
        }

        try
        {
            return JsonValue.Create(Uri.UnescapeDataString(data));
        }
        catch (UriFormatException ex)
        {
            throw call.Failure("the data of the URI is not valid percent-encoded text.", ex);
        }
    }

    private static string DecodeBase64(FunctionCall call, string base64)
    {
        call.Context.Budget.ChargeText(base64.Length, call.Offset);
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException ex)
        {
            throw call.Failure("the argument is not valid base64.", ex);
        }
    }
}
