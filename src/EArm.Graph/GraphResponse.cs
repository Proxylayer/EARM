using System.Net;
using System.Text.Json.Nodes;

namespace EArm.Graph;

/// <summary>A successful Graph response: the real HTTP status (200, 201, 202, 204, ...) and the parsed JSON body, or null when the response had none.</summary>
public sealed record GraphResponse(HttpStatusCode Status, JsonNode? Body)
{
    public bool IsCreated => Status == HttpStatusCode.Created;

    public bool IsNoContent => Status == HttpStatusCode.NoContent;
}
