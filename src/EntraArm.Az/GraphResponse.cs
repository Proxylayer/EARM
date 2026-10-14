using System.Net;
using System.Text.Json.Nodes;

namespace EntraArm.Az;

/// <summary>
/// A successful Graph response. <c>az rest</c> does not expose the HTTP status, so <see cref="Status"/> is inferred:
/// no output means 204; a JSON body means 200, or 201 for a POST / create-if-missing PATCH whose body carries an <c>id</c>.
/// </summary>
public sealed record GraphResponse(HttpStatusCode Status, JsonNode? Body)
{
    public bool IsCreated => Status == HttpStatusCode.Created;

    public bool IsNoContent => Status == HttpStatusCode.NoContent;
}
