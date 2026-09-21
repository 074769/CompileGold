using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using CompilePalX.Compiling;
using Newtonsoft.Json;

namespace CompilePalX
{
    static partial class ErrorFinder
    {
        private static List<Error> errorList = [];

        /// <summary>
        /// CompilePal originally downloaded its known-error database from interlopers.net, a
        /// Source Engine mapping site - its patterns are for VBSP/VVIS/VRAD error text and will
        /// never match HLCSG/HLBSP/HLVIS/HLRAD/hlfix output, so that fetch (and the network call
        /// it made on every launch) has no value for CompileGold. Errors/CompileErrors and the
        /// pass/fail state in the telemetry panel all depend on GetError() matching *something*,
        /// so this seeds a small local, hand-picked set of well-established HLT-family error and
        /// warning patterns instead. Best-effort: I can't run these tools to verify exact wording
        /// against real output, so this is scoped to conventions that have been stable across the
        /// whole ZHLT/HLT tool lineage for a long time, not an exhaustive list. If a real failure
        /// isn't getting flagged, the fix is adding its pattern here.
        /// </summary>
        public static void Init(bool refresh = false)
        {
            LoadBuiltInGoldSrcErrors();
        }

        private static void LoadBuiltInGoldSrcErrors()
        {
            errorList = [];
            int id = 0;

            void Add(string pattern, ErrorSeverity severity, string description)
            {
                errorList.Add(new Error(description, description, severity, id++)
                {
                    RegexTrigger = new Regex(pattern, RegexOptions.IgnoreCase)
                });
            }

            // fatal - compile did not produce a usable result
            Add(@"^Error:", ErrorSeverity.FatalError, "Fatal compile error");
            Add(@"MAX_MAP_\w+", ErrorSeverity.FatalError, "Exceeded a hardcoded engine limit");
            Add(@"^Command line error", ErrorSeverity.FatalError, "Invalid command line arguments");
            Add(@"Failed to run executable", ErrorSeverity.FatalError, "Could not launch the compile tool");
            Add(@"Unhandled Exception|Access Violation", ErrorSeverity.FatalError, "Tool crashed");
            Add(@"can'?t (open|find|load)\b.*\.wad", ErrorSeverity.FatalError, "Could not find a referenced WAD file");
            Add(@"input file can'?t be the same as output file", ErrorSeverity.FatalError, "hlfix input/output collision");

            // warning - compile likely completed, but something is worth a mapper's attention
            Add(@"^Warning:|>>> WARNING", ErrorSeverity.Warning, "Compile warning");
            Add(@"\bleak\b", ErrorSeverity.Warning, "Map has a leak");
            Add(@"\bfullbright\b", ErrorSeverity.Warning, "Surface with no lighting information");
        }

        public static Error? GetError(string line)
        {
            foreach (var error in errorList)
            {
                if (error.RegexTrigger.IsMatch(line))
                {
	                var err = error.Clone() as Error;
					// remove all control chars
	                err.ShortDescription = new string(line.Where(c => !char.IsControl(c)).ToArray());;
                    return err;
                }
            }
            return null;
        }

        public static void ShowErrorDialog(Error error)
        {
            ErrorWindow w = new ErrorWindow(error);
            w.ShowDialog();
        }
    }

    public class Error : ICloneable
    {
        public Regex RegexTrigger;
        public string Message;
        public string ShortDescription;
        public int Severity;

        [JsonIgnore]
        public int ID;

        public Error() { }

        public Error(string message, string shortDescription, ErrorSeverity severity, int id = -1)
        {
            Message = message;
            ShortDescription = shortDescription;
            Severity = (int) severity;
            ID = id;
        }
        public Error(string message, ErrorSeverity severity, int id = -1)
        {
            Message = message;
            ShortDescription = message;
            Severity = (int) severity;
            ID = id;
        }

        public override bool Equals(object obj)
        {
            if (obj is not Error) {
                return false;
            }
            return ((Error)obj).ID == ID;
        }

        public override int GetHashCode()
        {
            return ID;//ID is unique between errors
        }

        public object Clone()
        {
	        return MemberwiseClone();
        }

        [JsonIgnore]
        public Brush ErrorColor => GetSeverityBrush(Severity);

        public static Brush GetSeverityBrush(int severity)
        {
            return severity switch
            {
                2 => (Brush)Application.Current.TryFindResource("CompilePal.Brushes.Severity2"),
                3 => (Brush)Application.Current.TryFindResource("CompilePal.Brushes.Severity3"),
                4 => (Brush)Application.Current.TryFindResource("CompilePal.Brushes.Severity4"),
                5 => (Brush)Application.Current.TryFindResource("CompilePal.Brushes.Severity5"),
                _ => (Brush)Application.Current.TryFindResource("CompilePal.Brushes.Severity1"),
            };
        }

        public string SeverityText
        {
            get
            {
                return Severity switch
                {
                    2 => "Caution",
                    3 => "Warning",
                    4 => "Error",
                    5 => "Fatal Error",
                    _ => "Info",
                };
            }
        }
    }

    public enum ErrorSeverity {
        Info = 1,
        Caution = 2,
        Warning = 3,
        Error = 4,
        FatalError = 5,
    }
}
