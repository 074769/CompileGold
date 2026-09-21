using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;

namespace CompilePalX
{
    /// <summary>A single "label (xx.x%)" line from a tool's BSP limits report (e.g. after HLVIS/HLRAD).</summary>
    public class LimitEntry
    {
        public string Label { get; }
        public double Percent { get; }

        public LimitEntry(string label, double percent)
        {
            Label = label;
            Percent = percent;
        }

        public string Display => $"{Label} ({Percent:0.0}%)";
        public string LabelUpper => Label.ToUpperInvariant();
        public string PercentText => $"{Percent:0.0}%";
    }

    /// <summary>
    /// One compile step's timing and pass/fail state, for the Status panel. Created (via
    /// TelemetryManager.Start) the moment a step begins, while it's still running - shown as a
    /// progress bar in that state - then updated in place (via TelemetryManager.Finish) once it
    /// completes, switching the UI over to the pass/fail circle and duration.
    /// </summary>
    public class ToolTelemetryEntry : INotifyPropertyChanged
    {
        public string ToolName { get; }

        private bool isRunning = true;
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

        public ToolTelemetryEntry(string toolName)
        {
            ToolName = toolName;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Tracks per-tool compile telemetry (time spent, pass/fail) for the Status panel, and the
    /// BSP limits report (Statistics panel) - the latter only from HLVIS/HLRAD, whichever ran
    /// most recently, since HLCSG/HLBSP's own -chart output reflects an earlier, less complete
    /// stage of the same map and would just be redundant/stale next to VIS/RAD's. Cleared at the
    /// start of each compile run and appended to as each compile step finishes.
    /// </summary>
    public static class TelemetryManager
    {
        public static ObservableCollection<ToolTelemetryEntry> Entries { get; } = [];
        public static ObservableCollection<LimitEntry> Statistics { get; } = [];

        private static readonly string[] StatisticsSourceTools = ["HLVIS", "HLRAD"];

        // Matches lines from the HLT tools' -chart limits report, e.g.:
        //   models             25/512         1600/32768    ( 4.9%)
        //   * worldfaces     9033/32768          0/0        (27.6%)
        //   texdata          [variable]   18639348/33554432 (55.5%)
        // Only the label and the trailing percentage are captured - the Objects/Maxobjs and
        // Memory/Maxmem columns in between are ignored (shown in "short form", % only).
        private static readonly Regex LimitLine = new(@"^\s*(\*\s*)?([A-Za-z][A-Za-z0-9_]*)\s+.*\(\s*([\d.]+)\s*%\)\s*$", RegexOptions.Multiline);

        public static void Clear()
        {
            MainWindow.ActiveDispatcher.Invoke(() =>
            {
                Entries.Clear();
                Statistics.Clear();
            });
        }

        /// <summary>Call as a compile step begins. Adds a running entry (shown as a progress bar) and returns it for Finish() to update.</summary>
        public static ToolTelemetryEntry Start(string toolName)
        {
            var entry = new ToolTelemetryEntry(toolName);
            MainWindow.ActiveDispatcher.Invoke(() => Entries.Add(entry));
            return entry;
        }

        /// <summary>Call once a compile step completes. Updates the same entry Start() returned in place, switching its UI from a progress bar to the pass/fail circle.</summary>
        public static void Finish(ToolTelemetryEntry entry, TimeSpan duration, bool passed, string rawOutput)
        {
            MainWindow.ActiveDispatcher.Invoke(() =>
            {
                entry.Duration = duration;
                entry.Passed = passed;
                entry.IsRunning = false;

                if (StatisticsSourceTools.Contains(entry.ToolName, StringComparer.OrdinalIgnoreCase))
                {
                    var limits = ParseLimits(rawOutput);
                    if (limits.Count > 0)
                    {
                        Statistics.Clear();
                        foreach (var limit in limits)
                            Statistics.Add(limit);
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
