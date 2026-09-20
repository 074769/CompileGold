using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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

    /// <summary>One compile step's timing, pass/fail state, and any BSP limits it reported.</summary>
    public class ToolTelemetryEntry : INotifyPropertyChanged
    {
        public string ToolName { get; }
        public TimeSpan Duration { get; }
        public bool Passed { get; }
        public ObservableCollection<LimitEntry> Limits { get; }

        public string DurationText => $"{Duration.TotalSeconds:0.00}s";

        public ToolTelemetryEntry(string toolName, TimeSpan duration, bool passed, IEnumerable<LimitEntry> limits)
        {
            ToolName = toolName;
            Duration = duration;
            Passed = passed;
            Limits = new ObservableCollection<LimitEntry>(limits);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// Tracks per-tool compile telemetry (time spent, pass/fail, BSP limits usage) for display
    /// in the side panel. Cleared at the start of each compile run and appended to as each
    /// compile step finishes.
    /// </summary>
    public static class TelemetryManager
    {
        public static ObservableCollection<ToolTelemetryEntry> Entries { get; } = [];

        // Matches lines like "models             ( 0.2%)" or "* worldfaces       ( 6.0%)"
        // printed by the HLT tools' BSP limits report (typically after HLVIS/HLRAD/HLBSP).
        private static readonly Regex LimitLine = new(@"^\s*(\*\s*)?([A-Za-z][A-Za-z0-9_]*)\s*\(\s*([\d.]+)\s*%\)\s*$", RegexOptions.Multiline);

        public static void Clear()
        {
            MainWindow.ActiveDispatcher.Invoke(() => Entries.Clear());
        }

        public static void Record(string toolName, TimeSpan duration, bool passed, string rawOutput)
        {
            var limits = ParseLimits(rawOutput);
            var entry = new ToolTelemetryEntry(toolName, duration, passed, limits);
            MainWindow.ActiveDispatcher.Invoke(() => Entries.Add(entry));
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
