using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using CompilePalX.Compiling;
using Error = CompilePalX.Error;

namespace CompilePalX.Compilers
{
    /// <summary>
    /// Scans a compiled GoldSrc .bsp for external asset dependencies (models, sounds, sprites,
    /// sky textures, WADs) and packages them - together with the .bsp, a generated .res file,
    /// and the map's _detail.txt if present - into a single zip. Nothing is packed into the
    /// bsp itself; GoldSrc doesn't support that the way Source's pak lump does.
    ///
    /// This is heuristic, not a full FGD-aware parser: it scans every entity key/value pair for
    /// values that look like resource paths (known extensions), plus a couple of special cases
    /// (skyname, worldspawn wad list). It does not look inside .mdl files for their own embedded
    /// dependencies (external T-model texture files are handled as a special case; internally
    /// embedded studio textures need nothing extra). Use the "Extra File" option to add anything
    /// the scan misses.
    /// </summary>
    class ResourceGenerator : CompileProcess
    {
        public ResourceGenerator() : base("RESGEN") { }

        private static readonly string[] ResourceExtensions =
        {
            ".mdl", ".spr", ".wav", ".tga", ".bmp", ".wad", ".txt", ".scr", ".ogg", ".mp3", ".pic", ".fnt"
        };

        public override void Run(CompileContext context, CancellationToken cancellationToken)
        {
            CompileErrors = [];
            if (!CanRun(context)) return;
            if (cancellationToken.IsCancellationRequested) return;

            var activeParams = PresetDictionary.TryGetValue(ConfigurationManager.CurrentPreset, out var p) ? p : [];
            var extraFiles = activeParams.Where(i => i.Name == "Extra File" && !string.IsNullOrWhiteSpace(i.Value))
                                          .Select(i => i.Value!.Trim().Replace('\\', '/'))
                                          .ToList();
            bool skipValveFallback = activeParams.Any(i => i.Name == "Skip Base Game Fallback");
            bool verbose = activeParams.Any(i => i.Name == "Verbose Scan Log");

            string bspPath = context.BSPFile;
            if (!File.Exists(bspPath))
            {
                CompilePalLogger.LogLineColor("\nRESGEN: no compiled .bsp found at {0}, skipping.", Error.GetSeverityBrush(4), bspPath);
                return;
            }

            CompilePalLogger.LogLine("\nCompileGold - Resource Generator", 900);

            string mapName = Path.GetFileNameWithoutExtension(bspPath);
            string gameFolder = context.Configuration.GameFolder ?? "";
            string valveFallback = "";
            if (!skipValveFallback && !string.IsNullOrEmpty(gameFolder))
            {
                var parent = Directory.GetParent(gameFolder.TrimEnd('\\', '/'));
                if (parent != null)
                    valveFallback = Path.Combine(parent.FullName, "valve");
            }

            HashSet<string> discovered;
            List<string> wadNames;
            try
            {
                using var stream = File.OpenRead(bspPath);
                using var reader = new BinaryReader(stream);
                (discovered, wadNames) = ParseBsp(reader, verbose);
            }
            catch (Exception e)
            {
                CompilePalLogger.LogLineColor("\nRESGEN: failed to parse {0}: {1}", Error.GetSeverityBrush(4), bspPath, e.Message);
                return;
            }

            foreach (var wad in wadNames)
                discovered.Add(wad);

            foreach (var extra in extraFiles)
                discovered.Add(extra);

            // mapname_detail.txt (SDHLT detail prop config) - lives next to the source file
            string? detailTxtPath = null;
            string sourceDir = Path.GetDirectoryName(context.MapFile) ?? "";
            string candidateDetail = Path.Combine(sourceDir, mapName + "_detail.txt");
            if (File.Exists(candidateDetail))
            {
                detailTxtPath = candidateDetail;
                CompilePalLogger.LogLine("Found {0}, including it and scanning it for dependencies.", Path.GetFileName(candidateDetail));
                foreach (var line in File.ReadAllLines(candidateDetail))
                    foreach (var token in ExtractResourceTokens(line))
                        discovered.Add(token);
            }

            // resolve everything against the mod folder, then the base game folder
            var resolved = new List<string>();   // relative paths, forward-slash, confirmed to exist
            var missing = new List<string>();

            foreach (var relPath in discovered.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                string normalized = relPath.Replace('\\', '/').TrimStart('/');
                string? fullPath = ResolveExisting(normalized, gameFolder, valveFallback);

                if (fullPath != null)
                {
                    resolved.Add(normalized);
                    if (verbose)
                        CompilePalLogger.LogLine("  found: {0}", normalized);
                }
                else
                {
                    missing.Add(normalized);
                }
            }

            if (missing.Count > 0)
            {
                CompilePalLogger.LogLineColor("RESGEN: {0} referenced file(s) could not be found in the mod or base game folder:", Error.GetSeverityBrush(2), missing.Count);
                foreach (var m in missing)
                    CompilePalLogger.LogLine("  missing: {0}", m);
            }

            // write the .res file (relative, forward-slash paths; bsp itself listed first)
            string resRelative = $"maps/{mapName}.bsp";
            var resLines = new List<string> { resRelative };
            resLines.AddRange(resolved);

            string outputDir = sourceDir;
            string resPath = Path.Combine(outputDir, mapName + ".res");
            File.WriteAllLines(resPath, resLines, new UTF8Encoding(false));

            string zipPath = Path.Combine(outputDir, mapName + "_resources.zip");
            if (File.Exists(zipPath))
                File.Delete(zipPath);

            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(bspPath, resRelative);
                archive.CreateEntryFromFile(resPath, $"{mapName}.res");

                if (detailTxtPath != null)
                    archive.CreateEntryFromFile(detailTxtPath, Path.GetFileName(detailTxtPath));

                foreach (var relPath in resolved)
                {
                    string? fullPath = ResolveExisting(relPath, gameFolder, valveFallback);
                    if (fullPath != null)
                        archive.CreateEntryFromFile(fullPath, relPath);
                }
            }

