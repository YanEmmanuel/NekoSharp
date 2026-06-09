using CommunityToolkit.Mvvm.ComponentModel;
using NekoSharp.Core.Interfaces;

namespace NekoSharp.App.ViewModels;

public sealed partial class ProviderAuthCardViewModel : ObservableObject
{
    private IInteractiveAuthProvider _provider;
    private ICredentialAuthProvider? _credentialProvider;
    private readonly Action<string, string> _setStatus;

    public ProviderAuthCardViewModel(
        string providerName,
        IInteractiveAuthProvider provider,
        ICredentialAuthProvider? credentialProvider,
        Action<string, string> setStatus)
    {
        ProviderName = providerName;
        _provider = provider;
        _credentialProvider = credentialProvider;
        _setStatus = setStatus;
    }

    public string ProviderName { get; }

    public bool SupportsCredentials => _credentialProvider is not null;

    public string StatusSubtitle
    {
        get
        {
            var user = string.IsNullOrWhiteSpace(User) ? "-" : User;
            var saved = SupportsCredentials ? (HasSavedCredentials ? "sim" : "não") : "n/a";
            return $"Status: {Status} • Usuário: {user} • Login salvo: {saved} • Atualizado: {LastUpdated}";
        }
    }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Desconectado";
    [ObservableProperty] private string _user = string.Empty;
    [ObservableProperty] private string _lastUpdated = "-";
    [ObservableProperty] private string _usernameOrEmail = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _rememberCredentials = true;
    [ObservableProperty] private bool _hasSavedCredentials;

    public void UpdateProviders(IInteractiveAuthProvider provider, ICredentialAuthProvider? credentialProvider)
    {
        _provider = provider;
        _credentialProvider = credentialProvider;
        OnPropertyChanged(nameof(SupportsCredentials));
        OnPropertyChanged(nameof(StatusSubtitle));
    }

    public async Task ConnectInBrowserAsync(CancellationToken ct = default)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            _setStatus($"Abrindo login do {ProviderName} no navegador...", "info");
            await _provider.LoginInteractivelyAsync(ct);
            await RefreshStateCoreAsync(ct);
            _setStatus($"Login do {ProviderName} concluído.", "success");
        }
        catch (Exception ex)
        {
            _setStatus($"Falha no login do {ProviderName}: {ex.Message}", "error");
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LoginWithCredentialsAsync(CancellationToken ct = default)
    {
        if (_credentialProvider is null)
        {
            _setStatus($"Login por email/senha não está disponível para {ProviderName}.", "warning");
            return;
        }

        if (IsBusy)
            return;

        if (string.IsNullOrWhiteSpace(UsernameOrEmail) || string.IsNullOrWhiteSpace(Password))
        {
            _setStatus($"Preencha email ou usuário e senha para conectar no {ProviderName}.", "warning");
            return;
        }

        IsBusy = true;
        try
        {
            _setStatus($"Realizando login do {ProviderName} com email/senha...", "info");
            await _credentialProvider.LoginWithCredentialsAsync(
                UsernameOrEmail.Trim(),
                Password,
                RememberCredentials,
                ct);

            if (RememberCredentials)
                Password = string.Empty;

            await RefreshStateCoreAsync(ct);
            _setStatus($"Login por credenciais do {ProviderName} concluído.", "success");
        }
        catch (Exception ex)
        {
            _setStatus($"Falha no login por credenciais do {ProviderName}: {ex.Message}", "error");
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ClearAuthAsync(CancellationToken ct = default)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            await _provider.ClearAuthAsync(ct);
            await RefreshStateCoreAsync(ct);
            _setStatus($"Sessão do {ProviderName} removida.", "success");
        }
        catch (Exception ex)
        {
            _setStatus($"Falha ao limpar sessão do {ProviderName}: {ex.Message}", "error");
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ClearSavedCredentialsAsync(CancellationToken ct = default)
    {
        if (_credentialProvider is null)
        {
            _setStatus($"Credenciais salvas não são suportadas para {ProviderName}.", "warning");
            return;
        }

        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            await _credentialProvider.ClearSavedCredentialsAsync(ct);
            HasSavedCredentials = false;
            Password = string.Empty;
            LastUpdated = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");
            _setStatus($"Credenciais salvas do {ProviderName} removidas.", "success");
        }
        catch (Exception ex)
        {
            _setStatus($"Falha ao limpar credenciais salvas do {ProviderName}: {ex.Message}", "error");
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RefreshStateAsync(CancellationToken ct = default)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            await RefreshStateCoreAsync(ct);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshStateCoreAsync(CancellationToken ct)
    {
        try
        {
            var state = await _provider.GetAuthStateAsync(ct);
            Status = state.IsAuthenticated
                ? "Conectado"
                : state.IsExpired ? "Expirado" : "Desconectado";

            User = !string.IsNullOrWhiteSpace(state.UserDisplayName)
                ? state.UserDisplayName!
                : !string.IsNullOrWhiteSpace(state.UserEmail) ? state.UserEmail! : string.Empty;

            HasSavedCredentials = _credentialProvider is not null &&
                                  await _credentialProvider.HasSavedCredentialsAsync(ct);

            LastUpdated = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");
        }
        catch (Exception ex)
        {
            Status = "Erro";
            User = ex.Message;
            LastUpdated = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");
            HasSavedCredentials = false;
        }
    }

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(StatusSubtitle));
    partial void OnUserChanged(string value) => OnPropertyChanged(nameof(StatusSubtitle));
    partial void OnLastUpdatedChanged(string value) => OnPropertyChanged(nameof(StatusSubtitle));
    partial void OnHasSavedCredentialsChanged(bool value) => OnPropertyChanged(nameof(StatusSubtitle));
}
