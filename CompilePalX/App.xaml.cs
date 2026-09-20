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
        private const string LightOverlayUri = "CompilePalTheme.Light.xaml";
        private const string DarkOverlayUri = "CompilePalTheme.Dark.xaml";

        /// <summary>
        /// Swaps both the base MahApps Light/Dark theme dictionary and CompileGold's own
        /// accent/grid-color overlay together, so app-specific colors (grid row shading,
        /// disabled checkboxes) get dark-appropriate values too instead of just inheriting
        /// whatever the base theme happens to leave unset.
        /// </summary>
        public static void ApplyTheme(bool dark)
        {
            IsDarkMode = dark;

            var mergedDicts = Application.Current.Resources.MergedDictionaries;

            var existingBase = mergedDicts.FirstOrDefault(d => d.Source != null &&
                (d.Source.OriginalString.IndexOf("light.red.xaml", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 d.Source.OriginalString.IndexOf("dark.red.xaml", StringComparison.OrdinalIgnoreCase) >= 0));
            var newBase = new ResourceDictionary { Source = new Uri(dark ? DarkThemeUri : LightThemeUri) };
            if (existingBase != null)
                mergedDicts[mergedDicts.IndexOf(existingBase)] = newBase;
            else
                mergedDicts.Insert(0, newBase);

            var existingOverlay = mergedDicts.FirstOrDefault(d => d.Source != null &&
                (d.Source.OriginalString.IndexOf("CompilePalTheme.Light.xaml", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 d.Source.OriginalString.IndexOf("CompilePalTheme.Dark.xaml", StringComparison.OrdinalIgnoreCase) >= 0));
            var newOverlay = new ResourceDictionary { Source = new Uri(dark ? DarkOverlayUri : LightOverlayUri, UriKind.Relative) };
            if (existingOverlay != null)
                mergedDicts[mergedDicts.IndexOf(existingOverlay)] = newOverlay;
            else
                mergedDicts.Add(newOverlay);
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
