using NekoSharp.App.ViewModels;
using NekoSharp.Core.Interfaces;
using NekoSharp.Core.Models;
using Xunit;

namespace NekoSharp.Tests;

public sealed class ProviderAuthCardViewModelTests
{
    [Fact]
    public async Task RefreshStateAsync_UpdatesConnectedStatusAndSavedCredentialsFlag()
    {
        var provider = new FakeCredentialProvider
        {
            AuthState = new AuthSessionState
            {
                IsAuthenticated = true,
                UserDisplayName = "GrandePika"
            },
            HasSavedCredentialsResult = true
        };

        var card = new ProviderAuthCardViewModel(
            "Little Tyrant",
            provider,
            provider,
            static (_, _) => { });

        await card.RefreshStateAsync();

        Assert.Equal("Conectado", card.Status);
        Assert.Equal("GrandePika", card.User);
        Assert.True(card.HasSavedCredentials);
    }

    [Fact]
    public async Task ConnectInBrowserAsync_UsesInteractiveLoginAndPublishesStatusMessages()
    {
        var provider = new FakeCredentialProvider
        {
            AuthState = new AuthSessionState
            {
                IsAuthenticated = true,
                UserDisplayName = "GrandePika"
            }
        };
        var statuses = new List<(string Message, string Type)>();

        var card = new ProviderAuthCardViewModel(
            "Little Tyrant",
            provider,
            provider,
            (message, type) => statuses.Add((message, type)));

        await card.ConnectInBrowserAsync();

        Assert.Equal(1, provider.InteractiveLoginCalls);
        Assert.Contains(statuses, entry => entry.Type == "info" && entry.Message.Contains("Abrindo login do Little Tyrant", StringComparison.Ordinal));
        Assert.Contains(statuses, entry => entry.Type == "success" && entry.Message.Contains("Login do Little Tyrant concluído", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoginWithCredentialsAsync_ClearsPasswordWhenRememberCredentialsIsEnabled()
    {
        var provider = new FakeCredentialProvider
        {
            AuthState = new AuthSessionState
            {
                IsAuthenticated = true,
                UserDisplayName = "GrandePika"
            }
        };

        var card = new ProviderAuthCardViewModel(
            "Little Tyrant",
            provider,
            provider,
            static (_, _) => { })
        {
            UsernameOrEmail = "gika@example.com",
            Password = "123456",
            RememberCredentials = true
        };

        await card.LoginWithCredentialsAsync();

        Assert.Equal(1, provider.CredentialLoginCalls);
        Assert.Equal("gika@example.com", provider.LastUsernameOrEmail);
        Assert.Equal("123456", provider.LastPassword);
        Assert.True(provider.LastRememberCredentials);
        Assert.Equal(string.Empty, card.Password);
    }

    private sealed class FakeCredentialProvider : ICredentialAuthProvider
    {
        public AuthSessionState AuthState { get; set; } = new();
        public bool HasSavedCredentialsResult { get; set; }
        public int InteractiveLoginCalls { get; private set; }
        public int CredentialLoginCalls { get; private set; }
        public string? LastUsernameOrEmail { get; private set; }
        public string? LastPassword { get; private set; }
        public bool LastRememberCredentials { get; private set; }

        public Task<AuthSessionState> GetAuthStateAsync(CancellationToken ct = default)
            => Task.FromResult(AuthState);

        public Task<AuthSessionState> LoginInteractivelyAsync(CancellationToken ct = default)
        {
            InteractiveLoginCalls++;
            return Task.FromResult(AuthState);
        }

        public Task<AuthSessionState> LoginWithCredentialsAsync(
            string usernameOrEmail,
            string password,
            bool rememberCredentials = true,
            CancellationToken ct = default)
        {
            CredentialLoginCalls++;
            LastUsernameOrEmail = usernameOrEmail;
            LastPassword = password;
            LastRememberCredentials = rememberCredentials;
            return Task.FromResult(AuthState);
        }

        public Task<bool> HasSavedCredentialsAsync(CancellationToken ct = default)
            => Task.FromResult(HasSavedCredentialsResult);

        public Task ClearSavedCredentialsAsync(CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ClearAuthAsync(CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
