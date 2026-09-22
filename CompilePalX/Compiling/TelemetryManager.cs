using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using CompilePalX.Configuration;

namespace CompilePalX
{
    /// <summary>
    /// A single "label (xx.x%)" line from the BSP limits report. Mutable (Percent can be
    /// updated in place) so the Statistics panel can keep showing every known label at all
    /// times, and just refresh the numbers each time HLVIS/HLRAD reports a new set, rather than
    /// clearing and rebuilding the whole list.
    /// </summary>
    public class LimitEntry : INotifyPropertyChanged
    {
        public string Label { get; }
        public string LabelUpper => Label.ToUpperInvariant();

        private double percent;
        public double Percent
        {
            get => percent;
            set { percent = value; OnPropertyChanged(nameof(Percent)); OnPropertyChanged(nameof(PercentText)); }
        }

        public string PercentText => $"{Percent:0.0}%";
        public string Display => $"{Label} ({Percent:0.0}%)";

        public LimitEntry(string label, double percent = 0)
        {
            Label = label;
            this.percent = percent;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// One compile step's timing and pass/fail state, for the Status panel. Persistent - stays
    /// in the list (and keeps its last result) across compiles, rather than being recreated each
    /// run. Defaults to "never run" (Passed = false, i.e. shown red) until it actually runs once.
    /// While running, Duration ticks up live off an internal timer instead of showing a
    /// progress bar, so DurationText doubles as both a live elapsed-time readout and, once
    /// finished, the final duration.
    /// </summary>
    public class ToolTelemetryEntry : INotifyPropertyChanged
    {
        public string ToolName { get; }

        private bool isRunning;
        public bool IsRunning
        {
            get => isRunning;
            set { isRunning = value; OnPropertyChanged(nameof(IsRunning)); }
        }

        private TimeSpan duration;
        public TimeSpan Duration
        {
            get => duration;
            set { duration = value; OnPropertyChanged(nameof(Duration)); OnPropertyChanged(nameof(DurationText)); }
        }

        private bool passed;
        public bool Passed
        {
            get => passed;
            set { passed = value; OnPropertyChanged(nameof(Passed)); }
        }

        public string DurationText => $"{Duration.TotalSeconds:0.00}s";

        private Stopwatch? liveStopwatch;
        private DispatcherTimer? liveTimer;

        public ToolTelemetryEntry(string toolName)
        {
            ToolName = toolName;
        }

        public void StartLiveTimer()
        {
            liveStopwatch = Stopwatch.StartNew();
            liveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
            liveTimer.Tick += (_, _) => Duration = liveStopwatch.Elapsed;
            liveTimer.Start();
        }

        public void StopLiveTimer()
        {
            liveTimer?.Stop();
            liveTimer = null;
            liveStopwatch = null;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Tracks per-tool compile telemetry for the Status panel, and the BSP limits report for the
    /// Statistics panel (the latter only from HLVIS/HLRAD - see ResourceScan-adjacent notes
    /// elsewhere for why). Both panels are persistent: every tool that's part of the current
    /// compile order gets a permanent "never run" (red) entry as soon as that order is known -
    /// including before the very first compile - and every known limits label is pre-seeded at
    /// 0%. Nothing is ever cleared between compiles; entries are updated in place and keep
    /// their last result indefinitely (until the app restarts).
    /// </summary>
    public static class TelemetryManager
    {
        public static ObservableCollection<ToolTelemetryEntry> Entries { get; } = [];
        public static ObservableCollection<LimitEntry> Statistics { get; } = CreateInitialStatistics();

        private static readonly string[] StatisticsSourceTools = ["HLVIS", "HLRAD"];

        // The BSP limits report's known labels (from the -chart output), pre-seeded at 0% so
        // Statistics has something to show before any compile has run.
        private static ObservableCollection<LimitEntry> CreateInitialStatistics()
        {
            string[] labels =
            [
                "models", "planes", "vertexes", "nodes", "texinfos", "faces", "worldfaces",
                "clipnodes", "leaves", "worldleaves", "marksurfaces", "surfedges", "edges",
                "texdata", "lightdata", "visdata", "entdata", "AllocBlock"
            ];
            return new ObservableCollection<LimitEntry>(labels.Select(l => new LimitEntry(l)));
        }

        // Matches lines from the HLT tools' -chart limits report, e.g.:
        //   models             25/512         1600/32768    ( 4.9%)
        //   * worldfaces     9033/32768          0/0        (27.6%)
        //   texdata          [variable]   18639348/33554432 (55.5%)
        // Only the label and the trailing percentage are captured - the Objects/Maxobjs and
        // Memory/Maxmem columns in between are ignored (shown in "short form", % only).
        private static readonly Regex LimitLine = new(@"^\s*(\*\s*)?([A-Za-z][A-Za-z0-9_]*)\s+.*\(\s*([\d.]+)\s*%\)\s*$", RegexOptions.Multiline);

        private static bool initialized;

        /// <summary>
        /// Wires Status up to the current compile order, so every tool that's part of it gets a
        /// permanent entry (defaulting to "never run"/red) as soon as the order is known, rather
        /// than only appearing once it first runs. Call once at startup; safe to call more than
        /// once. Existing entries and their results are never touched by this.
        /// </summary>
        public static void Init()
        {
            if (initialized)
                return;
            initialized = true;

            OrderManager.CurrentOrder.CollectionChanged += (_, _) => SyncWithOrder();
            SyncWithOrder();
        }

        private static void SyncWithOrder()
        {
            var names = OrderManager.CurrentOrder.Select(p => p.Name).ToList();
            MainWindow.ActiveDispatcher.Invoke(() =>
            {
                foreach (var name in names)
                {
                    if (!Entries.Any(e => e.ToolName == name))
                        Entries.Add(new ToolTelemetryEntry(name));
                }
            });
        }

        /// <summary>Call as a compile step begins. Reuses that tool's existing entry if it has one (keeping it in place, just marking it running again) rather than creating a new one.</summary>
        public static ToolTelemetryEntry Start(string toolName)
        {
            return MainWindow.ActiveDispatcher.Invoke(() =>
            {
                var entry = Entries.FirstOrDefault(e => e.ToolName == toolName);
                if (entry == null)
                {
                    entry = new ToolTelemetryEntry(toolName);
                    Entries.Add(entry);
                }

                entry.IsRunning = true;
                entry.StartLiveTimer();
                return entry;
            });
        }

        /// <summary>Call once a compile step completes. Updates the same entry Start() returned in place - the live timer stops and its final value is kept as the shown duration.</summary>
        public static void Finish(ToolTelemetryEntry entry, TimeSpan duration, bool passed, string rawOutput)
        {
            MainWindow.ActiveDispatcher.Invoke(() =>
            {
                entry.StopLiveTimer();
                entry.Duration = duration;
                entry.Passed = passed;
                entry.IsRunning = false;

                if (StatisticsSourceTools.Contains(entry.ToolName, StringComparer.OrdinalIgnoreCase))
                {
                    foreach (var parsed in ParseLimits(rawOutput))
                    {
                        var existing = Statistics.FirstOrDefault(s => string.Equals(s.Label, parsed.Label, StringComparison.OrdinalIgnoreCase));
                        if (existing != null)
                            existing.Percent = parsed.Percent;
                        else
                            Statistics.Add(parsed); // an unexpected label the pre-seeded list didn't anticipate - still show it
                    }
                }
            });
        }

        public static List<LimitEntry> ParseLimits(string? rawOutput)
        {
            var results = new List<LimitEntry>();
            if (string.IsNullOrEmpty(rawOutput))
                return results;

            foreach (Match match in LimitLine.Matches(rawOutput))
            {
                // the leading "*" (sub-entries like "worldfaces" under "faces") is just
                // captured to keep the regex accurate - dropped here for a clean label
                string label = match.Groups[2].Value;
                if (!double.TryParse(match.Groups[3].Value, out double percent))
                    continue;

                results.Add(new LimitEntry(label, percent));
            }

            return results;
        }
    }
}
