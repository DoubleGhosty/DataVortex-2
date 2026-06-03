using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DataVortex.App.Services;
using DataVortex.Core.Abstractions;
using DataVortex.Core.Accounts;
using DataVortex.Core.Extraction;
using DataVortex.Core.Models;
using DataVortex.Core.Passculture;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;

namespace DataVortex.App.ViewModels;

public sealed partial class AccountsViewModel : ObservableObject
{
    private readonly IStorageService _storage;
    private readonly IUiDispatcher _ui;

    private int _accountCount;
    public int AccountCount
    {
        get => _accountCount;
        set => SetProperty(ref _accountCount, value);
    }

    private readonly PasscultureClient _passClient;
    private readonly IAccountTestRegistry _accounts;

    public AccountsViewModel(IStorageService storage, IUiDispatcher ui, PasscultureClient passClient, IAccountTestRegistry accounts)
    {
        _storage = storage;
        _ui = ui;
        _passClient = passClient;
        _accounts = accounts;
        Refresh();
    }

    private void PopulateFromRegistry()
    {
        // The registry is the single, already-deduplicated source of truth for accounts.
        Accounts.Clear();
        foreach (var e in _accounts.Snapshot())
        {
            // Hide credentials the backend rejected as invalid (HTTP 400).
            if (!e.Result.Success && e.Result.StatusCode == 400) continue;
            Accounts.Add(new CredentialEntry(e.Url, e.Email, e.Password, 0, "",
                Tested: true, TestSuccess: e.Result.Success, TestMessage: e.Result.Message,
                TestedUtc: e.Result.TestedUtc, AccessToken: e.Result.AccessToken, RefreshToken: e.Result.RefreshToken,
                Credit: e.Result.Credit, BirthDate: e.Result.BirthDate, StatusCode: e.Result.StatusCode));
        }
        AccountCount = Accounts.Count;
    }

    private string _captchaToken = "";
    public string CaptchaToken { get => _captchaToken; set => SetProperty(ref _captchaToken, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    public ObservableCollection<CredentialEntry> Accounts { get; } = new();

    [RelayCommand]
    private void CopyAccessToken(CredentialEntry? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.AccessToken)) return;
        try { System.Windows.Clipboard.SetText(entry.AccessToken); StatusText = "Access token copied to clipboard."; } catch { }
    }

    [RelayCommand]
    private void CopyRefreshToken(CredentialEntry? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.RefreshToken)) return;
        try { System.Windows.Clipboard.SetText(entry.RefreshToken); StatusText = "Refresh token copied to clipboard."; } catch { }
    }

    [RelayCommand]
    private async Task TestAccountsAsync()
    {
        StatusText = "Test des comptes…";
        await Task.Run(async () =>
        {
            try
            {
                // Collect every credential we know about: saved records + a fresh scan of extracted files.
                var creds = new List<CredentialEntry>();
                foreach (var r in _storage.LoadRecords())
                    if (r.Credentials is not null) creds.AddRange(r.Credentials);
                foreach (var path in _storage.EnumerateExtractedFiles(null))
                {
                    try { creds.AddRange(CredentialScanner.ScanFile(path)); } catch { }
                }

                // Deduplicate by NORMALIZED identity (email+password) so the same account is never tested twice.
                var unique = creds
                    .Where(c => !string.IsNullOrWhiteSpace(c.Username) || !string.IsNullOrWhiteSpace(c.Password))
                    .GroupBy(c => AccountKey.Of(c.Username, c.Password))
                    .Select(g => g.First())
                    .ToList();

                if (unique.Count == 0)
                {
                    _ui.Post(() => StatusText = "Aucun compte à tester.");
                    return;
                }

                int sent = 0, alreadyKnown = 0;
                foreach (var c in unique)
                {
                    if (_accounts.TryGet(c.Username, c.Password, out _)) { alreadyKnown++; continue; }
                    // The registry reserves atomically before any backend call → no duplicate captcha.
                    await AccountTester.TestOnceAsync(_passClient, _accounts, c);
                    sent++;
                }

                _ui.Post(() =>
                {
                    StatusText = $"Terminé : {unique.Count} compte(s) unique(s) — {sent} envoyé(s) au backend, {alreadyKnown} déjà connu(s).";
                    Refresh();
                });
            }
            catch (Exception ex)
            {
                _ui.Post(() => StatusText = ex.Message);
            }
        });
    }

    public void Refresh() => _ui.Post(() =>
    {
        try { PopulateFromRegistry(); }
        catch { AccountCount = 0; }
    });
}
