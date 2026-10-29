using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Secrets;
using EArm.Engine.KeyVault;
using EArm.Graph;
using EArm.Tests.Cli;
using EArm.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>
/// <see cref="KeyVaultSecretResolver.ResolveAsync"/> reads up to <see cref="KeyVaultSecretResolver.MaxConcurrentReads"/> secrets at once, then takes the results in
/// parameter-name order: the failure reported, the constraint checks and the log lines are those of a one-by-one read, whichever read finished first. The
/// vault below answers each secret when the test says so (gates, not sleeps), so the completion order is forced.
/// </summary>
public sealed class KeyVaultConcurrentReadsTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A template whose parameters <c>p</c> (definitions as given) are each bound to the Key Vault secret <c>s-{p}</c>.</summary>
    private static TemplateDocument Document(params (string Parameter, string Definition)[] parameters)
    {
        var references = string.Join(", ", parameters.Select(p => "\"" + p.Parameter + "\": { \"reference\": { \"keyVault\": { \"id\": \"kv-vault\" }, \"secretName\": \"s-" + p.Parameter + "\" } }"));
        var definitions = string.Join(", ", parameters.Select(p => "\"" + p.Parameter + "\": " + p.Definition));
        return Expr.Parse(Expr.Envelope("\"parameters\": { " + definitions + " }, \"resources\": []"), ParameterSet.Parse("{ \"parameters\": { " + references + " } }"));
    }

    private static TemplateDocument Document(params string[] parameters)
    {
        return Document([.. parameters.Select(p => (p, "{ \"type\": \"securestring\" }"))]);
    }

    private static (KeyVaultSecretResolver Resolver, HttpClient Http, RecordingLogger<KeyVaultSecretResolver> Log) Resolver(HttpMessageHandler vault)
    {
        var http = new HttpClient(vault, disposeHandler: false);
        var client = new KeyVaultClient(http, new FakeJwtTokenProvider(), new RetryPolicy(new RetryOptions(), new NoDelay(), NullLogger<RetryPolicy>.Instance), GraphCloud.Public);
        var log = new RecordingLogger<KeyVaultSecretResolver>();
        return (new KeyVaultSecretResolver(client, log), http, log);
    }

    private static string Value(TemplateDocument document, string parameter)
    {
        Assert.True(SecretJson.IsSecret(document.ParameterValues[parameter].Value, out var secret));
        return secret.Value;
    }

    [Fact]
    public async Task TheSecretsAreReadConcurrently_AtMostFourAtOnce()
    {
        // Every read is held until four are inside the vault at the same time: a one-by-one resolver would never get there (the hang guard fails it).
        var vault = new GatedVault();
        string[] names = ["a1", "a2", "a3", "a4", "a5", "a6"];
        foreach (var name in names)
        {
            vault.Answer("s-" + name, async ct =>
            {
                await vault.InFlightReached(KeyVaultSecretResolver.MaxConcurrentReads).WaitAsync(HangGuard, ct);
                return (200, "value of " + name);
            });
        }

        var (resolver, http, _) = Resolver(vault);
        using var httpClient = http;

        var resolved = await resolver.ResolveAsync(Document(names), Ct);

        Assert.Equal(4, KeyVaultSecretResolver.MaxConcurrentReads);
        Assert.Equal(KeyVaultSecretResolver.MaxConcurrentReads, vault.MaxConcurrency);
        Assert.Equal(6, vault.Requests.Count);
        Assert.All(names, name => Assert.Equal("value of " + name, Value(resolved, name)));
        Assert.False(KeyVaultSecretResolver.HasPendingReferences(resolved));
    }

    [Fact]
    public async Task TheFirstFailureReported_IsThatOfTheFirstName_EvenWhenALaterNameFailsFirst()
    {
        // alpha's 404 is answered only after beta's 403: beta fails first, alpha is still the failure a one-by-one read would have reported.
        var vault = new GatedVault();
        vault.Answer("s-alpha", async ct =>
        {
            await vault.Answered("s-beta").WaitAsync(HangGuard, ct);
            return (404, ScriptedGraphHandler.Error("SecretNotFound", "A secret with (name/id) s-alpha was not found."));
        });
        vault.Answer("s-beta", _ => Task.FromResult((403, ScriptedGraphHandler.Error("Forbidden", "The user does not have secrets get permission."))));
        var (resolver, http, log) = Resolver(vault);
        using var httpClient = http;

        var error = await Assert.ThrowsAsync<KeyVaultResolutionException>(() => resolver.ResolveAsync(Document("alpha", "beta"), Ct));

        Assert.Equal(["s-beta", "s-alpha"], vault.AnswerOrder);
        Assert.Equal("alpha", error.ParameterName);
        Assert.Equal("s-alpha", error.Reference.SecretName);
        Assert.Contains("the vault has no such secret", error.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("s-beta", error.Message, StringComparison.Ordinal);
        Assert.Empty(log.Events);
    }

    [Fact]
    public async Task AFailure_DoesNotStopTheLaterReads_AndNothingAfterItIsBoundOrLogged()
    {
        var vault = new GatedVault();
        vault.Answer("s-alpha", _ => Task.FromResult((200, "first")));
        vault.Answer("s-beta", _ => Task.FromResult((404, ScriptedGraphHandler.Error("SecretNotFound", "gone"))));
        vault.Answer("s-gamma", async ct =>
        {
            await vault.Answered("s-beta").WaitAsync(HangGuard, ct);
            return (200, "late");
        });
        var (resolver, http, log) = Resolver(vault);
        using var httpClient = http;
        var document = Document("alpha", "beta", "gamma");

        var error = await Assert.ThrowsAsync<KeyVaultResolutionException>(() => resolver.ResolveAsync(document, Ct));

        Assert.Equal("beta", error.ParameterName);

        // gamma was read anyway (every read finishes before anything is reported), but only alpha, before the failure, was taken.
        Assert.Equal(3, vault.Requests.Count);
        Assert.Contains("s-gamma", vault.AnswerOrder);
        var resolved = Assert.Single(log.Events);
        Assert.Contains("'alpha'", resolved.Message, StringComparison.Ordinal);
        Assert.All(document.ParameterValues.Values, b => Assert.NotNull(b.PendingReference));
    }

    [Fact]
    public async Task TheConstraintChecks_FollowTheNameOrder_NotTheCompletionOrder()
    {
        // Both values break their parameter's constraint; beta's arrives first. alpha's violation is the one reported, as in a one-by-one read.
        var vault = new GatedVault();
        vault.Answer("s-alpha", async ct =>
        {
            await vault.Answered("s-beta").WaitAsync(HangGuard, ct);
            return (200, "much-too-long-for-alpha");
        });
        vault.Answer("s-beta", _ => Task.FromResult((200, "short")));
        var (resolver, http, _) = Resolver(vault);
        using var httpClient = http;
        var document = Document(("alpha", "{ \"type\": \"securestring\", \"maxLength\": 8 }"), ("beta", "{ \"type\": \"securestring\", \"minLength\": 20 }"));

        var error = await Assert.ThrowsAsync<KeyVaultResolutionException>(() => resolver.ResolveAsync(document, Ct));

        Assert.Equal(["s-beta", "s-alpha"], vault.AnswerOrder);
        Assert.Equal("alpha", error.ParameterName);
        Assert.Contains("maxLength", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("minLength", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("much-too-long-for-alpha", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEarlierConstraintViolation_WinsOverALaterReadFailure_ThatFinishedFirst()
    {
        var vault = new GatedVault();
        vault.Answer("s-alpha", async ct =>
        {
            await vault.Answered("s-beta").WaitAsync(HangGuard, ct);
            return (200, "x");
        });
        vault.Answer("s-beta", _ => Task.FromResult((403, ScriptedGraphHandler.Error("Forbidden", "denied"))));
        var (resolver, http, _) = Resolver(vault);
        using var httpClient = http;
        var document = Document(("alpha", "{ \"type\": \"securestring\", \"minLength\": 8 }"), ("beta", "{ \"type\": \"securestring\" }"));

        var error = await Assert.ThrowsAsync<KeyVaultResolutionException>(() => resolver.ResolveAsync(document, Ct));

        Assert.Equal("alpha", error.ParameterName);
        Assert.Contains("minLength", error.Message, StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task TheValuesAreBound_AndLoggedInNameOrder_WhateverOrderTheyArrived()
    {
        // Name order is alpha, beta, delta, gamma; the answers come delta, gamma, beta, alpha.
        var vault = new GatedVault();
        vault.Answer("s-delta", _ => Task.FromResult((200, "D")));
        vault.Answer("s-gamma", async ct =>
        {
            await vault.Answered("s-delta").WaitAsync(HangGuard, ct);
            return (200, "C");
        });
        vault.Answer("s-beta", async ct =>
        {
            await vault.Answered("s-gamma").WaitAsync(HangGuard, ct);
            return (200, "B");
        });
        vault.Answer("s-alpha", async ct =>
        {
            await vault.Answered("s-beta").WaitAsync(HangGuard, ct);
            return (200, "A");
        });
        var (resolver, http, log) = Resolver(vault);
        using var httpClient = http;

        var resolved = await resolver.ResolveAsync(Document("gamma", "alpha", "delta", "beta"), Ct);

        Assert.Equal(["s-delta", "s-gamma", "s-beta", "s-alpha"], vault.AnswerOrder);
        Assert.Equal(["A", "B", "C", "D"], new[] { "alpha", "beta", "gamma", "delta" }.Select(p => Value(resolved, p)));
        Assert.Equal(["alpha", "beta", "delta", "gamma"], log.Events.Select(e => e.Message.Split('\'')[1]));
    }

    /// <summary>
    /// A fake Key Vault: <c>GET /secrets/{name}</c> is answered by the script of that name (status, then the value for a 200 or the error document otherwise),
    /// recording which requests arrived, the order the answers were produced in and the most requests inside at once.
    /// </summary>
    private sealed class GatedVault : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<CancellationToken, Task<(int Status, string Body)>>> _answers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource> _answered = new(StringComparer.Ordinal);
        private readonly List<string> _requests = [];
        private readonly List<string> _answerOrder = [];
        private readonly List<(int Threshold, TaskCompletionSource Reached)> _inFlightWaiters = [];
        private readonly object _lock = new();
        private int _inFlight;
        private int _max;

        public IReadOnlyList<string> Requests
        {
            get
            {
                lock (_lock)
                {
                    return [.. _requests];
                }
            }
        }

        public IReadOnlyList<string> AnswerOrder
        {
            get
            {
                lock (_lock)
                {
                    return [.. _answerOrder];
                }
            }
        }

        public int MaxConcurrency
        {
            get
            {
                lock (_lock)
                {
                    return _max;
                }
            }
        }

        public void Answer(string secret, Func<CancellationToken, Task<(int Status, string Body)>> answer)
        {
            _answers[secret] = answer;
            _answered[secret] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>Completes once the answer of <paramref name="secret"/> has been produced.</summary>
        public Task Answered(string secret)
        {
            return _answered[secret].Task;
        }

        /// <summary>Completes once <paramref name="threshold"/> requests have been inside the vault at the same time.</summary>
        public Task InFlightReached(int threshold)
        {
            lock (_lock)
            {
                if (_max >= threshold)
                {
                    return Task.CompletedTask;
                }

                var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _inFlightWaiters.Add((threshold, reached));
                return reached.Task;
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string secret = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[1];
            lock (_lock)
            {
                _requests.Add(secret);
                _inFlight++;
                _max = Math.Max(_max, _inFlight);
                foreach (var waiter in _inFlightWaiters.Where(w => _max >= w.Threshold).ToList())
                {
                    waiter.Reached.TrySetResult();
                    _inFlightWaiters.Remove(waiter);
                }
            }

            try
            {
                var (status, body) = await _answers[secret](cancellationToken).ConfigureAwait(false);
                lock (_lock)
                {
                    _answerOrder.Add(secret);
                }

                string json = status == 200 ? "{ \"value\": " + JsonSerializer.Serialize(body) + " }" : body;
                return new HttpResponseMessage((HttpStatusCode)status) { RequestMessage = request, Content = new StringContent(json, new UTF8Encoding(false), "application/json") };
            }
            finally
            {
                lock (_lock)
                {
                    _inFlight--;
                }

                _answered[secret].TrySetResult();
            }
        }
    }
}
