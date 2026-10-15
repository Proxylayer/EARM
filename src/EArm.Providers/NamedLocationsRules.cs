using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>
/// The pure rules of <see cref="NamedLocationsProvider"/>: the two kinds selected by
/// <c>@odata.type</c>, the per-kind defaults, and the normalisation of <c>ipRanges</c> (the IPv4/IPv6 range type
/// is inferred from the address when omitted; CIDR text is canonicalised so that reordered or differently spelt
/// ranges compare equal; host bits set in a range are refused, as Graph would with a 400) and of
/// <c>countriesAndRegions</c> (ISO 3166-1 alpha-2 codes, upper-cased). Testable without Graph.
/// </summary>
public static class NamedLocationsRules
{
    public const string ODataTypeProperty = "@odata.type";

    public const string IpKind = "#microsoft.graph.ipNamedLocation";

    public const string CountryKind = "#microsoft.graph.countryNamedLocation";

    public const string IPv4RangeType = "#microsoft.graph.iPv4CidrRange";

    public const string IPv6RangeType = "#microsoft.graph.iPv6CidrRange";

    public const string DefaultCountryLookupMethod = "clientIpAddress";

    /// <summary>Properties Graph assigns; never sent in a body.</summary>
    public static readonly string[] ServerAssignedProperties = ["id", "createdDateTime", "modifiedDateTime"];

    /// <summary>The declared kind in canonical casing: <see cref="IpKind"/>, <see cref="CountryKind"/>, or <c>null</c> when absent, unknown at plan time, or not one of the two.</summary>
    public static string? KindOf(JsonObject? properties)
    {
        string? declared = JsonObjects.GetString(properties, ODataTypeProperty);
        if (declared is null)
        {
            return null;
        }

        if (declared.Equals(IpKind, StringComparison.OrdinalIgnoreCase))
        {
            return IpKind;
        }

        return declared.Equals(CountryKind, StringComparison.OrdinalIgnoreCase) ? CountryKind : null;
    }

    /// <summary>
    /// Normalises the evaluated properties in place (kind casing, per-kind defaults, <c>ipRanges</c> range types and
    /// canonical CIDR text, upper-cased country codes) and returns the problems that make the object unsupported:
    /// an unknown kind, a property of the other kind, a non-object range, an invalid CIDR, host bits set, or a
    /// declared range type that contradicts the address family. Deploy-time-unknown values are left alone.
    /// </summary>
    public static IReadOnlyList<string> NormalizeDesired(JsonObject properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var problems = new List<string>();

        if (!JsonObjects.TryGetIgnoreCase(properties, ODataTypeProperty, out var declaredKind) || JsonObjects.IsAbsent(declaredKind) || UnknownJson.IsUnknown(declaredKind))
        {
            // Absent: reported by requiredOnCreate. Unknown: the kind is only known at deployment time.
            return problems;
        }

        string? kind = KindOf(properties);
        if (kind is null)
        {
            problems.Add($"properties.{ODataTypeProperty} ({JsonValues.Render(declaredKind)}) must be '{IpKind}' or '{CountryKind}'");
            return problems;
        }

        properties[JsonObjects.FindKey(properties, ODataTypeProperty) ?? ODataTypeProperty] = kind;
        if (kind == IpKind)
        {
            RejectForeign(properties, "countriesAndRegions", "country", problems);
            RejectForeign(properties, "includeUnknownCountriesAndRegions", "country", problems);
            RejectForeign(properties, "countryLookupMethod", "country", problems);
            JsonObjects.SetDefault(properties, "isTrusted", JsonValue.Create(false));
            if (JsonObjects.TryGetIgnoreCase(properties, "ipRanges", out var ranges) && ranges is JsonArray array)
            {
                NormalizeRanges(array, problems);
            }
        }
        else
        {
            RejectForeign(properties, "ipRanges", "IP", problems);
            RejectForeign(properties, "isTrusted", "IP", problems);
            JsonObjects.SetDefault(properties, "includeUnknownCountriesAndRegions", JsonValue.Create(false));
            JsonObjects.SetDefault(properties, "countryLookupMethod", JsonValue.Create(DefaultCountryLookupMethod));
            if (JsonObjects.TryGetIgnoreCase(properties, "countriesAndRegions", out var countries) && countries is JsonArray codes)
            {
                UpperCaseStrings(codes);
            }
        }

        return problems;
    }

    /// <summary>
    /// The live object (already stripped) in the same canonical form as a normalised desired object: range types in
    /// canonical casing, canonical CIDR text, upper-cased country codes. Values Graph returns that do not parse are
    /// left as they are (they will show as drift, which is right).
    /// </summary>
    public static JsonObject NormalizeLive(JsonObject live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var copy = (JsonObject)JsonValues.Clone(live)!;
        if (KindOf(copy) is { } kind)
        {
            copy[JsonObjects.FindKey(copy, ODataTypeProperty) ?? ODataTypeProperty] = kind;
        }

        if (JsonObjects.TryGetIgnoreCase(copy, "ipRanges", out var ranges) && ranges is JsonArray array)
        {
            NormalizeRanges(array, problems: null);
        }

        if (JsonObjects.TryGetIgnoreCase(copy, "countriesAndRegions", out var countries) && countries is JsonArray codes)
        {
            UpperCaseStrings(codes);
        }

        return copy;
    }

