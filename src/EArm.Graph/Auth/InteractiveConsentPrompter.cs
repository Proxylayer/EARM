using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Identity;

namespace EArm.Graph.Auth;

/// <summary>
/// The real <see cref="IConsentPrompter"/>: an interactive request (browser, or device code) for exactly the missing scope set, as the account of the stored
/// sign-in (its client and tenant, the <c>earm</c> cache). The new grant lands in the shared persistent cache, where the token provider's silent retry finds it.
/// Made by <see cref="GraphAuthFactory.CreateConsentPrompter"/>.
/// </summary>
internal sealed class InteractiveConsentPrompter : IConsentPrompter
{
    private readonly GraphAuthFactory _factory;
    private readonly GraphAuthOptions _options;
    private readonly AuthenticationRecord _record;
    private readonly bool _deviceCode;
    private readonly Func<DeviceCodeInfo, CancellationToken, Task>? _deviceCodeCallback;
    private readonly Action<string>? _notice;

    public InteractiveConsentPrompter(GraphAuthFactory factory, GraphAuthOptions options, AuthenticationRecord record, bool deviceCode, Func<DeviceCodeInfo, CancellationToken, Task>? deviceCodeCallback, Action<string>? notice)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(record);
        _factory = factory;
        _options = options;
        _record = record;
        _deviceCode = deviceCode;
        _deviceCodeCallback = deviceCodeCallback;
        _notice = notice;
    }

    public async Task ConsentAsync(TokenScopes scopes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        _notice?.Invoke($"This run needs consent for {scopes} that the sign-in of {_record.Username} has not been granted yet. "
            + (_deviceCode ? "Follow the device-code instructions to grant it." : "Complete the consent prompt in your browser to grant it.")
            + " " + GraphAuthGuidance.SharedConsentNote);
        var granted = await _factory.AuthenticateAsync(_options, scopes, _record, _deviceCode, _deviceCodeCallback, cancellationToken).ConfigureAwait(false);
        GraphAuthFactory.EnsureSameAccount(_record, granted);
    }
}
