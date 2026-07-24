using System.IO;
using System.Threading.Tasks;
using System.Windows;
using DataVortex.App.Logging;
using DataVortex.App.Services;
using DataVortex.App.Themes;
using DataVortex.App.ViewModels;
using DataVortex.App.Views;
using DataVortex.Core.Abstractions;
using DataVortex.Core.Accounts;
using DataVortex.Core.Backfill;
using DataVortex.Core.Configuration;
using DataVortex.Core.Extraction;
using DataVortex.Core.Licensing;
using DataVortex.Core.Metrics;
using DataVortex.Core.Models;
using DataVortex.Core.Pipeline;
using DataVortex.Core.Security;
using DataVortex.Core.Storage;
using DataVortex.Core.Telegram;
using DataVortex.Core.Updates;
using DataVortex.Licensing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace DataVortex.App;

public partial class App : Application
{
    private ServiceProvider? _provider;
    private bool _handlingAccessLoss;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single global exception guards so a background fault never silently kills the app.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled UI exception");
            MessageBox.Show(args.Exception.Message, "DataVortex — unexpected error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error(args.ExceptionObject as Exception, "Unhandled domain exception");

        var paths = AppPaths.CreateDefault().EnsureCreated();
        ConfigureLogging(paths);
        _provider = ConfigureServices(paths);

        ThemeManager.Apply(_provider!.GetRequiredService<ISettingsService>().Current.Theme);
        DataVortex.Core.Accounts.AccountTester.ConfigureParallelism(
            _provider!.GetRequiredService<ISettingsService>().Current.MaxParallelAccountChecks);
        DataVortex.Core.Accounts.AccountTester.SetLogger(
            _provider!.GetRequiredService<ILoggerFactory>().CreateLogger("Checker"));
        _provider!.GetRequiredService<DataVortex.Core.Storage.CleanupService>().Start();
        _provider!.GetRequiredService<DataVortex.Core.Notifications.AccountNotifier>().Start();

        // Licence gate. A RELEASE build ALWAYS enforces — there is no runtime flag to flip (that was the old hole),
        // the enforcement code is simply compiled in and runs before the shell. A DEBUG build skips it and grants
        // full entitlements so development runs with no licence server; that bypass path does not exist in Release.
#if DEBUG
        _provider!.GetRequiredService<ILicenseGate>().Set(Entitlements.Unrestricted);
        // Dev builds have no licence server → feed the checker recipe directly so it works offline. This whole block
        // is compiled OUT of Release; the Release binary carries none of these Passculture values.
        _provider!.GetRequiredService<RecipeHolder>().Set(new OperationalRecipe
        {
            BaseUrl = "https://backend.passculture.app/",
            SiteKey = "6LdWB0caAAAAAKfVe3he0FqXQXOepICF-5aZh_rQ",
            PageUrl = "https://passculture.app/connexion?preventCancellation=true",
            SignInPath = "native/v1/signin",
            RefreshPath = "native/v1/refresh_access_token",
            UnsuspendPath = "native/v1/account/unsuspend",
            MePath = "native/v1/me",
        });
#else
        if (!EnsureLicensed())
        {
            Shutdown();
            return;
        }
#endif

        var shell = _provider!.GetRequiredService<ShellWindow>();
        MainWindow = shell;
        shell.Show();

        // Kick off connection / login without blocking the UI thread.
        _ = _provider!.GetRequiredService<ShellViewModel>().InitializeAsync();
    }

    /// <summary>Blocks startup until the licence is usable: evaluates the stored licence and, if it isn't
    /// Active/Degraded, shows the activation dialog. Returns false if the user quit without activating.</summary>
    private bool EnsureLicensed()
    {
        var manager = _provider!.GetRequiredService<ILicenseManager>();
        var status = manager.EvaluateAsync().GetAwaiter().GetResult();
        if (status.State is not (LicenseState.Active or LicenseState.Degraded))
        {
            var dialog = _provider!.GetRequiredService<LicenseActivationDialog>();
            if (dialog.ShowDialog() != true) return false;
            status = manager.EvaluateAsync().GetAwaiter().GetResult();
        }

        // Start the runtime watchdog: it re-checks with the server on a timer, so a revocation/suspension takes
        // effect within one interval instead of only at the next lease renewal.
        // Open a live runtime session (Palier B) BEFORE feeding the gate, so the online-only capabilities light up
        // immediately when a session is granted. No session (server unreachable / seat full) → pipeline + checker
        // stay gated off while the offline features still work.
        var session = _provider!.GetRequiredService<SessionManager>();
        session.StartAsync().GetAwaiter().GetResult();

        var guard = _provider!.GetRequiredService<LicenseGuard>();
        guard.SetStatus(status);
        guard.AccessLost += OnLicenseAccessLost;
        guard.Start(TimeSpan.FromSeconds(30));

        // Release-only tamper watchdog (this method only runs in Release). Reacts late + off-site on a debugger.
        _provider!.GetRequiredService<TamperGuard>().Start();
        return true;
    }

