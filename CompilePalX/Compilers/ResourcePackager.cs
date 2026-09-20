using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using CompilePalX.Compiling;
using Error = CompilePalX.Error;

namespace CompilePalX.Compilers
{
    /// <summary>
    /// Same dependency scan as RESGEN, but also bundles everything into a zip (named to match
    /// the bsp) alongside the .res - the bsp itself, the .res, mapname_detail.txt if present,
    /// and every resolved dependency at its correct relative path. Nothing is packed into the
    /// bsp itself; GoldSrc doesn't support that the way Source's pak lump does.
    /// </summary>
    class ResourcePackager : CompileProcess
    {
        public ResourcePackager() : base("PACK") { }

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
                CompilePalLogger.LogLineColor("\nPACK: no compiled .bsp found at {0}, skipping.", Error.GetSeverityBrush(4), context.BSPFile);
                return;
            }

            CompilePalLogger.LogLine("\nCompileGold - Pack", 900);

            ResourceScanResult result;
            try
            {
                result = ResourceScan.Scan(context, extraFiles, skipValveFallback, verbose);
            }
            catch (Exception e)
            {
                CompilePalLogger.LogLineColor("\nPACK: failed to parse {0}: {1}", Error.GetSeverityBrush(4), context.BSPFile, e.Message);
                return;
            }

            if (result.Missing.Count > 0)
            {
                CompilePalLogger.LogLineColor("PACK: {0} referenced file(s) could not be found in the mod or base game folder:", Error.GetSeverityBrush(2), result.Missing.Count);
                foreach (var m in result.Missing)
                    CompilePalLogger.LogLine("  missing: {0}", m);
            }

            string mapFolder = context.Configuration.MapFolder ?? Path.GetDirectoryName(context.BSPFile) ?? "";
            Directory.CreateDirectory(mapFolder);

            string resPath = Path.Combine(mapFolder, result.MapName + ".res");
            ResourceScan.WriteRes(result, resPath);

            string zipPath = Path.Combine(mapFolder, result.MapName + ".zip");
            if (File.Exists(zipPath))
                File.Delete(zipPath);

            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(result.BspPath, $"maps/{result.MapName}.bsp");
                archive.CreateEntryFromFile(resPath, $"{result.MapName}.res");

                if (result.DetailTxtPath != null)
                    archive.CreateEntryFromFile(result.DetailTxtPath, Path.GetFileName(result.DetailTxtPath));

                foreach (var (_, paths) in result.ResolvedByCategory)
                {
                    foreach (var relPath in paths)
                    {
                        string? fullPath = ResourceScan.ResolveExisting(relPath, result.GameFolder, result.ValveFallback);
                        if (fullPath != null)
                            archive.CreateEntryFromFile(fullPath, relPath);
                    }
                }
            }

            int total = result.ResolvedByCategory.Sum(c => c.Paths.Count);
            CompilePalLogger.LogLineColor("PACK: wrote {0} and {1} with {2} dependencies.", SuccessBrush(), Path.GetFileName(resPath), Path.GetFileName(zipPath), total);
        }

        private static Brush SuccessBrush() =>
            (Brush)Application.Current.TryFindResource("CompilePal.Brushes.Success");
    }
}
