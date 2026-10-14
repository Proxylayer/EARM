using System.Text;
using EntraArm.Core.Diagnostics;
using EntraArm.Core.Json;
using Xunit;

namespace EntraArm.Tests;

public sealed class SourceMapTests
{
    private const string Layout = "{\n  \"a\": { \"b\": 1 },\n  \"list\": [10, 20],\n  \"s\": \"x\"\n}\n";

    private static SourceMap Build(string text)
    {
        return SourceMap.Build(Encoding.UTF8.GetBytes(text));
    }

    [Fact]
    public void Build_RecordsRootPropertiesAndArrayElements()
    {
        var map = Build(Layout);

        Assert.Equal(new SourceLocation(1, 1), map.Find(JsonPath.Root));
        Assert.Equal(new SourceLocation(2, 3), map.Find(JsonPath.Parse("a")));
        Assert.Equal(new SourceLocation(2, 10), map.Find(JsonPath.Parse("a.b")));
        Assert.Equal(new SourceLocation(3, 3), map.Find(JsonPath.Parse("list")));
        Assert.Equal(new SourceLocation(3, 12), map.Find(JsonPath.Parse("list[0]")));
        Assert.Equal(new SourceLocation(3, 16), map.Find(JsonPath.Parse("list[1]")));
        Assert.Equal(new SourceLocation(4, 3), map.Find(JsonPath.Parse("s")));
        Assert.Equal(7, map.Count);
    }

    [Fact]
    public void Find_ReturnsNullForUnknownPaths_FindNearestFallsBackToAnAncestor()
    {
        var map = Build(Layout);

        Assert.Null(map.Find(JsonPath.Parse("a.missing")));
        Assert.Null(map.Find(JsonPath.Parse("list[2]")));
        Assert.Equal(new SourceLocation(2, 3), map.FindNearest(JsonPath.Parse("a.missing.deeper")));
        Assert.Equal(new SourceLocation(3, 3), map.FindNearest(JsonPath.Parse("list[2]")));
        Assert.Equal(new SourceLocation(1, 1), map.FindNearest(JsonPath.Parse("nope")));
    }

    [Fact]
    public void Build_IgnoresByteOrderMarkAndComments()
    {
        string text = "// comment\n{\n  /* block */ \"a\": 1,\n  \"b\": 2, // trailing\n}\n";
        byte[] bytes = [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(text)];

        var map = SourceMap.Build(bytes);

        Assert.Equal(new SourceLocation(2, 1), map.Find(JsonPath.Root));
        Assert.Equal(new SourceLocation(3, 15), map.Find(JsonPath.Parse("a")));
        Assert.Equal(new SourceLocation(4, 3), map.Find(JsonPath.Parse("b")));
    }

    [Fact]
    public void Columns_CountCharactersNotBytes()
    {
        var map = Build("{ \"nämé日本\": 1, \"x\": 2 }");

        Assert.Equal(new SourceLocation(1, 3), map.Find(JsonPath.Parse("nämé日本")));
        Assert.Equal(new SourceLocation(1, 16), map.Find(JsonPath.Parse("x")));
    }

    [Fact]
    public void Build_HandlesWindowsLineEndings()
    {
        var map = Build("{\r\n  \"a\": 1,\r\n  \"b\": [\r\n    true\r\n  ]\r\n}");

        Assert.Equal(new SourceLocation(2, 3), map.Find(JsonPath.Parse("a")));
        Assert.Equal(new SourceLocation(3, 3), map.Find(JsonPath.Parse("b")));
        Assert.Equal(new SourceLocation(4, 5), map.Find(JsonPath.Parse("b[0]")));
    }

    [Fact]
    public void Build_OnInvalidJson_ReturnsTheEmptyMap()
    {
        var map = Build("{ \"a\": ");

        Assert.Equal(0, map.Count);
        Assert.Null(map.Find(JsonPath.Root));
        Assert.Null(map.FindNearest(JsonPath.Parse("a")));
        Assert.Equal(0, SourceMap.Empty.Count);
    }

    [Fact]
    public void Build_RecordsNestedArraysOfObjects()
    {
        var map = Build("[\n  { \"n\": 1 },\n  [ 2, { \"m\": 3 } ]\n]");

        Assert.Equal(new SourceLocation(1, 1), map.Find(JsonPath.Root));
        Assert.Equal(new SourceLocation(2, 3), map.Find(JsonPath.Parse("[0]")));
        Assert.Equal(new SourceLocation(2, 5), map.Find(JsonPath.Parse("[0].n")));
        Assert.Equal(new SourceLocation(3, 3), map.Find(JsonPath.Parse("[1]")));
        Assert.Equal(new SourceLocation(3, 5), map.Find(JsonPath.Parse("[1][0]")));
        Assert.Equal(new SourceLocation(3, 8), map.Find(JsonPath.Parse("[1][1]")));
        Assert.Equal(new SourceLocation(3, 10), map.Find(JsonPath.Parse("[1][1].m")));
    }

    [Fact]
    public void SourceLocation_RendersLineColonColumn()
    {
        Assert.Equal("12:7", new SourceLocation(12, 7).ToString());
    }
}
