using System;
using System.Net;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>
/// Mutation survivor E03: the replication classification of <see cref="GraphException"/> must match only the documented statuses and messages
/// (a 404 with an unrelated code, or a replication message on a non-400 status, is NOT replication lag); plus the other classification tables.
/// </summary>
public sealed class Review1B_GraphExceptionClassificationTests
{
    [Theory]
    [InlineData(404, "", true)]
    [InlineData(404, "Request_ResourceNotFound", true)]
    [InlineData(404, "ResourceNotFound", true)]
    [InlineData(404, "NotFound", true)]
    [InlineData(404, "itemNotFound", true)]
    [InlineData(404, "Authorization_RequestDenied", false)]
    [InlineData(404, "Forbidden", false)]
    [InlineData(404, "InvalidAuthenticationToken", false)]
    [InlineData(404, "Request_BadRequest", false)]
    [InlineData(404, "generalException", false)]
    public void AFourOhFourIsReplicationLagOnlyWithAnEmptyOrNotFoundCode(int status, string code, bool expected)
    {
        Assert.Equal(expected, GraphException.IsReplicationError((HttpStatusCode)status, code, "irrelevant"));
    }

    [Theory]
    [InlineData("Resource does not exist or one of its queried reference-property objects are not present.", true)]
    [InlineData("The source resource object 'ServicePrincipal_1' is wrong", true)]
    [InlineData("Principal 'x' don't exist", true)]
    [InlineData("Principal 'x' doesn't exist", true)]
    [InlineData("PrincipalNotFound", true)]
    [InlineData("The reference does not reference a valid application object.", true)]
    [InlineData("Service principal 'x' not found in the directory 'y'", true)]
    [InlineData("DON'T EXIST", true)]
    [InlineData("1040: NamedLocation with id 071774e8-8656-4733-9914-e564af5f3d93 does not exist in the directory.", true)]
    [InlineData("1001: AuthenticationStrengthPolicy with id 3f8b0c4e-0000-0000-0000-000000000000 does not exist.", true)]
    [InlineData("Group with id x does not exist in the directory.", true)]
    [InlineData("1003: The policy is invalid.", false)]
    [InlineData("The policy says the feature does not exist.", false)]
    [InlineData("Property displayName is invalid.", false)]
    [InlineData("A required property is missing.", false)]
    [InlineData("Another object with the same value already exists.", false)]
    [InlineData("", false)]
    public void ABadRequestIsReplicationLagOnlyForTheDocumentedMessages(string message, bool expected)
    {
        Assert.Equal(expected, GraphException.IsReplicationError(HttpStatusCode.BadRequest, "Request_BadRequest", message));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.OK)]
    public void NoOtherStatusIsEverReplicationLagWhateverTheMessageSays(HttpStatusCode status)
    {
        Assert.False(GraphException.IsReplicationError(status, string.Empty, "Resource does not exist or one of its queried reference-property objects are not present."));
        Assert.False(GraphException.IsReplicationError(status, "Request_ResourceNotFound", "PrincipalNotFound doesn't exist"));
    }

    [Fact]
    public void TheMessageOfAReplicationErrorIsNeverNull()
    {
        Assert.False(GraphException.IsReplicationError(HttpStatusCode.BadRequest, "c", null!));
        Assert.False(GraphException.IsAlreadyExistsError(HttpStatusCode.BadRequest, "c", null!));
    }

    [Fact]
    public void TheReplicationFlagNeedsTheHintAsWellAsTheStatus()
    {
        var message = "Resource does not exist or one of its queried reference-property objects are not present.";

        Assert.True(new GraphException(HttpStatusCode.NotFound, "Request_ResourceNotFound", message, RetryHint.ExpectReplicationLag).IsReplication);
        Assert.False(new GraphException(HttpStatusCode.NotFound, "Request_ResourceNotFound", message, RetryHint.Default).IsReplication);
        Assert.False(new GraphException(HttpStatusCode.NotFound, "Request_ResourceNotFound", message, RetryHint.None).IsReplication);
        Assert.False(new GraphException(HttpStatusCode.NotFound, "Authorization_RequestDenied", message, RetryHint.ExpectReplicationLag).IsReplication);
        Assert.False(new GraphException(HttpStatusCode.Forbidden, "Request_ResourceNotFound", message, RetryHint.ExpectReplicationLag).IsReplication);
    }

    [Theory]
    [InlineData(400, "Request_MultipleObjectsWithSameKeyValue", "anything", true)]
    [InlineData(409, "Request_MultipleObjectsWithSameKeyValue", "anything", true)]
    [InlineData(400, "Request_BadRequest", "Another object with the same value for property uniqueName already exists.", true)]
    [InlineData(409, "Conflict", "The object already exist.", true)]
    [InlineData(400, "Request_BadRequest", "Permission entry already exists.", true)]
    [InlineData(400, "Request_BadRequest", "One or more added object references already exist for the following modified properties: 'members'.", true)]
    [InlineData(400, "Request_BadRequest", "A conflicting object with this id is present.", true)]
    [InlineData(400, "Request_BadRequest", "Property is invalid.", false)]
    [InlineData(404, "Request_MultipleObjectsWithSameKeyValue", "already exists", false)]
    [InlineData(500, "Request_BadRequest", "already exists", false)]
    [InlineData(403, "Request_BadRequest", "already exists", false)]
    public void AlreadyExistsIsRecognisedOnlyOnBadRequestAndConflict(int status, string code, string message, bool expected)
    {
        Assert.Equal(expected, GraphException.IsAlreadyExistsError((HttpStatusCode)status, code, message));
        Assert.Equal(expected, new GraphException((HttpStatusCode)status, code, message).IsAlreadyExists);
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    [InlineData(501, false)]
    [InlineData(505, false)]
    [InlineData(408, false)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    [InlineData(409, false)]
    [InlineData(200, false)]
    public void OnlyThrottlingAndTheFourServerErrorsAreTransient(int status, bool expected)
    {
        Assert.Equal(expected, GraphException.IsTransientStatus((HttpStatusCode)status));
        Assert.Equal(expected, new GraphException((HttpStatusCode)status, "c", "m").IsTransient);
    }

    [Fact]
    public void TheStatusShortcutsAndTheAmbiguityFlagStartFalse()
    {
        var error = new GraphException(HttpStatusCode.NotFound, "c", "m");

        Assert.True(error.IsNotFound);
        Assert.False(error.IsUnauthorized);
        Assert.False(error.IsForbidden);
        Assert.False(error.IsAmbiguous);
        Assert.True(new GraphException(HttpStatusCode.Unauthorized, "c", "m").IsUnauthorized);
        Assert.True(new GraphException(HttpStatusCode.Forbidden, "c", "m").IsForbidden);
    }

    [Fact]
    public void TheMessageCarriesTheStatusCodeAndText()
    {
        Assert.Equal("Graph 400 Request_BadRequest: bad value", new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", "bad value").Message);
        Assert.Equal("Graph 500", new GraphException(HttpStatusCode.InternalServerError, string.Empty, string.Empty).Message.TrimEnd());
    }
}
