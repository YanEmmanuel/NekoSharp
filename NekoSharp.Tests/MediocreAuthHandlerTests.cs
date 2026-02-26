using System.Net;
using NekoSharp.Core.Models;
using NekoSharp.Core.Services;
using Xunit;

namespace NekoSharp.Tests;

public class ProviderAuthHandlerTests
{
    [Fact]
    public async Task TokenValido_Request200_SemRetry()
    {
        var profile = ProviderAuthProfile.CreateMediocreScan();
        var auth = new FakeAuthService { TokenToApply = "valid-token", RecoverResult = true };
        var inner = new SequenceHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
        {
            _ => new HttpResponseMessage(HttpStatusCode.OK)
        });

        using var client = new HttpClient(new ProviderAuthHandler(auth, profile, inner: inner));
        var response = await client.GetAsync("https://api.mediocretoons.site/capitulos/1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, auth.ApplyCalls);
        Assert.Equal(0, auth.RecoverCalls);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task Unauthorized_RefreshSuccess_FazRetryUnico()
    {
        var profile = ProviderAuthProfile.CreateMediocreScan();
        var auth = new FakeAuthService { TokenToApply = "stale-token", RecoverResult = true };
        var inner = new SequenceHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
        {
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
        });

        using var client = new HttpClient(new ProviderAuthHandler(auth, profile, inner: inner));
        var response = await client.GetAsync("https://api.mediocretoons.site/capitulos/2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, auth.ApplyCalls);
        Assert.Equal(1, auth.RecoverCalls);
        Assert.Equal(1, inner.UnauthorizedCount);
        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task Unauthorized_RefreshFalha_LoginSucesso_FazRetry()
    {
        var profile = ProviderAuthProfile.CreateMediocreScan();
        var auth = new FakeAuthService
        {
            TokenToApply = "token-old",
            RecoverResult = true,
            RecoverBehavior = _ => Task.FromResult(true)
        };

        var inner = new SequenceHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
        {
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
        });

        using var client = new HttpClient(new ProviderAuthHandler(auth, profile, inner: inner));
        var response = await client.GetAsync("https://api.mediocretoons.site/capitulos/3");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, auth.ApplyCalls);
        Assert.Equal(1, auth.RecoverCalls);
        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task Unauthorized_LoginCanceladoOuTimeout_PropagaErroAmigavel()
    {
        var profile = ProviderAuthProfile.CreateMediocreScan();
        var auth = new FakeAuthService
        {
            TokenToApply = "token-old",
            RecoverBehavior = _ => throw new System.Security.Authentication.AuthenticationException("Login do MediocreScan cancelado.")
        };

        var inner = new SequenceHandler(new Func<HttpRequestMessage, HttpResponseMessage>[]
        {
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        });

        using var client = new HttpClient(new ProviderAuthHandler(auth, profile, inner: inner));

        var ex = await Assert.ThrowsAsync<System.Security.Authentication.AuthenticationException>(
            () => client.GetAsync("https://api.mediocretoons.site/capitulos/4"));

        Assert.Contains("cancelado", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, auth.RecoverCalls);
        Assert.Equal(1, inner.CallCount);
    }

    private sealed class FakeAuthService : IProviderAuthService
    {
        public string TokenToApply { get; set; } = "token";
        public bool RecoverResult { get; set; } = true;
        public Func<string?, Task<bool>>? RecoverBehavior { get; set; }

        public int ApplyCalls { get; private set; }
        public int RecoverCalls { get; private set; }

        public Task<string?> ApplyAuthHeadersAsync(HttpRequestMessage request, CancellationToken ct = default)
        {
            ApplyCalls++;
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TokenToApply);
            return Task.FromResult<string?>(TokenToApply);
        }

        public Task<bool> RecoverFromUnauthorizedAsync(string? failedAccessToken, CancellationToken ct = default)
        {
            RecoverCalls++;
            if (RecoverBehavior is not null)
                return RecoverBehavior(failedAccessToken);
            return Task.FromResult(RecoverResult);
        }

        public Task<AuthSessionState> GetAuthStateAsync(CancellationToken ct = default)
            => Task.FromResult(new AuthSessionState { IsAuthenticated = true, Message = "Conectado" });

        public Task<AuthSessionState> LoginInteractivelyAsync(CancellationToken ct = default)
            => Task.FromResult(new AuthSessionState { IsAuthenticated = true, Message = "Conectado" });

        public Task ClearAuthAsync(CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;

        public int CallCount { get; private set; }
        public int UnauthorizedCount { get; private set; }

        public SequenceHandler(IEnumerable<Func<HttpRequestMessage, HttpResponseMessage>> responses)
        {
            _responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (_responses.Count == 0)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            var response = _responses.Dequeue().Invoke(request);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                UnauthorizedCount++;

            return Task.FromResult(response);
        }
    }
}
