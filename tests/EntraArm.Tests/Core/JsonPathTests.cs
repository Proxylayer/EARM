using System;
using System.Text.Json.Nodes;
using EntraArm.Core.Json;
using Xunit;

namespace EntraArm.Tests;

public sealed class JsonPathTests
{
    [Theory]
    [InlineData("resources[0].properties.displayName")]
    [InlineData("properties['@odata.type']")]
    [InlineData("a['x.y'][2]")]
    [InlineData("[0].a")]
    [InlineData("parameters.env")]
    [InlineData("a['it\\'s']")]
    [InlineData("a['back\\\\slash']")]
    [InlineData("resources[0].resources[1].name")]
    [InlineData("['0']")]
    [InlineData("['']")]
    public void Parse_And_ToString_RoundTrip(string text)
    {
        var path = JsonPath.Parse(text);

        Assert.Equal(text, path.ToString());
        Assert.Equal(path, JsonPath.Parse(path.ToString()));
    }

    [Fact]
    public void Of_And_Append_RenderArmStyle()
    {
        var path = JsonPath.Of("resources").Append(0).Append("properties").Append("@odata.type");

        Assert.Equal("resources[0].properties['@odata.type']", path.ToString());
        Assert.Equal(4, path.Depth);
        Assert.False(path.IsRoot);
        Assert.Equal(JsonPathSegment.Property("@odata.type"), path.Last);
        Assert.Equal("[0]", JsonPathSegment.ArrayIndex(0).ToString());
        Assert.Equal("name", JsonPathSegment.Property("name").ToString());
        Assert.True(JsonPathSegment.ArrayIndex(3).IsIndex);
        Assert.Equal(-1, JsonPathSegment.Property("x").Index);
    }

    [Fact]
    public void Append_Path_ConcatenatesSegments()
    {
        var left = JsonPath.Of("a", "b");
        var right = JsonPath.Of("c").Append(1);

        Assert.Equal("a.b.c[1]", left.Append(right).ToString());
        Assert.Same(left, left.Append(JsonPath.Root));
        Assert.Equal(right, JsonPath.Root.Append(right));
    }

    [Theory]
    [InlineData("")]
    [InlineData("$")]
    [InlineData("   ")]
    public void Parse_RootForms(string text)
    {
        var path = JsonPath.Parse(text);

        Assert.True(path.IsRoot);
        Assert.Equal(JsonPath.Root, path);
        Assert.Equal(string.Empty, path.ToString());
        Assert.Equal(0, path.Depth);
        Assert.Null(path.Parent);
    }

    [Theory]
    [InlineData("$.resources[1]", "resources[1]")]
    [InlineData("$resources", "resources")]
    [InlineData(".a.b", "a.b")]
    [InlineData("a..b", "a.b")]
    [InlineData(" a.b ", "a.b")]
    public void Parse_IsLenientAboutSeparators(string text, string rendered)
    {
        Assert.Equal(rendered, JsonPath.Parse(text).ToString());
    }

    [Theory]
    [InlineData("a[")]
    [InlineData("a[x]")]
    [InlineData("a['x'")]
    [InlineData("a[]")]
    [InlineData("a['x'b]")]
    [InlineData("a[1")]
    [InlineData("a[-1]")]
    [InlineData("a[\"0\"]")]
    public void TryParse_RejectsMalformedText(string text)
    {
        Assert.False(JsonPath.TryParse(text, out var path));
        Assert.Equal(JsonPath.Root, path);
        Assert.Throws<FormatException>(() => JsonPath.Parse(text));
    }

    [Fact]
    public void TryParse_RejectsNull()
    {
        Assert.False(JsonPath.TryParse(null!, out _));
        Assert.Throws<ArgumentNullException>(() => JsonPath.Parse(null!));
    }

    [Fact]
    public void ToJsonPointer_EscapesSpecialCharacters()
    {
        var path = JsonPath.Of("a/b", "c~d").Append(3).Append("plain");

        Assert.Equal("/a~1b/c~0d/3/plain", path.ToJsonPointer());
        Assert.Equal(string.Empty, JsonPath.Root.ToJsonPointer());
        Assert.Equal("/resources/0/properties/@odata.type", JsonPath.Parse("resources[0].properties['@odata.type']").ToJsonPointer());
    }

