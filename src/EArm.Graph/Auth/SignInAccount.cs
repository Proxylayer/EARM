namespace EArm.Graph.Auth;

/// <summary>The stored interactive sign-in a token provider acts for (display only: the account and the client it signed in to), used in consent guidance.</summary>
/// <param name="Username">The account (user principal name), when known.</param>
/// <param name="ClientId">The client (application) id the sign-in uses, when known.</param>
public sealed record SignInAccount(string? Username, string? ClientId);