    /// <summary>Fired (on the UI thread) when the licence stops being usable while the app runs. Halts the
    /// pipeline, hides the UI and demands a valid licence again; if the user can't re-activate, the app closes.</summary>
    private void OnLicenseAccessLost(LicenseStatus status)
    {
        if (_handlingAccessLoss) return;
        _handlingAccessLoss = true;
        try
        {
            Log.Warning("Licence access lost at runtime: {State} — {Message}", status.State, status.Message);
            try { _provider!.GetRequiredService<PipelineCoordinator>().Pause(); } catch { /* best-effort */ }
            MainWindow?.Hide();

            var dialog = _provider!.GetRequiredService<LicenseActivationDialog>();
            if (dialog.ShowDialog() == true)
            {
                // Re-establish a live session against the (possibly new) licence before re-feeding the gate.
                _provider!.GetRequiredService<SessionManager>().RefreshNowAsync().GetAwaiter().GetResult();
                var guard = _provider!.GetRequiredService<LicenseGuard>();
                guard.SetStatus(guard.RefreshAsync(false).GetAwaiter().GetResult());
                try { _provider!.GetRequiredService<PipelineCoordinator>().Resume(); } catch { /* best-effort */ }
                MainWindow?.Show();
                guard.Start(TimeSpan.FromSeconds(30));
            }
            else
            {
                Shutdown();
            }
        }
        finally { _handlingAccessLoss = false; }
    }

    private static void ConfigureLogging(AppPaths paths)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(paths.Logs, "datavortex-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.Sink(ObservableLogSink.Instance)
            .CreateLogger();

        Log.Information("DataVortex starting. Data root: {Root}", paths.Root);
    }

