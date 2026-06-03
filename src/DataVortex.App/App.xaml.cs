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

        var shell = _provider.GetRequiredService<ShellWindow>();
        MainWindow = shell;
        shell.Show();

        // Kick off connection / login without blocking the UI thread.
        _ = _provider.GetRequiredService<ShellViewModel>().InitializeAsync();
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
        services.AddSingleton<IAccountTestRegistry, AccountTestRegistry>();
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
        // Passculture API client (simple HttpClient instance)
        services.AddSingleton(sp => new DataVortex.Core.Passculture.TwoCaptchaService(
            sp.GetRequiredService<ISettingsService>().Current.TwoCaptchaApiKey,
            sp.GetRequiredService<ILogger<DataVortex.Core.Passculture.TwoCaptchaService>>()));

        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<ISettingsService>().Current;
            var handler = new System.Net.Http.HttpClientHandler();
            if (cfg.ProxyEnabled && !string.IsNullOrWhiteSpace(cfg.ProxyAddress))
            {
                handler.Proxy = new System.Net.WebProxy(cfg.ProxyAddress)
                {
                    Credentials = new System.Net.NetworkCredential(cfg.ProxyUsername ?? "", cfg.ProxyPassword ?? "")
                };
                handler.UseProxy = true;
            }
            return new DataVortex.Core.Passculture.PasscultureClient(
                new System.Net.Http.HttpClient(handler) { BaseAddress = new Uri("https://backend.passculture.app/") },
                sp.GetService<DataVortex.Core.Passculture.TwoCaptchaService>());
        });



        // ---- ViewModels ----
        services.AddSingleton<LogViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<ChannelsViewModel>();
        services.AddSingleton<QueuesViewModel>();
        services.AddSingleton<FilesViewModel>();
        services.AddSingleton<AccountsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddTransient<LoginViewModel>();

        // ---- Windows ----
        services.AddTransient<LoginDialog>();
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
