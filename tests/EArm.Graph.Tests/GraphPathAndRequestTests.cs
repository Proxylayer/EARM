using System;
using System.Collections.Generic;
using EArm.Graph;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary><see cref="GraphPath"/> helpers and the <see cref="GraphRequest"/> builder (pure, no I/O).</summary>
public sealed class GraphPathAndRequestTests
{
    [Fact]
    public void AlternateKey_DoublesApostrophesAndEncodesUnsafeCharacters()
    {
        Assert.Equal("applications(uniqueName='it''s')", GraphPath.AlternateKey("applications", "uniqueName", "it's"));
        Assert.Equal("applications(uniqueName='a%20b')", GraphPath.AlternateKey("applications/", "uniqueName", "a b"));
        Assert.Equal("users(userPrincipalName='alice@contoso.example')", GraphPath.AlternateKey("users", "userPrincipalName", "alice@contoso.example"));
        Assert.Equal("groups(uniqueName='')", GraphPath.AlternateKey("groups", "uniqueName", string.Empty));
    }

    [Theory]
    [InlineData("it's", "it''s")]
    [InlineData("a b", "a%20b")]
    [InlineData("Ünï/日", "%C3%9Cn%C3%AF%2F%E6%97%A5")]
    [InlineData("a=b,c:(d)$!*~._-", "a=b,c:(d)$!*~._-")]
    [InlineData("50% & #1?", "50%25%20%26%20%231%3F")]
    [InlineData("\"q\"", "%22q%22")]
    public void KeyValue_PercentEncodesEverythingButSafeCharacters(string value, string expected)
    {
        Assert.Equal(expected, GraphPath.KeyValue(value));
    }

    [Theory]
    [InlineData("11111111-2222-3333-4444-555555555555", "11111111-2222-3333-4444-555555555555")]
    [InlineData("user_contoso.example#EXT#@fabrikam.example", "user_contoso.example%23EXT%23@fabrikam.example")]
    [InlineData("it's", "it's")]
    [InlineData("a b/c", "a%20b%2Fc")]
    public void Segment_KeepsSafeCharactersAndEncodesTheRest(string value, string expected)
    {
        Assert.Equal(expected, GraphPath.Segment(value));
    }

    [Fact]
    public void Join_NormalisesSlashes()
    {
        Assert.Equal("/users/abc/memberOf", GraphPath.Join("users", "/abc/", "memberOf"));
        Assert.Equal("/users", GraphPath.Join("", "users", null!));
        Assert.Equal("/", GraphPath.Join());
        Assert.Equal("/groups/g1/members/$ref", GraphPath.Join("/groups", "g1", "members", "$ref"));
    }

    [Fact]
    public void GraphPath_RejectsNullAndBlankArguments()
    {
        Assert.Throws<ArgumentException>(static () => GraphPath.AlternateKey(" ", "k", "v"));
        Assert.Throws<ArgumentException>(static () => GraphPath.AlternateKey("c", "", "v"));
        Assert.Throws<ArgumentNullException>(static () => GraphPath.AlternateKey("c", "k", null!));
        Assert.Throws<ArgumentNullException>(static () => GraphPath.Segment(null!));
    }

    [Fact]
    public void GraphRequest_RejectsQueryStringsInThePath()
    {
        var exception = Assert.Throws<ArgumentException>(static () => new GraphRequest("/users?$select=id"));
        Assert.Equal("path", exception.ParamName);
        Assert.Contains("WithParameter", exception.Message);
        Assert.Throws<ArgumentException>(static () => new GraphRequest(" "));
    }

    [Fact]
    public void GraphRequest_KeepsTheQueryInParametersNotInThePath()
    {
        var request = new GraphRequest("users").WithSelect("id", "displayName").WithFilter("displayName eq 'a b'").WithTop(5).WithCount();

        Assert.Equal("/users", request.Path);
        Assert.Equal(GraphApi.Beta, request.WithApi(GraphApi.Beta).Api);
        Assert.NotNull(request.UrlParameters);
        Assert.Equal(["$select", "$filter", "$top", "$count"], request.UrlParameters.Keys);
        Assert.Equal("id,displayName", request.UrlParameters["$select"]);
        Assert.Equal("displayName eq 'a b'", request.UrlParameters["$filter"]);
        Assert.Equal("5", request.UrlParameters["$top"]);
        Assert.Equal("true", request.UrlParameters["$count"]);
        Assert.Equal("https://graph.microsoft.com/v1.0/users?$select=id,displayName&$filter=displayName eq 'a b'&$top=5&$count=true", request.ToString());
        Assert.DoesNotContain('?', request.Path);

        var replaced = request.WithParameter("$select", "id");
        Assert.Equal("id", replaced.UrlParameters?["$select"]);
        Assert.Equal("id,displayName", request.UrlParameters["$select"]);
    }

    [Fact]
    public void GraphRequest_MergesPreferAndAddsConsistencyLevelWhenAdvanced()
    {
        var request = new GraphRequest("/users").WithPrefer("create-if-missing").WithPrefer("return=representation").WithHeader("x-custom", "1");

        Assert.Equal("create-if-missing, return=representation", request.Headers?["Prefer"]);
        Assert.Equal("1", request.Headers?["X-Custom"]);
        Assert.False(request.Advanced);
        Assert.DoesNotContain("ConsistencyLevel", request.EffectiveHeaders().Keys);

        var advanced = request.WithConsistencyLevelEventual();
        Assert.True(advanced.Advanced);
        Assert.Equal("eventual", advanced.EffectiveHeaders()["ConsistencyLevel"]);
        Assert.Equal("create-if-missing, return=representation", advanced.EffectiveHeaders()["Prefer"]);

        var explicitLevel = advanced.WithHeader("ConsistencyLevel", "strong");
        Assert.Equal("strong", explicitLevel.EffectiveHeaders()["ConsistencyLevel"]);

        Assert.Equal(RetryHint.Default, request.Hint);
        Assert.Equal(RetryHint.ExpectReplicationLag, request.WithHint(RetryHint.ExpectReplicationLag).Hint);
    }

    [Fact]
    public void GraphApi_BaseUrls()
    {
        Assert.Equal("https://graph.microsoft.com/v1.0", GraphApi.V1.BaseUrl());
        Assert.Equal("https://graph.microsoft.com/beta", GraphApi.Beta.BaseUrl());
        Assert.Equal("https://graph.microsoft.us/beta", GraphApi.Beta.BaseUrl(GraphCloud.UsGov));
        Assert.Equal("v1.0", GraphApi.V1.Segment());
        Assert.Equal("beta", GraphApi.Beta.Segment());
    }
}