    /// <summary>
    /// Validates and canonicalises one CIDR range. <paramref name="canonical"/> is <c>address/prefix</c> with the
    /// address as .NET prints it (IPv6 compressed and lower-cased; a bare address gets the full-length prefix);
    /// <paramref name="isIPv6"/> tells the family. Returns a problem when the text is not a CIDR range, the prefix is
    /// out of range, a scope id is present, or host bits are set.
    /// </summary>
    public static string? CidrProblem(string text, out string canonical, out bool isIPv6)
    {
        ArgumentNullException.ThrowIfNull(text);
        canonical = text;
        isIPv6 = false;

        string trimmed = text.Trim();
        int slash = trimmed.IndexOf('/', StringComparison.Ordinal);
        string addressText = slash < 0 ? trimmed : trimmed[..slash];
        if (addressText.Length == 0 || addressText.Contains('%', StringComparison.Ordinal) || !IPAddress.TryParse(addressText, out var address))
        {
            return $"'{text}' is not a valid CIDR range (expected an IPv4 or IPv6 address followed by /prefix)";
        }

        switch (address.AddressFamily)
        {
            case AddressFamily.InterNetwork:
                if (CountDots(addressText) != 3)
                {
                    return $"'{text}' is not a valid CIDR range (an IPv4 address needs four octets)";
                }

                break;

            case AddressFamily.InterNetworkV6:
                isIPv6 = true;
                break;

            default:
                return $"'{text}' is not a valid CIDR range";
        }

        int bits = isIPv6 ? 128 : 32;
        int prefix = bits;
        if (slash >= 0 && (!int.TryParse(trimmed[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix > bits))
        {
            return $"'{text}' has an invalid prefix length (expected 0-{bits})";
        }

        var bytes = address.GetAddressBytes();
        bool hostBits = false;
        for (int i = 0; i < bytes.Length; i++)
        {
            int keep = Math.Clamp(prefix - (i * 8), 0, 8);
            byte mask = keep == 0 ? (byte)0 : (byte)(0xFF << (8 - keep));
            if ((bytes[i] & ~mask) != 0)
            {
                hostBits = true;
                bytes[i] &= mask;
            }
        }

        string network = new IPAddress(bytes).ToString();
        if (hostBits)
        {
            return $"'{text}' has host bits set; Graph rejects it, use the network address '{network}/{prefix}'";
        }

        canonical = network + "/" + prefix.ToString(CultureInfo.InvariantCulture);
        return null;
    }

    /// <summary>The range type Graph expects for an address family.</summary>
    public static string RangeTypeFor(bool isIPv6)
    {
        return isIPv6 ? IPv6RangeType : IPv4RangeType;
    }

    // ---- helpers ----

    private static void NormalizeRanges(JsonArray ranges, List<string>? problems)
    {
        for (int i = 0; i < ranges.Count; i++)
        {
            if (ranges[i] is not JsonObject range)
            {
                problems?.Add($"properties.ipRanges[{i}] must be an object with a cidrAddress");
                continue;
            }

            if (UnknownJson.ContainsUnknown(range))
            {
                continue;
            }

            string? cidr = JsonObjects.GetString(range, "cidrAddress");
            if (cidr is null)
            {
                problems?.Add($"properties.ipRanges[{i}] has no cidrAddress");
                continue;
            }

            if (CidrProblem(cidr, out var canonical, out var isIPv6) is { } problem)
            {
                problems?.Add($"properties.ipRanges[{i}]: {problem}");
                continue;
            }

            range[JsonObjects.FindKey(range, "cidrAddress") ?? "cidrAddress"] = canonical;
            string expected = RangeTypeFor(isIPv6);
            string? declared = JsonObjects.GetString(range, ODataTypeProperty);
            if (declared is not null && !declared.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                problems?.Add($"properties.ipRanges[{i}]: '{cidr}' is an {(isIPv6 ? "IPv6" : "IPv4")} range but declares {ODataTypeProperty} '{declared}' (expected '{expected}')");
                continue;
            }

            range[JsonObjects.FindKey(range, ODataTypeProperty) ?? ODataTypeProperty] = expected;
        }
    }

    private static void UpperCaseStrings(JsonArray codes)
    {
        for (int i = 0; i < codes.Count; i++)
        {
            var item = codes[i];
            if (item is JsonValue value && !UnknownJson.IsUnknown(value) && JsonValues.TryGetString(value, out var text))
            {
                string upper = text.Trim().ToUpperInvariant();
                if (!string.Equals(upper, text, StringComparison.Ordinal))
                {
                    codes[i] = upper;
                }
            }
        }
    }

    private static void RejectForeign(JsonObject properties, string property, string otherKind, List<string> problems)
    {
        if (JsonObjects.TryGetIgnoreCase(properties, property, out var value) && !JsonObjects.IsAbsent(value))
        {
            problems.Add($"properties.{property} applies to {otherKind} named locations only");
        }
    }

    private static int CountDots(string text)
    {
        int count = 0;
        foreach (char c in text)
        {
            if (c == '.')
            {
                count++;
            }
        }

        return count;
    }
}
