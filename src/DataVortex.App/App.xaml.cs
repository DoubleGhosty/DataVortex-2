using System.IO;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace DataVortex.App;

public partial class App : Application
{
    private ServiceProvider? _provider;

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

        ThemeManager.Apply(_provider.GetRequiredService<ISettingsService>().Current.Theme);
        DataVortex.Core.Accounts.AccountTester.ConfigureParallelism(
            _provider.GetRequiredService<ISettingsService>().Current.MaxParallelAccountChecks);
        DataVortex.Core.Accounts.AccountTester.SetLogger(
            _provider.GetRequiredService<ILoggerFactory>().CreateLogger("Checker"));
        _provider.GetRequiredService<DataVortex.Core.Storage.CleanupService>().Start();
        _provider.GetRequiredService<DataVortex.Core.Notifications.AccountNotifier>().Start();

        // Licence gate (no-op unless enabled in settings). Runs before the shell so an unlicensed copy can't run.
        if (_provider.GetRequiredService<ISettingsService>().Current.LicensingEnabled && !EnsureLicensed())
        {
            Shutdown();
            return;
        }

        var shell = _provider.GetRequiredService<ShellWindow>();
        MainWindow = shell;
        shell.Show();

        // Kick off connection / login without blocking the UI thread.
        _ = _provider.GetRequiredService<ShellViewModel>().InitializeAsync();
    }

    /// <summary>Blocks startup until the licence is usable: evaluates the stored licence and, if it isn't
    /// Active/Degraded, shows the activation dialog. Returns false if the user quit without activating.</summary>
    private bool EnsureLicensed()
    {
        var manager = _provider!.GetRequiredService<ILicenseManager>();
        var status = manager.EvaluateAsync().GetAwaiter().GetResult();
        if (status.IsUsable) return true;

        var dialog = _provider!.GetRequiredService<LicenseActivationDialog>();
        return dialog.ShowDialog() == true;
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
            var pool = new DataVortex.Core.Passculture.ProxyPool(
                cfg.Proxies, new Uri("https://backend.passculture.app/"), cfg.ProxyEnabled);
            return new DataVortex.Core.Passculture.PasscultureClient(
                pool, sp.GetService<DataVortex.Core.Passculture.ICaptchaSolver>(),
                sp.GetRequiredService<ILogger<DataVortex.Core.Passculture.PasscultureClient>>());
        });



        // ---- Licensing (client) — gated by AppSettings.LicensingEnabled (off by default) ----
        services.AddSingleton<ILicenseStore>(sp => new DpapiLicenseStore(sp.GetRequiredService<AppPaths>().LicenseFile));
        services.AddSingleton<ILicenseApiClient>(sp =>
        {
            var cfg = sp.GetRequiredService<ISettingsService>().Current;
            var url = string.IsNullOrWhiteSpace(cfg.LicenseServerUrl) ? LicensingConstants.DefaultServerUrl : cfg.LicenseServerUrl;
            return new HttpLicenseApiClient(
                PinnedHttpClientFactory.Create(LicensingConstants.ServerCertSpkiPin), url, LicensingConstants.AppHmacKey);
        });
        services.AddSingleton<ILicenseManager>(sp => new LicenseManager(
            sp.GetRequiredService<ILicenseStore>(),
            sp.GetRequiredService<ILicenseApiClient>(),
            new LicenseOptions
            {
                PublicKeys = LicensingConstants.PublicKeys,
                AppVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "",
            }));

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
        // Each stage is isolated so one failing teardown step can't abort the others, and
        // Log.CloseAndFlush() runs LAST so any exception above is actually captured in the log file.
        try { _provider?.GetService<IBackfillService>()?.StopAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { Log.Warning(ex, "Error stopping backfill"); }

        try { _provider?.GetService<PipelineCoordinator>()?.StopAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { Log.Warning(ex, "Error stopping pipeline"); }

        try { _provider?.GetService<ITelegramService>()?.DisconnectAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { Log.Warning(ex, "Error disconnecting Telegram"); }

        try { _provider?.Dispose(); }
        catch (Exception ex) { Log.Warning(ex, "Error disposing services"); }

        Log.Information("DataVortex stopping");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
