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

    private int _countValid;
    public int CountValid { get => _countValid; set => SetProperty(ref _countValid, value); }

    private int _countBan;
    public int CountBan { get => _countBan; set => SetProperty(ref _countBan, value); }

    private int _countCustom;
    public int CountCustom { get => _countCustom; set => SetProperty(ref _countCustom, value); }

    private int _countExpire;
    public int CountExpire { get => _countExpire; set => SetProperty(ref _countExpire, value); }

    private int _countRetry;
    public int CountRetry { get => _countRetry; set => SetProperty(ref _countRetry, value); }

    // Category filters (toggled from the badges); changing one re-filters the grid from page 1.
    private bool _showValid = true;
    public bool ShowValid { get => _showValid; set { if (SetProperty(ref _showValid, value)) ResetAndRefresh(); } }

    private bool _showBan = true;
    public bool ShowBan { get => _showBan; set { if (SetProperty(ref _showBan, value)) ResetAndRefresh(); } }

    private bool _showCustom = true;
    public bool ShowCustom { get => _showCustom; set { if (SetProperty(ref _showCustom, value)) ResetAndRefresh(); } }

    private bool _showExpire = true;
    public bool ShowExpire { get => _showExpire; set { if (SetProperty(ref _showExpire, value)) ResetAndRefresh(); } }

    // ---- Search + pagination (indexed SQL, so the grid never loads the whole store) ----
    private const int PageSize = 200;

    private string _searchText = "";
    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) ResetAndRefresh(); } }

    private int _page;
    public int Page { get => _page; set { if (SetProperty(ref _page, value)) { OnPropertyChanged(nameof(PageInfo)); OnPropertyChanged(nameof(CanPrev)); OnPropertyChanged(nameof(CanNext)); } } }

    private int _totalResults;
    public int TotalResults { get => _totalResults; set { if (SetProperty(ref _totalResults, value)) { OnPropertyChanged(nameof(PageInfo)); OnPropertyChanged(nameof(CanPrev)); OnPropertyChanged(nameof(CanNext)); } } }

    public string PageInfo => TotalResults == 0
        ? "0 compte"
        : $"Page {Page + 1} / {Math.Max(1, (TotalResults + PageSize - 1) / PageSize)}  ·  {TotalResults} affichés";
    public bool CanPrev => Page > 0;
    public bool CanNext => (Page + 1) * PageSize < TotalResults;

    [RelayCommand]
    private void NextPage() { if (CanNext) { Page++; Refresh(); } }

    [RelayCommand]
    private void PrevPage() { if (CanPrev) { Page--; Refresh(); } }

    /// <summary>The categories to show: always "other"/retry, plus the toggled VALIDE/BAN/CUSTOM. INVALIDE
    /// (HTTP 400) is never included, so it stays hidden as before.</summary>
    private List<string> VisibleCategories()
    {
        var cats = new List<string> { "" };
        if (ShowValid) cats.Add("VALIDE");
        if (ShowBan) cats.Add("BAN");
        if (ShowCustom) cats.Add("CUSTOM");
        if (ShowExpire) cats.Add("EXPIRE");
        return cats;
    }

    private void ResetAndRefresh() { Page = 0; Refresh(); }

    private readonly PasscultureClient _passClient;
    private readonly IAccountTestRegistry _accounts;
    private readonly IDialogService _dialogs;
    private readonly ICaptchaSolver _twoCaptcha;

    // One checker run at a time; the Stop button cancels its token.
    private CancellationTokenSource? _checkerCts;
    private bool _isChecking;
    public bool IsChecking { get => _isChecking; set => SetProperty(ref _isChecking, value); }

    [RelayCommand]
    private void StopChecker()
    {
        if (_checkerCts is { } cts && !cts.IsCancellationRequested) { cts.Cancel(); StatusText = "Arrêt demandé…"; }
    }

    /// <summary>Runs one checker job: rejects a second concurrent run, owns a cancellation token wired to the
    /// Stop button, and always resets the busy flag.</summary>
    private async Task RunCheckerAsync(Func<CancellationToken, Task> body)
    {
        if (IsChecking) { StatusText = "Un check est déjà en cours."; return; }
        var cts = _checkerCts = new CancellationTokenSource();
        IsChecking = true;
        try { await body(cts.Token); }
        catch (OperationCanceledException) { _ui.Post(() => StatusText = "Annulé."); }
        catch (Exception ex) { _ui.Post(() => StatusText = ex.Message); }
        finally
        {
            IsChecking = false;
            if (ReferenceEquals(_checkerCts, cts)) _checkerCts = null;
            cts.Dispose();
        }
    }

    public AccountsViewModel(IStorageService storage, IUiDispatcher ui, PasscultureClient passClient,
        IAccountTestRegistry accounts, IDialogService dialogs, ICaptchaSolver twoCaptcha)
    {
        _storage = storage;
        _ui = ui;
        _passClient = passClient;
        _accounts = accounts;
        _dialogs = dialogs;
        _twoCaptcha = twoCaptcha;
        CaptchaRequests = _twoCaptcha.RequestCount;
        _twoCaptcha.RequestCountChanged += n => _ui.Post(() => CaptchaRequests = n);
        AccountTester.RetryAbandoned += () => _ui.Post(() => CountRetry++);
        Refresh();
    }

    private static CredentialEntry ToCredential(AccountRecord a) => new(
        a.Url, a.Email, a.Password, 0, "",
        Tested: true, TestSuccess: a.Success, TestMessage: a.Message,
        TestedUtc: a.TestedUtc, AccessToken: a.AccessToken, RefreshToken: a.RefreshToken,
        Credit: a.Credit, BirthDate: a.BirthDate, StatusCode: a.StatusCode, AccountState: a.AccountState);

    /// <summary>Loads only the current page from indexed SQL (sorted by credit), plus the live category
    /// counters — never the whole store, so the grid stays responsive however large the registry grows.</summary>
    private async Task LoadPageAsync()
    {
        var text = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
        var cats = VisibleCategories();
        var offset = Page * PageSize;

        var (total, rows, counts) = await Task.Run(() => (
            _storage.CountAccounts(text, cats),
            _storage.SearchAccounts(text, cats, PageSize, offset),
            _storage.GetAccountCategoryCounts())).ConfigureAwait(false);

        _ui.Post(() =>
        {
            TotalResults = total;
            AccountCount = total;
            Accounts.Clear();
            foreach (var a in rows) Accounts.Add(ToCredential(a));

            CountValid = counts.FirstOrDefault(c => c.Category == "VALIDE")?.Count ?? 0;
            CountBan = counts.FirstOrDefault(c => c.Category == "BAN")?.Count ?? 0;
            CountCustom = counts.FirstOrDefault(c => c.Category == "CUSTOM")?.Count ?? 0;
            CountExpire = counts.FirstOrDefault(c => c.Category == "EXPIRE")?.Count ?? 0;
        });
    }

    private string _captchaToken = "";
    public string CaptchaToken { get => _captchaToken; set => SetProperty(ref _captchaToken, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    public ObservableCollection<CredentialEntry> Accounts { get; } = new();

    /// <summary>Opens Chrome already logged into passculture for this account: mints a fresh access token from
    /// the stored refresh token (the stored one expires in ~15 min), then injects both tokens into the site's
    /// localStorage via DevTools so the session is open. Zero captcha.</summary>
    [RelayCommand]
    private async Task OpenSessionAsync(CredentialEntry? entry)
    {
        if (entry is null) return;
        if (string.IsNullOrWhiteSpace(entry.RefreshToken) && string.IsNullOrWhiteSpace(entry.AccessToken))
        {
            StatusText = "Aucun token disponible pour ce compte.";
            return;
        }

        StatusText = $"Ouverture de session pour {entry.Username}…";
        try
        {
            // Refresh the (short-lived) access token first; fall back to the stored one if refresh fails.
            var access = entry.AccessToken;
            if (!string.IsNullOrWhiteSpace(entry.RefreshToken))
            {
                var r = await _passClient.RefreshAccessTokenAsync(entry.RefreshToken!);
                if (!string.IsNullOrWhiteSpace(r.AccessToken)) access = r.AccessToken;
            }
            if (string.IsNullOrWhiteSpace(access))
            {
                StatusText = "Impossible d'obtenir un token valide (refresh expiré ?).";
                return;
            }

            await BrowserSessionLauncher.OpenAsync(access!, entry.RefreshToken);
            StatusText = $"Session ouverte dans Chrome pour {entry.Username}.";
        }
        catch (Exception ex)
        {
            StatusText = "Échec de l'ouverture de session : " + ex.Message;
        }
    }

    [RelayCommand]
    private Task TestAccountsAsync() => RunCheckerAsync(async ct =>
    {
        StatusText = "Test des comptes…";
        await Task.Run(async () =>
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
                new ParallelOptions { MaxDegreeOfParallelism = 10, CancellationToken = ct },
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
        }, ct);
    });

    /// <summary>Re-verifies every successful account that still holds a refresh token — credit, status (incl.
    /// EXPIRE) and suspension — by reusing the stored refresh token, so it costs <b>zero captcha</b> and no
    /// sign-in. Catches accounts whose credit has since expired (ex_beneficiary → EXPIRE).</summary>
    [RelayCommand]
    private Task RefreshCreditsAsync() => RunCheckerAsync(async ct =>
    {
        var candidates = await Task.Run(() => _storage.LoadAccountsToRecheck(), ct);
        if (candidates.Count == 0)
        {
            _ui.Post(() => StatusText = "Aucun compte à revérifier (pas de refresh token).");
            return;
        }

        _ui.Post(() => StatusText = $"Rafraîchissement de {candidates.Count} compte(s)…");
        await Task.Run(async () =>
        {
            int got = 0, suspended = 0, done = 0;
            await Parallel.ForEachAsync(candidates,
                new ParallelOptions { MaxDegreeOfParallelism = 10, CancellationToken = ct },
                async (a, token) =>
                {
                    var (_, gotCredit, wasSuspended) = await AccountTester.RefreshCreditAsync(_passClient, _accounts, a, token);
                    if (gotCredit) Interlocked.Increment(ref got);
                    if (wasSuspended) Interlocked.Increment(ref suspended);

                    int d = Interlocked.Increment(ref done);
                    if (d % 10 == 0 || d == candidates.Count)
                    {
                        int g = Volatile.Read(ref got), s = Volatile.Read(ref suspended);
                        _ui.Post(() => StatusText = $"Rafraîchissement : {d}/{candidates.Count} — {g} crédit(s), {s} suspendu(s)");
                    }
                });

            _ui.Post(() =>
            {
                StatusText = $"Terminé : {got} crédit(s) récupéré(s), {suspended} compte(s) passé(s) suspendu(s) sur {candidates.Count} rafraîchi(s).";
                Refresh();
            });
        }, ct);
    });

    /// <summary>DANGER: forgets every stored account and re-tests them all from scratch via a real sign-in
    /// (NOT the refresh token) — one captcha per account. The grid is emptied and re-filled as results land.</summary>
    [RelayCommand]
    private async Task RetestAllAccountsAsync()
    {
        var creds = await Task.Run(() => _storage.LoadAccounts()
            .Where(a => !string.IsNullOrWhiteSpace(a.Email) || !string.IsNullOrWhiteSpace(a.Password))
            .Select(a => new CredentialEntry(a.Url, a.Email, a.Password, 0, ""))
            .ToList());

        if (creds.Count == 0) { StatusText = "Aucun compte enregistré à retester."; return; }

        if (!_dialogs.Confirm(
                $"⚠️ ATTENTION — re-test COMPLET de {creds.Count} compte(s).\n\n" +
                "• Tous les résultats enregistrés vont être SUPPRIMÉS puis recalculés.\n" +
                "• Chaque compte est retesté via une VRAIE connexion (PAS le refresh token).\n" +
                "• Chaque test consomme un CAPTCHA (coût réel) et prend du temps.\n" +
                "• Le panneau est vidé puis re-rempli au fur et à mesure.\n\n" +
                "Action irréversible. Continuer ?",
                "Re-tester TOUS les comptes"))
        {
            StatusText = "Re-test annulé.";
            return;
        }

        await RunCheckerAsync(async ct =>
        {
            // Wipe all prior knowledge so every account is genuinely re-sent (not skipped as 'already known').
            _accounts.Reset();
            _ui.Post(() => { Accounts.Clear(); StatusText = $"Re-test complet de {creds.Count} compte(s)…"; });

            await Task.Run(async () =>
            {
                int done = 0;
                await Parallel.ForEachAsync(creds,
                    new ParallelOptions { MaxDegreeOfParallelism = 10, CancellationToken = ct },
                    async (c, token) =>
                    {
                        await AccountTester.TestOnceAsync(_passClient, _accounts, c, token);
                        int d = Interlocked.Increment(ref done);
                        if (d % 5 == 0 || d == creds.Count)
                            _ui.Post(() => { StatusText = $"Re-test : {d}/{creds.Count}"; Refresh(); });
                    });

                _ui.Post(() => { StatusText = $"Re-test complet terminé : {creds.Count} compte(s)."; Refresh(); });
            }, ct);
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

        await RunCheckerAsync(async ct =>
        {
            await Task.Run(async () =>
            {
                int sent = 0, alreadyKnown = 0, done = 0;
                await Parallel.ForEachAsync(unique,
                    new ParallelOptions { MaxDegreeOfParallelism = 10, CancellationToken = ct },
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
            }, ct);
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

    public void Refresh() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        try { await LoadPageAsync().ConfigureAwait(false); }
        catch { _ui.Post(() => AccountCount = 0); }
    }
}