    [Fact]
    public void FromJsonPointer_WithoutAnInstance_TreatsNumericTokensAsIndexes()
    {
        var path = JsonPath.FromJsonPointer("/resources/0/properties/displayName");

        Assert.Equal("resources[0].properties.displayName", path.ToString());
        Assert.True(path.Segments[1].IsIndex);
        Assert.Equal(0, path.Segments[1].Index);
    }

    [Fact]
    public void FromJsonPointer_WithAnInstance_DistinguishesObjectKeysFromIndexes()
    {
        var objectInstance = JsonNode.Parse("""{ "resources": { "0": { "name": 1 } } }""");
        var arrayInstance = JsonNode.Parse("""{ "resources": [ { "name": 1 } ] }""");

        Assert.Equal("resources['0'].name", JsonPath.FromJsonPointer("/resources/0/name", objectInstance).ToString());
        Assert.Equal("resources[0].name", JsonPath.FromJsonPointer("/resources/0/name", arrayInstance).ToString());
        Assert.Equal("resources[5].name", JsonPath.FromJsonPointer("/resources/5/name", arrayInstance).ToString());
    }

    [Fact]
    public void FromJsonPointer_UnescapesTokensAndFragments()
    {
        Assert.Equal("['a/b']['c~d']", JsonPath.FromJsonPointer("/a~1b/c~0d").ToString());
        Assert.Equal("resources[0].properties['@odata.type']", JsonPath.FromJsonPointer("#/resources/0/properties/%40odata.type").ToString());
        Assert.Equal(JsonPath.Root, JsonPath.FromJsonPointer(string.Empty));
        Assert.Equal(JsonPath.Root, JsonPath.FromJsonPointer("#"));
    }

    [Fact]
    public void FromJsonPointer_RejectsTextWithoutALeadingSlash()
    {
        Assert.Throws<FormatException>(() => JsonPath.FromJsonPointer("resources/0"));
        Assert.Throws<ArgumentNullException>(() => JsonPath.FromJsonPointer(null!));
    }

    [Theory]
    [InlineData("resources[0].properties['@odata.type']")]
    [InlineData("a['x/y']['z~w'][3]")]
    [InlineData("")]
    public void Pointer_RoundTripsWhenTheInstanceIsKnown(string text)
    {
        var instance = JsonNode.Parse("""{ "resources": [ { "properties": { "@odata.type": 1 } } ], "a": { "x/y": { "z~w": [0, 1, 2, 3] } } }""");
        var path = JsonPath.Parse(text);

        Assert.Equal(path, JsonPath.FromJsonPointer(path.ToJsonPointer(), instance));
    }

    [Fact]
    public void Equality_IsStructural()
    {
        var a = JsonPath.Parse("resources[0].name");
        var b = JsonPath.Of("resources").Append(0).Append("name");
        var c = JsonPath.Parse("resources[1].name");
        var d = JsonPath.Parse("resources['0'].name");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a.Equals((object)b));

        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
        Assert.True(a != c);
        Assert.False(a.Equals(null));
        Assert.False(a == null);
        Assert.True((JsonPath?)null == null);
    }

    [Fact]
    public void Parent_Last_And_StartsWith()
    {
        var path = JsonPath.Parse("resources[0].properties.displayName");

        Assert.Equal(JsonPath.Parse("resources[0].properties"), path.Parent);
        Assert.Equal(JsonPath.Parse("resources[0]"), path.Parent!.Parent);
        Assert.Equal(JsonPath.Root, path.Parent.Parent!.Parent!.Parent);
        Assert.Equal("displayName", path.Last.Name);
        Assert.Throws<InvalidOperationException>(() => JsonPath.Root.Last);

        Assert.True(path.StartsWith(JsonPath.Root));
        Assert.True(path.StartsWith(JsonPath.Parse("resources[0]")));
        Assert.True(path.StartsWith(path));
        Assert.False(path.StartsWith(JsonPath.Parse("resources[1]")));
        Assert.False(JsonPath.Parse("resources").StartsWith(path));
        Assert.Throws<ArgumentNullException>(() => path.StartsWith(null!));
    }

    [Fact]
    public void Segments_RejectInvalidArguments()
    {
        Assert.Throws<ArgumentNullException>(() => JsonPathSegment.Property(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => JsonPathSegment.ArrayIndex(-1));
    }
}
