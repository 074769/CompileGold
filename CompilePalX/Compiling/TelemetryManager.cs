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
    }

    /// <summary>One compile step's timing and pass/fail state, for the Status panel.</summary>
    public class ToolTelemetryEntry : INotifyPropertyChanged
    {
        public string ToolName { get; }
        public TimeSpan Duration { get; }
        public bool Passed { get; }

        public string DurationText => $"{Duration.TotalSeconds:0.00}s";

        public ToolTelemetryEntry(string toolName, TimeSpan duration, bool passed)
        {
            ToolName = toolName;
            Duration = duration;
            Passed = passed;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
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

        public static void Record(string toolName, TimeSpan duration, bool passed, string rawOutput)
        {
            var entry = new ToolTelemetryEntry(toolName, duration, passed);

            MainWindow.ActiveDispatcher.Invoke(() =>
            {
                Entries.Add(entry);

                if (StatisticsSourceTools.Contains(toolName, StringComparer.OrdinalIgnoreCase))
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
                bool isStar = match.Groups[1].Success && match.Groups[1].Value.Trim() == "*";
                string label = match.Groups[2].Value;
                if (!double.TryParse(match.Groups[3].Value, out double percent))
                    continue;

                results.Add(new LimitEntry(isStar ? $"* {label}" : label, percent));
            }

            return results;
        }
    }
}