            CompilePalLogger.LogLineColor("RESGEN: wrote {0} with {1} dependencies to {2}", SuccessBrush(), Path.GetFileName(resPath), resolved.Count, zipPath);
        }

        private static Brush SuccessBrush() =>
            (Brush)Application.Current.TryFindResource("CompilePal.Brushes.Success");

        private static string? ResolveExisting(string relativePath, string gameFolder, string valveFallback)
        {
            if (!string.IsNullOrEmpty(gameFolder))
            {
                string candidate = Path.Combine(gameFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                    return candidate;
            }

            if (!string.IsNullOrEmpty(valveFallback))
            {
                string candidate = Path.Combine(valveFallback, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        private static IEnumerable<string> ExtractResourceTokens(string text)
        {
            foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                string token = raw.Trim('"', '\'', ',', ';', '(', ')');
                foreach (var ext in ResourceExtensions)
                {
                    if (token.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    {
                        yield return token.Replace('\\', '/').TrimStart('/');
                        break;
                    }
                }
            }
        }

        // -- GoldSrc .bsp (BSPVERSION 30) parsing --------------------------------------------
        // Header: int32 version, then 15 lumps of (int32 offset, int32 length).
        // Lump 0 = entities (text), lump 2 = textures (miptex).

        private const int LumpEntities = 0;
        private const int LumpTextures = 2;
        private const int LumpCount = 15;

        private static (HashSet<string> discovered, List<string> wadNames) ParseBsp(BinaryReader reader, bool verbose)
        {
            var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var wadNames = new List<string>();

            int version = reader.ReadInt32();
            if (version != 30)
                CompilePalLogger.LogLine("RESGEN: unexpected BSP version {0} (expected 30 for GoldSrc) - continuing anyway.", version);

            var lumps = new (int offset, int length)[LumpCount];
            for (int i = 0; i < LumpCount; i++)
            {
                int offset = reader.ReadInt32();
                int length = reader.ReadInt32();
                lumps[i] = (offset, length);
            }

            // entities lump - plain text, sequences of { "key" "value" ... }
            reader.BaseStream.Seek(lumps[LumpEntities].offset, SeekOrigin.Begin);
            byte[] entityBytes = reader.ReadBytes(lumps[LumpEntities].length);
            string entityText = Encoding.ASCII.GetString(entityBytes);

            bool anyExternalTexture = false;
            string? wadKeyValue = null;

            foreach (var entity in ParseEntities(entityText))
            {
                if (entity.TryGetValue("classname", out var classname) && classname == "worldspawn")
                {
                    if (entity.TryGetValue("wad", out var wadValue))
                        wadKeyValue = wadValue;
                }

                foreach (var kv in entity)
                {
                    if (kv.Key == "skyname" && !string.IsNullOrWhiteSpace(kv.Value))
                    {
                        foreach (var suffix in new[] { "up", "dn", "lf", "rt", "ft", "bk" })
                            discovered.Add($"gfx/env/{kv.Value}{suffix}.tga");
                        continue;
                    }

                    if (verbose)
                        CompilePalLogger.LogLine("  entity kv: {0} = {1}", kv.Key, kv.Value);

                    foreach (var token in ExtractResourceTokens(kv.Value))
                        discovered.Add(token);
                }
            }

            // miptex lump - detect whether any texture lacks embedded pixel data (needs external wad)
            if (lumps[LumpTextures].length > 0)
            {
                reader.BaseStream.Seek(lumps[LumpTextures].offset, SeekOrigin.Begin);
                int numMipTex = reader.ReadInt32();
                var texOffsets = new int[numMipTex];
                for (int i = 0; i < numMipTex; i++)
                    texOffsets[i] = reader.ReadInt32();

                foreach (var texOffset in texOffsets)
                {
                    if (texOffset < 0)
                        continue;

                    reader.BaseStream.Seek(lumps[LumpTextures].offset + texOffset + 16 + 8, SeekOrigin.Begin); // skip name[16] + width/height
                    int firstMipOffset = reader.ReadInt32();
                    if (firstMipOffset == 0)
                    {
                        anyExternalTexture = true;
                        break;
                    }
                }
            }

            if (anyExternalTexture && !string.IsNullOrWhiteSpace(wadKeyValue))
            {
                foreach (var wadPath in wadKeyValue.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    string fileName = Path.GetFileName(wadPath.Trim());
                    if (!string.IsNullOrEmpty(fileName))
                        wadNames.Add(fileName);
                }
            }

            return (discovered, wadNames);
        }

        private static IEnumerable<Dictionary<string, string>> ParseEntities(string text)
        {
            int i = 0;
            int len = text.Length;

            while (i < len)
            {
                while (i < len && text[i] != '{') i++;
                if (i >= len) yield break;
                i++; // skip {

                var entity = new Dictionary<string, string>();

                while (i < len && text[i] != '}')
                {
                    while (i < len && char.IsWhiteSpace(text[i])) i++;
                    if (i >= len || text[i] == '}') break;

                    if (text[i] != '"') { i++; continue; }
                    string key = ReadQuoted(text, ref i);

                    while (i < len && char.IsWhiteSpace(text[i])) i++;
                    if (i >= len || text[i] != '"') continue;
                    string value = ReadQuoted(text, ref i);

                    if (!string.IsNullOrEmpty(key))
                        entity[key] = value;
                }

                if (i < len && text[i] == '}') i++;

                yield return entity;
            }
        }

        private static string ReadQuoted(string text, ref int i)
        {
            // assumes text[i] == '"'
            i++;
            int start = i;
            while (i < text.Length && text[i] != '"') i++;
            string result = text.Substring(start, i - start);
            if (i < text.Length) i++; // skip closing quote
            return result;
        }
    }
}
