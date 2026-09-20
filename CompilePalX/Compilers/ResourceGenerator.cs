using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using CompilePalX.Compiling;
using Error = CompilePalX.Error;

namespace CompilePalX.Compilers
{
    /// <summary>
    /// Scans the compiled .bsp for dependencies and writes a categorized .res file next to the
    /// bsp in the mod's maps folder. Does not zip or copy any dependency files - see
    /// ResourcePackager (PACK) for that. Nothing is packed into the bsp itself; GoldSrc doesn't
    /// support that the way Source's pak lump does.
    /// </summary>
    class ResourceGenerator : CompileProcess
    {
        public ResourceGenerator() : base("RESGEN") { }

        public override void Run(CompileContext context, CancellationToken cancellationToken)
        {
            CompileErrors = [];
            if (!CanRun(context)) return;
            if (cancellationToken.IsCancellationRequested) return;

            var activeParams = PresetDictionary.TryGetValue(ConfigurationManager.CurrentPreset, out var p) ? p : [];
            var extraFiles = activeParams.Where(i => i.Name == "Extra File" && !string.IsNullOrWhiteSpace(i.Value))
                                          .Select(i => i.Value.Trim());
            bool skipValveFallback = activeParams.Any(i => i.Name == "Skip Base Game Fallback");
            bool verbose = activeParams.Any(i => i.Name == "Verbose Scan Log");

            if (!File.Exists(context.BSPFile))
            {
                CompilePalLogger.LogLineColor("\nRESGEN: no compiled .bsp found at {0}, skipping.", Error.GetSeverityBrush(4), context.BSPFile);
                return;
            }

            CompilePalLogger.LogLine("\nCompileGold - Resource Generator", 900);

            ResourceScanResult result;
            try
            {
                result = ResourceScan.Scan(context, extraFiles, skipValveFallback, verbose);
            }
            catch (Exception e)
            {
                CompilePalLogger.LogLineColor("\nRESGEN: failed to parse {0}: {1}", Error.GetSeverityBrush(4), context.BSPFile, e.Message);
                return;
            }

            if (result.Missing.Count > 0)
            {
                CompilePalLogger.LogLineColor("RESGEN: {0} referenced file(s) could not be found in the mod or base game folder:", Error.GetSeverityBrush(2), result.Missing.Count);
                foreach (var m in result.Missing)
                    CompilePalLogger.LogLine("  missing: {0}", m);
            }

            string mapFolder = context.Configuration.MapFolder ?? Path.GetDirectoryName(context.BSPFile) ?? "";
            Directory.CreateDirectory(mapFolder);
            string resPath = Path.Combine(mapFolder, result.MapName + ".res");
            ResourceScan.WriteRes(result, resPath);

            int total = result.ResolvedByCategory.Sum(c => c.Paths.Count);
            CompilePalLogger.LogLineColor("RESGEN: wrote {0} with {1} dependencies.", SuccessBrush(), Path.GetFileName(resPath), total);
        }

        private static Brush SuccessBrush() =>
            (Brush)Application.Current.TryFindResource("CompilePal.Brushes.Success");
    }
}
