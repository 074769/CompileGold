using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CompilePalX.Compiling;

namespace CompilePalX.Compilers
{
    /// <summary>
    /// The result of scanning a compiled GoldSrc .bsp for external asset dependencies.
    /// Shared by RESGEN (.res only) and PACK (.res + zip), so both see identical results.
    /// </summary>
    class ResourceScanResult
    {
        public required string MapName;
        public required string BspPath;
        public required string GameFolder;
        public required string ValveFallback;
        public string? DetailTxtPath;

        /// <summary>Relative (forward-slash) paths that were found on disk, grouped by category, in display order.</summary>
        public required List<(string Category, List<string> Paths)> ResolvedByCategory;

        /// <summary>Relative paths that were referenced but couldn't be found anywhere.</summary>
        public required List<string> Missing;
    }

    /// <summary>
    /// Parses a GoldSrc .bsp (BSPVERSION 30) for external asset dependencies and resolves them
    /// against the mod/base game folders. Heuristic, not a full FGD-aware parser: scans every
    /// entity key/value pair for values that look like resource paths (known extensions), plus
    /// special cases for skyname and worldspawn's wad list. Does not look inside .mdl files for
    /// their own embedded dependencies.
    /// </summary>
    static class ResourceScan
    {
        private static readonly string[] ResourceExtensions =
        {
            ".mdl", ".spr", ".wav", ".tga", ".bmp", ".wad", ".txt", ".scr", ".ogg", ".mp3", ".pic", ".fnt"
        };

        // The standard player model ships with every GoldSrc install - never worth bundling.
        private static bool IsPlayerModel(string relPath) =>
            relPath.Equals("models/player.mdl", StringComparison.OrdinalIgnoreCase) ||
            relPath.StartsWith("models/player/", StringComparison.OrdinalIgnoreCase);

        public static ResourceScanResult Scan(CompileContext context, IEnumerable<string> extraFiles, bool skipValveFallback, bool verbose)
        {
            string bspPath = context.BSPFile;
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
            using (var stream = File.OpenRead(bspPath))
            using (var reader = new BinaryReader(stream))
            {
                (discovered, wadNames) = ParseBsp(reader, verbose);
            }

            foreach (var wad in wadNames)
                discovered.Add(wad);

            foreach (var extra in extraFiles)
                if (!string.IsNullOrWhiteSpace(extra))
                    discovered.Add(extra.Trim().Replace('\\', '/'));

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

            var resolved = new List<string>();
            var missing = new List<string>();

            foreach (var relPath in discovered.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                string normalized = relPath.Replace('\\', '/').TrimStart('/');

                if (IsPlayerModel(normalized))
                {
                    if (verbose)
                        CompilePalLogger.LogLine("  skipping player model: {0}", normalized);
                    continue;
                }

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

            var byCategory = new List<(string Category, List<string> Paths)>
            {
                ("Models", []), ("Sound", []), ("Sprites", []), ("GFX", []), ("WADs", []), ("Other", [])
            };
            foreach (var path in resolved)
            {
                var bucket = byCategory.First(c => c.Category == Categorize(path));
                bucket.Paths.Add(path);
            }

            return new ResourceScanResult
            {
                MapName = mapName,
                BspPath = bspPath,
                GameFolder = gameFolder,
                ValveFallback = valveFallback,
                DetailTxtPath = detailTxtPath,
                ResolvedByCategory = byCategory,
                Missing = missing
            };
        }

        /// <summary>Writes the .res file, grouped into "// Category" sections. Does not list the bsp
        /// itself - it's downloaded through the normal map-change mechanism, listing it in the .res
        /// too would have the server tell clients to download the very map they're already loading.</summary>
        public static void WriteRes(ResourceScanResult result, string resPath)
        {
            var lines = new List<string>();
            foreach (var (category, paths) in result.ResolvedByCategory)
            {
                if (paths.Count == 0)
                    continue;

                if (lines.Count > 0)
                    lines.Add("");
                lines.Add($"// {category}");
                lines.AddRange(paths);
            }

            File.WriteAllLines(resPath, lines, new UTF8Encoding(false));
        }

        public static string? ResolveExisting(string relativePath, string gameFolder, string valveFallback)
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

        private static string Categorize(string relPath)
        {
            string lower = relPath.ToLowerInvariant();
            string ext = Path.GetExtension(lower);

            if (lower.StartsWith("models/") || ext == ".mdl") return "Models";
            if (lower.StartsWith("sound/") || ext == ".wav" || ext == ".mp3" || ext == ".ogg") return "Sound";
            if (lower.StartsWith("sprites/") || ext == ".spr") return "Sprites";
            if (lower.StartsWith("gfx/")) return "GFX";
            if (ext == ".wad") return "WADs";
            return "Other";
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
