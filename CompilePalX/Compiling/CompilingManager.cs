using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using CompilePalX.Compilers;
using CompilePalX.Compiling;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents.Serialization;
using CompilePalX.Annotations;
using CompilePalX.Configuration;
using Newtonsoft.Json;

namespace CompilePalX
{
    internal delegate void CompileCleared();
    internal delegate void CompileStarted();
    internal delegate void CompileFinished();

    public class Map : INotifyPropertyChanged
    {
        private string file;

        public string File
        {
            get => file;
            set { file = value; OnPropertyChanged(nameof(File));  }
        }

        public string FullMapName => Path.GetFileNameWithoutExtension(file);
        /// <summary>
        /// Map name without version identifiers
        /// </summary>
        // try removing version identifier
        public string MapName  => Regex.Replace(FullMapName, @"((_[^_]+\d)|(_rc)|(_final))$", "");

        public bool IsBSP => Path.GetExtension(file) == ".bsp";

        private bool compile;
        public bool Compile 
        {
            get => compile;
            set { compile = value; OnPropertyChanged(nameof(Compile));  }
        }

        private Preset? preset;
        public Preset? Preset
        {
            get => preset;
            set { preset = value; OnPropertyChanged(nameof(Preset));  }
        }

        public Map(string file, bool compile = true, Preset? preset = null)
        {
            File = file;
            Compile = compile;
            Preset = preset;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    static class CompilingManager
    {
        static CompilingManager()
        {
            CompilePalLogger.OnErrorFound += CompilePalLogger_OnErrorFound;
        }

        // True while the postCompile summary is re-logging errors. Those LogCompileError
        // calls raise OnErrorFound again - they must not trigger a nested CancelCompile.
        private static bool isWritingSummary;
        // Guards CancelCompile/postCompile so error-cancel, manual cancel and the
        // background thread can't run cleanup twice (double OnFinish / double summary).
        private static int cancelState; // 0 = running, 1 = cancelling/finished
        private static readonly object stateLock = new object();
            
        private static void CompilePalLogger_OnErrorFound(Error e)
        {
            currentCompileProcess?.CompileErrors?.Add(e);

            if (e.Severity >= (int)ErrorSeverity.Error && IsCompiling && !isWritingSummary)
            {
                // Kill the running tool right away so the compile actually stops even if
                // the background thread is blocked reading output. CancelCompile then
                // flips shared state and resets the button via OnFinish.
                try { currentCompileProcess?.Cancel(); } catch { }
                try { cts?.Cancel(); } catch { }
                // Run on the window thread (the caller is usually the background compile
                // thread) but don't block it: a blocking Invoke here can deadlock against
                // a UI thread that is itself waiting on the compile.
                MainWindow.ActiveDispatcher?.BeginInvoke(new Action(() =>
                {
                    CompilePalLogger.LogLineColor("An error cancelled the compile.", Error.GetSeverityBrush(5));
                    CancelCompile();
                    ProgressManager.ErrorProgress();
                }));
            }
        }

        public static event CompileCleared OnClear;
        public static event CompileFinished OnStart;
        public static event CompileFinished OnFinish;

        public static TrulyObservableCollection<Map> MapFiles = [];

        private static Stopwatch compileTimeStopwatch = new Stopwatch();

        public static bool IsCompiling { get; private set; }
        private static CancellationTokenSource cts;

        public static void ToggleCompileState()
        {
            if (IsCompiling)
                CancelCompile();
            else
                StartCompile();
        }

        public static void StartCompile()
        {
            lock (stateLock) { cancelState = 0; }
            isWritingSummary = false;

            OnStart?.Invoke();

            // Tells windows to not go to sleep during compile
            NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED);

            AnalyticsManager.Compile();

            IsCompiling = true;

            compileTimeStopwatch.Restart();

            OnClear?.Invoke();

            try { cts?.Dispose(); } catch { }
            cts = new CancellationTokenSource();
            Task.Run(() => CompileThreaded(cts.Token));
        }

        private static CompileProcess currentCompileProcess;

        private static void CompileThreaded(CancellationToken cancellationToken)
        {
            var mapErrors = new List<MapErrors>();

            try
            {
                ProgressManager.SetProgress(0);


                foreach (Map map in MapFiles)
                {
                    if (!map.Compile)
                    {
                        CompilePalLogger.LogDebug($"Skipping {map.File}");
                        continue;
                    }

                    string mapFile = map.File; 
                    string cleanMapName = Path.GetFileNameWithoutExtension(mapFile);
                    ConfigurationManager.CurrentPreset = map.Preset;

                    var compileErrors = new List<Error>();
                    CompilePalLogger.LogLine($"Starting a '{ConfigurationManager.CurrentPreset?.Name}' compile for {GameConfigurationManager.GameConfiguration.Name}.");
                    CompilePalLogger.LogLine($"Starting compilation of {cleanMapName}");
                    CompilePalLogger.LogLineDebug($"Map path: {mapFile}");

					//Update the grid so we have the most up to date order
	                OrderManager.UpdateOrder();

                    GameConfigurationManager.BackupCurrentContext();
                    var buildContext = GameConfigurationManager.BuildContext(map);

                    // .rmf sources are converted to a same-folder .map first (no external
                    // tool), then compiled as usual. .map inputs skip conversion entirely.
                    if (RmfToMap.IsRmfSource(buildContext.MapFile))
                    {
                        string convertedMap = Path.ChangeExtension(buildContext.MapFile, ".map");
                        var telemetryEntry = TelemetryManager.Start("Convert RMF");
                        int telemetryGeneration = telemetryEntry.Generation;
                        var convertStopwatch = Stopwatch.StartNew();
                        try
                        {
                            var convertResult = RmfToMap.Convert(buildContext.MapFile, convertedMap);
                            buildContext.MapFile = convertedMap;
                            buildContext.BSPFile = Path.ChangeExtension(convertedMap, ".bsp");
                            CompilePalLogger.LogLine($"Converted {Path.GetFileName(map.File)} to {Path.GetFileName(convertedMap)} ({convertResult.BrushCount} brushes, {convertResult.EntityCount} entities).");
                            foreach (var warning in convertResult.Warnings)
                                CompilePalLogger.LogLine($"RMF conversion warning: {warning}");
                            convertStopwatch.Stop();
                            TelemetryManager.Finish(telemetryEntry, telemetryGeneration, convertStopwatch.Elapsed, true, "");
                        }
                        catch (Exception convertException) when (convertException is not OperationCanceledException)
                        {
                            convertStopwatch.Stop();
                            TelemetryManager.Finish(telemetryEntry, telemetryGeneration, convertStopwatch.Elapsed, false, "");
                            var error = new Error($"RMF conversion failed: {convertException.Message}", ErrorSeverity.FatalError);
                            compileErrors.Add(error);
                            // plain colored line, not LogCompileError: that path notifies
                            // currentCompileProcess, which isn't set yet at conversion time
                            CompilePalLogger.LogLineColor($"RMF conversion failed: {convertException.Message}", Error.GetSeverityBrush(5));
                            mapErrors.Add(new MapErrors { MapName = cleanMapName, Errors = compileErrors });
                            GameConfigurationManager.RestoreCurrentContext();
                            continue;
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                    }
					foreach (var compileProcess in OrderManager.CurrentOrder)
					{
                        cancellationToken.ThrowIfCancellationRequested();
                        currentCompileProcess = compileProcess;

                        var telemetryEntry = TelemetryManager.Start(compileProcess.Name);
                        int telemetryGeneration = telemetryEntry.Generation;
                        var stepStopwatch = Stopwatch.StartNew();
                        try
                        {
                            compileProcess.Run(buildContext, cancellationToken);
                        }
                        catch
                        {
                            // cancelled (or crashed) mid-step: stop the step's timer and mark it failed instead of leaving it running
                            stepStopwatch.Stop();
                            TelemetryManager.Finish(telemetryEntry, telemetryGeneration, stepStopwatch.Elapsed, false, "");
                            throw;
                        }
                        stepStopwatch.Stop();

                        compileErrors.AddRange(currentCompileProcess.CompileErrors);

                        // a step that was cancelled counts as failed
                        bool stepPassed = !cancellationToken.IsCancellationRequested &&
                                          !currentCompileProcess.CompileErrors.Any(e => e.Severity >= (int)ErrorSeverity.Error);
                        string stepOutput = (compileProcess as CompileExecutable)?.RawOutput.ToString() ?? "";
                        TelemetryManager.Finish(telemetryEntry, telemetryGeneration, stepStopwatch.Elapsed, stepPassed, stepOutput);

                        // Run() can return quietly on cancel; don't carry on to "Compiled Map" / progress updates
                        cancellationToken.ThrowIfCancellationRequested();

                        //Portal 2 cannot work with leaks, stop compiling if we do get a leak.
                        if (GameConfigurationManager.GameConfiguration.Name == "Portal 2")
                        {
                            if (currentCompileProcess.Name == "VBSP" && currentCompileProcess.CompileErrors.Count > 0)
                            {
                                //we have a VBSP error, aka a leak -> stop compiling;
                                break;
                            }
                        }

                        ProgressManager.Progress += (1d / ConfigurationManager.CompileProcesses.Count(c => c.Metadata.DoRun &&
                            c.PresetDictionary.ContainsKey(ConfigurationManager.CurrentPreset))) / MapFiles.Count;

                        // log empty line to make a space inbetween compile step logs
                        CompilePalLogger.LogLine();
                    }

                    mapErrors.Add(new MapErrors { MapName = cleanMapName, Errors = compileErrors });

                    CompilePalLogger.LogLineFileLocation($"Compiled Map: {buildContext.CopyLocation}\n", buildContext.CopyLocation);
                    GameConfigurationManager.RestoreCurrentContext();
                }

                if (!cancellationToken.IsCancellationRequested && TryMarkFinished())
                    MainWindow.ActiveDispatcher?.Invoke(() => postCompile(mapErrors));
            }
            catch (OperationCanceledException)
            {
                // The run was cancelled (manually or by a fatal error): CancelCompile
                // already ran cleanup and reset the button. Just make sure the UI
                // reflects the cancelled state.
                if (TryMarkFinished())
                    // OnFinish touches WPF controls, so marshal to the UI thread.
                    MainWindow.ActiveDispatcher?.Invoke((Action)FinishCancelledState);
                ProgressManager.ErrorProgress();
            }
            catch (Exception ex)
            {
                // Any unexpected failure must still stop the run and reset the button
                // instead of leaving Compile/Cancel stuck.
                CompilePalLogger.LogLineDebug($"Compile thread failed: {ex}");
                try { cts?.Cancel(); } catch { }
                try { currentCompileProcess?.Cancel(); } catch { }
                if (TryMarkFinished())
                    MainWindow.ActiveDispatcher?.Invoke(() => postCompile(mapErrors));
            }
        }

        private static bool TryMarkFinished()
        {
            lock (stateLock)
            {
                if (cancelState != 0)
                    return false;
                cancelState = 1;
                return true;
            }
        }

        /// <summary>
        /// Resets shared state when the compile thread itself observes cancellation
        /// (CancelCompile ran on another thread or never ran). Never re-logs the
        /// collected errors - that would re-trigger OnErrorFound and recurse.
        /// </summary>
        private static void FinishCancelledState()
        {
            IsCompiling = false;
            try { compileTimeStopwatch.Reset(); } catch { }
            try { ProgressManager.SetProgress(0); } catch { }
            try { ProgressManager.ErrorProgress(); } catch { }
            try { NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS); } catch { }
            OnFinish?.Invoke();
        }

        private static void postCompile(List<MapErrors> errors)
        {
            // postCompile owns the single finish transition: the background thread only
            // calls it after winning TryMarkFinished, and CancelCompile (below) claims
            // the same guard, so the summary / OnFinish / button reset run exactly once.
            TryMarkFinished();
            isWritingSummary = true;
            try
            {
                string presetName = ConfigurationManager.CurrentPreset?.Name ?? "Unknown";
                CompilePalLogger.LogLineColor(
                    $"'{presetName}' compile finished in {compileTimeStopwatch.Elapsed.ToString(@"hh\:mm\:ss")}", (Brush)Application.Current.TryFindResource("CompilePal.Brushes.Success"));

                if (errors != null && errors.Any())
                {
                    int numErrors = errors.Sum(e => e.Errors.Count);
                    int maxSeverity = errors.Max(e => e.Errors.Any() ? e.Errors.Max(e2 => e2.Severity) : 0);
                    CompilePalLogger.LogLineColor("{0} errors/warnings logged:", Error.GetSeverityBrush(maxSeverity), numErrors);

                    foreach (var map in errors)
                    {
                        CompilePalLogger.Log("  ");

                        if (!map.Errors.Any())
                        {
                            CompilePalLogger.LogLineColor("No errors/warnings logged for {0}", Error.GetSeverityBrush(0), map.MapName);
                            continue;
                        }

                        int mapMaxSeverity = map.Errors.Max(e => e.Severity);
                        CompilePalLogger.LogLineColor("{0} errors/warnings logged for {1}:", Error.GetSeverityBrush(mapMaxSeverity), map.Errors.Count, map.MapName);

                        var distinctErrors = map.Errors.GroupBy(e => e.ID).OrderBy(e => e.First().Severity);
                        foreach (var errorList in distinctErrors)
                        {
                            var error = errorList.First();

                            string errorText = $"{errorList.Count()}x: {error.SeverityText}: {error.ShortDescription}";

                            CompilePalLogger.Log("    ● ");
                            CompilePalLogger.LogCompileError(errorText, error);
                            CompilePalLogger.LogLine();

                            if (error.Severity >= 3)
                                AnalyticsManager.CompileError();
                        }
                    }
                }
            }
            finally
            {
                isWritingSummary = false;
            }

            OnFinish?.Invoke();

            compileTimeStopwatch.Reset();

            IsCompiling = false;

            // Tells windows it's now okay to enter sleep
            NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);
        }

        public static void CancelCompile()
        {
            // Only the first caller performs cleanup so a manual click racing the
            // fatal-error auto-cancel can't double-run postCompile / OnFinish.
            if (!TryMarkFinished())
                return;

            try
            {
                try { currentCompileProcess?.Cancel(); } catch { }
                cts?.Cancel();
            }
            catch
            {
            }
            IsCompiling = false;

            ProgressManager.SetProgress(0);

            CompilePalLogger.LogLineColor("Compile forcefully ended.", (Brush) Application.Current.TryFindResource("CompilePal.Brushes.Severity4"));

            postCompile(null);
        }

        public static Stopwatch GetTime()
        {
            return compileTimeStopwatch;
        }

        class MapErrors
        {
            public string MapName { get; set; }
            public List<Error> Errors { get; set; }
        }

        internal static class NativeMethods
        {
            // Import SetThreadExecutionState Win32 API and necessary flags
            [DllImport("kernel32.dll")]
            public static extern uint SetThreadExecutionState(uint esFlags);
            public const uint ES_CONTINUOUS = 0x80000000;
            public const uint ES_SYSTEM_REQUIRED = 0x00000001;
        }
    }
}