    private static ServiceProvider ConfigureServices(AppPaths paths)
    {
        var services = new ServiceCollection();

        services.AddLogging(b =>
        {
            b.ClearProviders();
            b.AddSerilog(Log.Logger, dispose: false);
        });

        // ---- Core ----
        services.AddSingleton(paths);
        services.AddSingleton(ObservableLogSink.Instance);
        services.AddSingleton<ISettingsService>(_ => new SettingsService(paths.SettingsFile));
        services.AddSingleton(_ => new CredentialStore(paths.CredentialsFile));
        services.AddSingleton<IStorageService, StorageService>();
        services.AddSingleton<IMetricsService, MetricsService>();
        services.AddSingleton<IArchiveExtractor, ArchiveExtractor>();
        services.AddSingleton<IDownloadDeduplicator, DownloadDeduplicator>();
        services.AddSingleton<IPendingDownloadStore, PendingDownloadStore>();
        services.AddSingleton<IAccountTestRegistry, AccountTestRegistry>();
        services.AddSingleton<CleanupService>();
        services.AddSingleton<DataVortex.Core.Notifications.AccountNotifier>();
        services.AddSingleton<IUpdateService>(sp => new GitHubUpdateService(
            new System.Net.Http.HttpClient(), sp.GetRequiredService<AppPaths>(),
            sp.GetRequiredService<ILogger<GitHubUpdateService>>()));
        services.AddSingleton<ITelegramService, TelegramService>();
        services.AddSingleton<PipelineCoordinator>();
        services.AddSingleton<IPipelineCoordinator>(sp => (IPipelineCoordinator)sp.GetRequiredService<PipelineCoordinator>());
        services.AddSingleton<IBackfillService, BackfillService>();

        // ---- App services ----
        services.AddSingleton<IUiDispatcher, UiDispatcher>();
        services.AddSingleton<IDialogService, DialogService>();
        // Captcha solvers: register both, then expose the one chosen by the CaptchaProvider setting.
        services.AddSingleton(sp => new DataVortex.Core.Passculture.TwoCaptchaService(
            sp.GetRequiredService<ISettingsService>().Current.TwoCaptchaApiKey,
            sp.GetRequiredService<ILogger<DataVortex.Core.Passculture.TwoCaptchaService>>()));
        services.AddSingleton(sp => new DataVortex.Core.Passculture.CapMonsterService(
            sp.GetRequiredService<ISettingsService>().Current.CapMonsterApiKey,
            sp.GetRequiredService<ILogger<DataVortex.Core.Passculture.CapMonsterService>>()));
        services.AddSingleton<DataVortex.Core.Passculture.ICaptchaSolver>(sp =>
            string.Equals(sp.GetRequiredService<ISettingsService>().Current.CaptchaProvider, "CapMonster", StringComparison.OrdinalIgnoreCase)
                ? sp.GetRequiredService<DataVortex.Core.Passculture.CapMonsterService>()
                : sp.GetRequiredService<DataVortex.Core.Passculture.TwoCaptchaService>());

        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<ISettingsService>().Current;
            // Build one HttpClient per proxy from the imported list; rotate them per request (see ProxyPool).
            // No backend base URL here any more — it comes from the session-delivered recipe (Palier C).
            var pool = new DataVortex.Core.Passculture.ProxyPool(cfg.Proxies, cfg.ProxyEnabled);
            return new DataVortex.Core.Passculture.PasscultureClient(
                pool, sp.GetService<DataVortex.Core.Passculture.ICaptchaSolver>(),
                sp.GetRequiredService<ILogger<DataVortex.Core.Passculture.PasscultureClient>>(),
                sp.GetRequiredService<ILicenseGate>(),
                sp.GetRequiredService<IRecipeSource>());
        });



        // ---- Licensing (client) — enforcement is compiled in: Release always enforces, Debug bypasses (see OnStartup) ----
        services.AddSingleton<ILicenseStore>(sp => new DpapiLicenseStore(sp.GetRequiredService<AppPaths>().LicenseFile));
        services.AddSingleton<ILicenseApiClient>(_ =>
            // Server URL + public keys are EMBEDDED constants, never read from settings.json — so editing that file
            // can't redirect the client to a rogue server or swap the verification key. (The signed-token check is
            // the real guard; this keeps the pointers immutable too.)
            new HttpLicenseApiClient(
                PinnedHttpClientFactory.Create(LicensingConstants.ServerCertSpkiPin),
                LicensingConstants.DefaultServerUrl, LicensingConstants.AppHmacKey));
        services.AddSingleton<ILicenseManager>(sp => new LicenseManager(
            sp.GetRequiredService<ILicenseStore>(),
            sp.GetRequiredService<ILicenseApiClient>(),
            new LicenseOptions
            {
                PublicKeys = LicensingConstants.PublicKeys,
                AppVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "",
            }));
        services.AddSingleton<ILicenseGate, LicenseGate>();
        services.AddSingleton<RecipeHolder>();
        services.AddSingleton<IRecipeSource>(sp => sp.GetRequiredService<RecipeHolder>());
        services.AddSingleton<SessionManager>();
        services.AddSingleton<LicenseGuard>();
        services.AddSingleton<TamperGuard>();

        // ---- ViewModels ----
        services.AddSingleton<LogViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<ChannelsViewModel>();
        services.AddSingleton<QueuesViewModel>();
        services.AddSingleton<FilesViewModel>();
        services.AddSingleton<StatsViewModel>();
        services.AddSingleton<AccountsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<LicenseActivationViewModel>();

        // ---- Windows ----
        services.AddTransient<LoginDialog>();
        services.AddTransient<LicenseActivationDialog>();
        services.AddSingleton<ShellWindow>();

        return services.BuildServiceProvider();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Each stage is isolated so one failing teardown step can't abort the others, and each is time-bounded so a
        // download that ignores cancellation (or a stuck MTProto call) can't hang shutdown. Log.CloseAndFlush() runs
        // LAST so any exception above is captured, then Environment.Exit GUARANTEES the process actually dies — a
        // lingering non-background thread (WTelegram's network loop, an in-flight download) otherwise leaves a
        // "ghost" instance in Task Manager, and also blocks the self-updater which waits for this PID to exit.
        RunBounded("stopping backfill", () => _provider?.GetService<IBackfillService>()?.StopAsync().GetAwaiter().GetResult());
        RunBounded("stopping pipeline", () => _provider?.GetService<PipelineCoordinator>()?.StopAsync().GetAwaiter().GetResult());
        RunBounded("disconnecting Telegram", () => _provider?.GetService<ITelegramService>()?.DisconnectAsync().GetAwaiter().GetResult());
        RunBounded("disposing services", () => _provider?.Dispose());

        Log.Information("DataVortex stopping");
        Log.CloseAndFlush();
        base.OnExit(e);
        Environment.Exit(e.ApplicationExitCode);
    }

    /// <summary>Runs a teardown step on a worker thread and waits at most <paramref name="timeoutMs"/> for it, so a
    /// hung stop/dispose can't freeze shutdown. Never throws — failures are logged and shutdown proceeds.</summary>
    private static void RunBounded(string what, Action action, int timeoutMs = 4000)
    {
        try
        {
            var t = Task.Run(action);
            if (!t.Wait(timeoutMs))
                Log.Warning("Timed out {What} after {Ms}ms; forcing exit", what, timeoutMs);
            else if (t.IsFaulted)
                Log.Warning(t.Exception?.GetBaseException(), "Error {What}", what);
        }
        catch (Exception ex) { Log.Warning(ex, "Error {What}", what); }
    }
}
