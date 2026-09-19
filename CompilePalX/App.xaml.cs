using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CompilePalX.Compiling;
using Microsoft.Win32;

namespace CompilePalX
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public static bool IsDarkMode { get; private set; }

        private const string LightThemeUri = "pack://application:,,,/MahApps.Metro;component/Styles/themes/light.red.xaml";
        private const string DarkThemeUri = "pack://application:,,,/MahApps.Metro;component/Styles/themes/dark.red.xaml";

        /// <summary>
        /// Swaps the base MahApps Light/Dark theme dictionary. CompilePalTheme.xaml stays merged
        /// in afterwards untouched, so the gold accent overrides still apply on top of either.
        /// </summary>
        public static void ApplyTheme(bool dark)
        {
            IsDarkMode = dark;

            var mergedDicts = Application.Current.Resources.MergedDictionaries;
            var existing = mergedDicts.FirstOrDefault(d => d.Source != null &&
                (d.Source.OriginalString.IndexOf("light.red.xaml", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 d.Source.OriginalString.IndexOf("dark.red.xaml", StringComparison.OrdinalIgnoreCase) >= 0));

            var newDict = new ResourceDictionary { Source = new Uri(dark ? DarkThemeUri : LightThemeUri) };

            if (existing != null)
                mergedDicts[mergedDicts.IndexOf(existing)] = newDict;
            else
                mergedDicts.Insert(0, newDict);
        }

	    protected override void OnStartup(StartupEventArgs e)
	    {
		    // catch all unhandled exceptions and log them
		    AppDomain.CurrentDomain.UnhandledException += (s, err) => { ExceptionHandler.LogException((Exception)err.ExceptionObject, false); };
		    DispatcherUnhandledException += (s, err) => { ExceptionHandler.LogException(err.Exception, false); };
		    TaskScheduler.UnobservedTaskException += (s, err) => { ExceptionHandler.LogException(err.Exception, false); };

            // force invariant culture so stack traces are always in english
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

            // set working directory
            Directory.SetCurrentDirectory(Path.GetDirectoryName(AppContext.BaseDirectory));

            // store path in registry
            RegistryManager.Write("Path", AppContext.BaseDirectory);

            // apply saved dark mode preference before the first window shows
            bool savedDarkMode = RegistryManager.Read<string>("DarkMode") == "1";
            ApplyTheme(savedDarkMode);

            base.OnStartup(e);
        }
    }
}
