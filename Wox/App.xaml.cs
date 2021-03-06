using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Collections.Generic;
using System.Threading;
using System.Globalization;

using CommandLine;
using NLog;

using Wox.Core;
using Wox.Core.Configuration;
using Wox.Core.Plugin;
using Wox.Core.Resource;
using Wox.Helper;
using Wox.Infrastructure;
using Wox.Infrastructure.Http;
using Wox.Infrastructure.Image;
using Wox.Infrastructure.Logger;
using Wox.Infrastructure.UserSettings;
using Wox.ViewModel;
using Stopwatch = Wox.Infrastructure.Stopwatch;



namespace Wox
{
    public partial class App : IDisposable, ISingleInstanceApp
    {
        public static PublicAPIInstance API { get; private set; }
        private const string Unique = "Wox_Unique_Application_Mutex";
        private static bool _disposed;
        private Settings _settings;
        private MainViewModel _mainVM;
        private SettingWindowViewModel _settingsVM;
        private readonly Updater _updater = new Updater(Wox.Properties.Settings.Default.GithubRepo);
        private readonly Portable _portable = new Portable();
        private readonly Alphabet _alphabet = new Alphabet();
        private StringMatcher _stringMatcher;
        
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private class Options
        {
            [Option('q', "query", Required = false, HelpText = "Specify text to query on startup.")]
            public string QueryText { get; set; }
        }

        private void ParseCommandLineArgs(IList<string> args)
        {
            if (args == null)
                return;

            Parser.Default.ParseArguments<Options>(args)
                .WithParsed(o =>
                {
                    if (o.QueryText != null && _mainVM != null)
                        _mainVM.ChangeQueryText(o.QueryText);
                });
        }

        [STAThread]
        public static void Main()
        {
            // force english exception message for better github issue
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");
            if (SingleInstance<App>.InitializeAsFirstInstance(Unique))
            {
                using (var application = new App())
                {
                    application.InitializeComponent();
                    application.Run();
                }
            }
        }

        private void OnStartup(object sender, StartupEventArgs e)
        {
            Logger.StopWatchNormal("Startup cost", () =>
            {
                Constant.Initialize();

                _portable.PreStartCleanUpAfterPortabilityUpdate();

                
                Logger.WoxInfo("Begin Wox startup----------------------------------------------------");
                Logger.WoxInfo($"Runtime info:{ErrorReporting.RuntimeInfo()}");
                RegisterAppDomainExceptions();
                RegisterDispatcherUnhandledException();

                ImageLoader.Initialize();

                _settingsVM = new SettingWindowViewModel(_updater, _portable);
                _settings = _settingsVM.Settings;

                _alphabet.Initialize(_settings);
                _stringMatcher = new StringMatcher(_alphabet);
                StringMatcher.Instance = _stringMatcher;
                _stringMatcher.UserSettingSearchPrecision = _settings.QuerySearchPrecision;

                PluginManager.LoadPlugins(_settings.PluginSettings);
                _mainVM = new MainViewModel(_settings);
                var window = new MainWindow(_settings, _mainVM);
                API = new PublicAPIInstance(_settingsVM, _mainVM, _alphabet);
                PluginManager.InitializePlugins(API);
                Logger.WoxInfo($"Info:{ErrorReporting.DependenciesInfo()}");

                Current.MainWindow = window;
                Current.MainWindow.Title = Constant.Wox;

                // todo temp fix for instance code logic
                // load plugin before change language, because plugin language also needs be changed
                InternationalizationManager.Instance.Settings = _settings;
                InternationalizationManager.Instance.ChangeLanguage(_settings.Language);
                // main windows needs initialized before theme change because of blur settigns
                ThemeManager.Instance.Settings = _settings;
                ThemeManager.Instance.ChangeTheme(_settings.Theme);

                Http.Proxy = _settings.Proxy;

                RegisterExitEvents();

                AutoStartup();
                AutoUpdates();

                ParseCommandLineArgs(SingleInstance<App>.CommandLineArgs);
                _mainVM.MainWindowVisibility = _settings.HideOnStartup ? Visibility.Hidden : Visibility.Visible;
                Logger.WoxInfo("End Wox startup ----------------------------------------------------  ");
            });
        }


        private void AutoStartup()
        {
            if (_settings.StartWoxOnSystemStartup)
            {
                if (!SettingWindow.StartupSet())
                {
                    SettingWindow.SetStartup();
                }
            }
        }

        //[Conditional("RELEASE")]
        private void AutoUpdates()
        {
            Task.Run(async () =>
            {
                if (_settings.AutoUpdates)
                {
                    // check udpate every 5 hours
                    var timer = new System.Timers.Timer(1000 * 60 * 60 * 5);
                    timer.Elapsed += async (s, e) =>
                    {
                        await _updater.UpdateApp(true, _settings.UpdateToPrereleases);
                    };
                    timer.Start();

                    // check updates on startup
                    await _updater.UpdateApp(true, _settings.UpdateToPrereleases);
                }
            }).ContinueWith(ErrorReporting.UnhandledExceptionHandleTask, TaskContinuationOptions.OnlyOnFaulted);
        }

        private void RegisterExitEvents()
        {
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Dispose();
            Current.Exit += (s, e) => Dispose();
            Current.SessionEnding += (s, e) => Dispose();
        }

        /// <summary>
        /// let exception throw as normal is better for Debug
        /// </summary>
        [Conditional("RELEASE")]
        private void RegisterDispatcherUnhandledException()
        {
            DispatcherUnhandledException += ErrorReporting.DispatcherUnhandledException;
        }


        /// <summary>
        /// let exception throw as normal is better for Debug
        /// </summary>
        [Conditional("RELEASE")]
        private static void RegisterAppDomainExceptions()
        {
            AppDomain.CurrentDomain.UnhandledException += ErrorReporting.UnhandledExceptionHandleMain;
        }

        public void Dispose()
        {
            // if sessionending is called, exit proverbially be called when log off / shutdown
            // but if sessionending is not called, exit won't be called when log off / shutdown
            if (!_disposed)
            {
                API.SaveAppAllSettings();
                _disposed = true;
            }
        }

        public void OnSecondAppStarted(IList<string> args)
        {
            ParseCommandLineArgs(args);
            Current.MainWindow.Visibility = Visibility.Visible;
        }
    }
}