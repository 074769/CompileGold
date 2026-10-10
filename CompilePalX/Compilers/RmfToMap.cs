using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CompilePalX.Compilers
{
    /// <summary>
    /// Clean-room .rmf (Rich Map Format, the Hammer 3.x/J.A.C.K. native binary format) to
    /// Valve220 .map exporter, written from the format's documented structure only -
    /// no third-party conversion code is used or referenced.
    ///
    /// Used automatically at compile time: selecting a .rmf converts it to a .map of the same
    /// name in the same folder, which the rest of the pipeline then compiles as usual.
    /// Selecting a .map skips conversion entirely.
    ///
    /// Fidelity notes / assumptions:
    /// - Editor-only data (visgroups, cameras/DOCINFO) has no .map representation and is not
    ///   exported. All objects are exported regardless of visibility, like the own editor .map export.
    /// - Groups are transparent: contents are flattened into the enclosing scope, the same way
    ///   the .map export treats them.
    /// - Face plane triples are passed through verbatim (RMF stores the same winding the
    ///   editor itself exports), so no winding guesses are made here.
    /// - No geometry repair is performed (no tessellation of non-planar faces, no convex
    ///   decomposition). If the compile tools reject the converted geometry, export a .map
    ///   from the editor directly and compile that instead.
    /// </summary>
    static class RmfToMap
    {
        public sealed class ConvertResult
        {
            public string MapPath = "";
            public int BrushCount;
            public int EntityCount;
            public int FaceCount;
            public List<string> Warnings = new List<string>();
            internal int DroppedWarnings;
        }

        public static bool IsRmfSource(string path)
        {
            return string.Equals(Path.GetExtension(path ?? ""), ".rmf", StringComparison.OrdinalIgnoreCase);
        }

        public static ConvertResult Convert(string rmfPath, string mapPath)
        {
            if (mapPath == null)
                mapPath = Path.ChangeExtension(rmfPath, ".map");

            RmfDoc doc;
            try
            {
                doc = ReadRmf(rmfPath);
            }
            catch (EndOfStreamException)
            {
                throw new RmfException("File is truncated or is not a valid .rmf file.");
            }

            var result = new ConvertResult();
            result.MapPath = mapPath;
            string text = WriteMap(doc, result);

            if (result.DroppedWarnings > 0)
                result.Warnings.Add("... and " + result.DroppedWarnings + " further warnings suppressed.");

            string tmpPath = mapPath + ".tmp";
            File.WriteAllBytes(tmpPath, Encoding.Latin1.GetBytes(text));
            File.Move(tmpPath, mapPath, true);

            return result;
        }

        public sealed class RmfException : Exception
        {
            public RmfException(string message) : base(message) { }
        }

        private sealed class RmfFace
        {
            public string Texture = "";
            public float[] U = new float[3];
            public float XShift;
            public float[] V = new float[3];
            public float YShift;
            public float Rotation;
            public float XScale = 1;
            public float YScale = 1;
            public float[][] Plane = new float[][] { new float[3], new float[3], new float[3] };
        }

        private sealed class RmfSolid
        {
            public List<RmfFace> Faces = new List<RmfFace>();
        }

        private sealed class RmfEntity
        {
            public string ClassName = "";
            public List<KeyValuePair<string, string>> KeyValues = new List<KeyValuePair<string, string>>();
            public List<RmfSolid> Brushes = new List<RmfSolid>();
            public float[] Origin = new float[3];
        }

        private sealed class RmfObject
        {
            public RmfSolid? Solid;
            public RmfEntity? Entity;
            public List<RmfObject> Group = new List<RmfObject>();
            public bool IsGroup;
        }

        private sealed class RmfCorner
        {
            public float[] Pos = new float[3];
            public int Index;
            public string NameOverride = "";
            public List<KeyValuePair<string, string>> KeyValues = new List<KeyValuePair<string, string>>();
        }

        private sealed class RmfPath
        {
            public string Name = "";
            public string Class = "";
            public int Type;
            public List<RmfCorner> Corners = new List<RmfCorner>();
        }

        private sealed class RmfDoc
        {
            public List<RmfObject> WorldObjects = new List<RmfObject>();
            public string WorldClassName = "worldspawn";
            public List<KeyValuePair<string, string>> WorldspawnKeyValues = new List<KeyValuePair<string, string>>();
            public List<RmfPath> Paths = new List<RmfPath>();
        }
        // ---------------- reading ----------------

        private static RmfDoc ReadRmf(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var br = new BinaryReader(stream, Encoding.Latin1))
            {
                // header is a float version immediately followed by the 3-byte RMF tag (no padding)
                float version = br.ReadSingle();
                if (version < 0.8f || version > 4.0f)
                    throw new RmfException("Bad RMF version - not a valid .rmf file.");

                byte[] magic = ReadBytes(br, 3);
                if (magic[0] != 82 || magic[1] != 77 || magic[2] != 70)
                    throw new RmfException("Missing RMF header - not a valid .rmf file.");

                var doc = new RmfDoc();

                int visGroupCount = ReadCount(br, "visgroup", 100000);
                br.BaseStream.Seek((long)visGroupCount * 140L, SeekOrigin.Current);

                if (ReadNString(br) != "CMapWorld")
                    throw new RmfException("Missing CMapWorld block - not a valid .rmf file.");
                br.BaseStream.Seek(7, SeekOrigin.Current);

                int objectCount = ReadCount(br, "world object", 1000000);
                for (int i = 0; i < objectCount; i++)
                    doc.WorldObjects.Add(ReadObject(br));

                doc.WorldClassName = ReadNString(br);
                if (string.IsNullOrWhiteSpace(doc.WorldClassName))
                    doc.WorldClassName = "worldspawn";
                br.BaseStream.Seek(4, SeekOrigin.Current); // unused
                br.ReadInt32(); // worldspawn flags
                doc.WorldspawnKeyValues = ReadKeyValues(br);
                br.BaseStream.Seek(12, SeekOrigin.Current);

                int pathCount = ReadCount(br, "path", 100000);
                for (int i = 0; i < pathCount; i++)
                    doc.Paths.Add(ReadPath(br));

                string docInfo = ReadFixedString(br, 8);
                if (!docInfo.StartsWith("DOCINFO"))
                    throw new RmfException("Missing DOCINFO section - not a valid .rmf file.");
                br.ReadSingle();
                br.ReadInt32();
                int cameraCount = ReadCount(br, "camera", 100000);
                br.BaseStream.Seek((long)cameraCount * 24L, SeekOrigin.Current);

                return doc;
            }
        }

        private static RmfObject ReadObject(BinaryReader br)
        {
            string tag = ReadNString(br);
            var obj = new RmfObject();

            if (tag == "CMapSolid")
            {
                obj.Solid = ReadSolid(br);
            }
            else if (tag == "CMapEntity")
            {
                obj.Entity = ReadEntity(br);
            }
            else if (tag == "CMapGroup")
            {
                obj.IsGroup = true;
                br.ReadInt32();
                br.BaseStream.Seek(3, SeekOrigin.Current);
                int childCount = ReadCount(br, "group child", 1000000);
                for (int i = 0; i < childCount; i++)
                    obj.Group.Add(ReadObject(br));
            }
            else
            {
                throw new RmfException("Unknown object type - unsupported or corrupt .rmf file.");
            }

            return obj;
        }

        private static RmfSolid ReadSolid(BinaryReader br)
        {
            var solid = new RmfSolid();
            br.ReadInt32();
            br.BaseStream.Seek(7, SeekOrigin.Current);

            int faceCount = ReadCount(br, "face", 100000);
            for (int i = 0; i < faceCount; i++)
                solid.Faces.Add(ReadFace(br));

            return solid;
        }

        private static RmfFace ReadFace(BinaryReader br)
        {
            var face = new RmfFace();
            face.Texture = ReadFixedString(br, 256);
            br.ReadSingle();
            face.U = ReadVector(br);
            face.XShift = br.ReadSingle();
            face.V = ReadVector(br);
            face.YShift = br.ReadSingle();
            face.Rotation = br.ReadSingle();
            face.XScale = br.ReadSingle();
            face.YScale = br.ReadSingle();
            br.BaseStream.Seek(16, SeekOrigin.Current);

            int vertCount = ReadCount(br, "face vertex", 100000);
            for (int i = 0; i < vertCount; i++)
                ReadVector(br);

            face.Plane[0] = ReadVector(br);
            face.Plane[1] = ReadVector(br);
            face.Plane[2] = ReadVector(br);

            return face;
        }

        private static RmfEntity ReadEntity(BinaryReader br)
        {
            var entity = new RmfEntity();
            br.ReadInt32();
            br.BaseStream.Seek(3, SeekOrigin.Current);

            int brushCount = ReadCount(br, "entity brush", 1000000);
            for (int i = 0; i < brushCount; i++)
            {
                // entity brushes are full solids, tag included
                if (ReadNString(br) != "CMapSolid")
                    throw new RmfException("Missing CMapSolid block - file does not look like a .rmf file.");
                entity.Brushes.Add(ReadSolid(br));
            }

            entity.ClassName = ReadNString(br);
            br.BaseStream.Seek(4, SeekOrigin.Current);
            br.ReadInt32();
            entity.KeyValues = ReadKeyValues(br);
            br.BaseStream.Seek(14, SeekOrigin.Current);
            entity.Origin = ReadVector(br);
            br.BaseStream.Seek(4, SeekOrigin.Current);

            return entity;
        }

        private static RmfPath ReadPath(BinaryReader br)
        {
            var path = new RmfPath();
            path.Name = ReadFixedString(br, 128).Trim();
            path.Class = ReadFixedString(br, 128).Trim();
            path.Type = br.ReadInt32();

            int cornerCount = ReadCount(br, "path corner", 100000);
            for (int i = 0; i < cornerCount; i++)
            {
                var corner = new RmfCorner();
                corner.Pos = ReadVector(br);
                corner.Index = br.ReadInt32();
                corner.NameOverride = ReadFixedString(br, 128).Trim();
                corner.KeyValues = ReadKeyValues(br);
                path.Corners.Add(corner);
            }

            return path;
        }

        private static List<KeyValuePair<string, string>> ReadKeyValues(BinaryReader br)
        {
            int pairCount = ReadCount(br, "key/value pair", 1000000);
            var pairs = new List<KeyValuePair<string, string>>(pairCount);
            for (int i = 0; i < pairCount; i++)
            {
                string key = ReadNString(br);
                string value = ReadNString(br);
                pairs.Add(new KeyValuePair<string, string>(key, value));
            }
            return pairs;
        }

        private static float[] ReadVector(BinaryReader br)
        {
            return new float[] { br.ReadSingle(), br.ReadSingle(), br.ReadSingle() };
        }

        private static int ReadCount(BinaryReader br, string what, int max)
        {
            int count = br.ReadInt32();
            if (count < 0 || count > max)
                throw new RmfException("Invalid " + what + " count - file is corrupt or unsupported.");
            return count;
        }

        private static byte[] ReadBytes(BinaryReader br, int count)
        {
            byte[] bytes = br.ReadBytes(count);
            if (bytes.Length != count)
                throw new RmfException("File is truncated or is not a valid .rmf file.");
            return bytes;
        }

        private static string ReadFixedString(BinaryReader br, int length)
        {
            byte[] bytes = ReadBytes(br, length);
            int end = Array.IndexOf(bytes, (byte)0);
            if (end < 0)
                end = bytes.Length;
            return Encoding.Latin1.GetString(bytes, 0, end);
        }

        private static string ReadNString(BinaryReader br)
        {
            int length = br.ReadByte();
            if (length == 0)
                return "";
            byte[] bytes = ReadBytes(br, length);
            int charCount = bytes.Length;
            if (charCount > 0 && bytes[charCount - 1] == 0)
                charCount--;
            return Encoding.Latin1.GetString(bytes, 0, charCount);
        }
        // ---------------- writing ----------------

        private const int MaxWarnings = 25;

        private static void Warn(ConvertResult result, string message)
        {
            if (result.Warnings.Count < MaxWarnings)
                result.Warnings.Add(message);
            else
                result.DroppedWarnings++;
        }

        private static string WriteMap(RmfDoc doc, ConvertResult result)
        {
            var sb = new StringBuilder(1048576);

            var worldBrushes = new List<RmfSolid>();
            var entities = new List<RmfEntity>();
            foreach (var obj in doc.WorldObjects)
                Flatten(obj, worldBrushes, entities);

            sb.AppendLine("{");
            sb.AppendLine("\"classname\" \"" + Escape(doc.WorldClassName) + "\"");
            // Every face below is written in Valve220 syntax ("[ ux uy uz shift ] [ vx vy vz shift ]").
            // The HLT compilers only expect that syntax when worldspawn declares "mapversion" "220";
            // without it the faces are read as the classic Quake format and the parse fails with
            // "Parsing Entity 0, Brush 0, Side N : Expecting '(' got ']'". The .rmf never stores this key.
            sb.AppendLine("\"mapversion\" \"220\"");
            bool hasWadKey = false;
            foreach (var kv in doc.WorldspawnKeyValues)
            {
                if (string.Equals(kv.Key, "classname", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(kv.Key, "mapversion", StringComparison.OrdinalIgnoreCase))
                    continue; // already written above, and it must match the face syntax we emit
                if (string.Equals(kv.Key, "wad", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value))
                    hasWadKey = true;
                sb.AppendLine("\"" + Escape(kv.Key) + "\" \"" + Escape(kv.Value) + "\"");
            }
            if (!hasWadKey)
                Warn(result, "Worldspawn has no \"wad\" key (the .rmf does not store one) - HLCSG will only look for textures in its own WAD configuration; any texture outside it fails with a LoadLump error. Either add the WADs to HLCSG's config or add a \"wad\" key to worldspawn in the editor before saving the .rmf.");
            foreach (var solid in worldBrushes)
                WriteBrush(sb, solid, result);
            sb.AppendLine("}");

            foreach (var entity in entities)
                WriteEntity(sb, entity, result);

            WritePaths(sb, doc, result);

            return sb.ToString();
        }

        private static void Flatten(RmfObject obj, List<RmfSolid> brushes, List<RmfEntity> entities)
        {
            if (obj.Solid != null)
                brushes.Add(obj.Solid);
            else if (obj.Entity != null)
                entities.Add(obj.Entity);
            else if (obj.IsGroup)
            {
                foreach (var child in obj.Group)
                    Flatten(child, brushes, entities);
            }
        }

        private static void WriteBrush(StringBuilder sb, RmfSolid solid, ConvertResult result)
        {
            var faces = new StringBuilder();
            int kept = 0;
            foreach (var face in solid.Faces)
            {
                if (TryWriteFace(faces, face, result))
                    kept++;
            }

            if (kept == 0)
            {
                Warn(result, "Skipped a brush with no usable faces.");
                return;
            }

            sb.AppendLine("{");
            sb.Append(faces);
            sb.AppendLine("}");
            result.BrushCount++;
            result.FaceCount += kept;
        }

        private static void WriteEntity(StringBuilder sb, RmfEntity entity, ConvertResult result)
        {
            sb.AppendLine("{");
            string classname = entity.ClassName;
            if (classname == null)
                classname = "";
            classname = classname.Trim();
            if (classname.Length == 0)
            {
                classname = "info_null";
                Warn(result, "Found an entity with no classname, written as info_null.");
            }
            sb.AppendLine("\"classname\" \"" + Escape(classname) + "\"");

            foreach (var kv in entity.KeyValues)
            {
                if (string.Equals(kv.Key, "classname", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(kv.Key, "origin", StringComparison.OrdinalIgnoreCase) && entity.Brushes.Count > 0)
                    continue;
                sb.AppendLine("\"" + Escape(kv.Key) + "\" \"" + Escape(kv.Value) + "\"");
            }

            if (entity.Brushes.Count == 0)
                sb.AppendLine("\"origin\" \"" + F(entity.Origin[0]) + " " + F(entity.Origin[1]) + " " + F(entity.Origin[2]) + "\"");

            foreach (var solid in entity.Brushes)
                WriteBrush(sb, solid, result);

            sb.AppendLine("}");
            result.EntityCount++;
        }

        private static void WritePaths(StringBuilder sb, RmfDoc doc, ConvertResult result)
        {
            for (int pi = 0; pi < doc.Paths.Count; pi++)
            {
                var path = doc.Paths[pi];
                if (path.Corners.Count == 0)
                    continue;

                string baseName = path.Name;
                if (baseName == null || baseName.Trim().Length == 0)
                    baseName = "path" + (pi + 1).ToString("D2") + "_";
                else
                    baseName = baseName.Trim();
                string classname = path.Class;
                if (classname == null || classname.Trim().Length == 0)
                    classname = "path_corner";
                else
                    classname = classname.Trim();

                var targetnames = new List<string>(path.Corners.Count);
                for (int i = 0; i < path.Corners.Count; i++)
                {
                    var corner = path.Corners[i];
                    if (corner.NameOverride != null && corner.NameOverride.Trim().Length > 0)
                        targetnames.Add(corner.NameOverride.Trim());
                    else
                        targetnames.Add(baseName + corner.Index.ToString("D2"));
                }

                for (int i = 0; i < path.Corners.Count; i++)
                {
                    var corner = path.Corners[i];
                    var entity = new RmfEntity();
                    entity.ClassName = classname;
                    entity.Origin = corner.Pos;
                    entity.KeyValues.Add(new KeyValuePair<string, string>("targetname", targetnames[i]));

                    if (i + 1 < path.Corners.Count)
                        entity.KeyValues.Add(new KeyValuePair<string, string>("target", targetnames[i + 1]));
                    else if (path.Type == 1 && path.Corners.Count > 1)
                        entity.KeyValues.Add(new KeyValuePair<string, string>("target", targetnames[0]));

                    foreach (var kv in corner.KeyValues)
                    {
                        if (string.Equals(kv.Key, "classname", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (string.Equals(kv.Key, "targetname", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (string.Equals(kv.Key, "target", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (string.Equals(kv.Key, "origin", StringComparison.OrdinalIgnoreCase))
                            continue;
                        entity.KeyValues.Add(kv);
                    }

                    WriteEntity(sb, entity, result);
                }
            }
        }

        private static bool TryWriteFace(StringBuilder sb, RmfFace face, ConvertResult result)
        {
            float[] p0 = face.Plane[0];
            float[] p1 = face.Plane[1];
            float[] p2 = face.Plane[2];

            double ux = (double)p1[0] - (double)p0[0];
            double uy = (double)p1[1] - (double)p0[1];
            double uz = (double)p1[2] - (double)p0[2];
            double vx = (double)p2[0] - (double)p0[0];
            double vy = (double)p2[1] - (double)p0[1];
            double vz = (double)p2[2] - (double)p0[2];
            double nx = uy * vz - uz * vy;
            double ny = uz * vx - ux * vz;
            double nz = ux * vy - uy * vx;
            if ((nx * nx + ny * ny + nz * nz) < 1e-12)
            {
                Warn(result, "Skipped a degenerate face.");
                return false;
            }

            string texture = SanitizeTextureName(face.Texture, result);

            // Valve220 face syntax, byte-identical to what the editor itself exports:
            // the stored U/V axes, shifts, rotation and scales pass straight through,
            // so texture alignment is preserved exactly.
            sb.AppendLine("( " + F(p0[0]) + " " + F(p0[1]) + " " + F(p0[2]) + " ) ( " + F(p1[0]) + " " + F(p1[1]) + " " + F(p1[2]) + " ) ( " + F(p2[0]) + " " + F(p2[1]) + " " + F(p2[2]) + " ) " + texture + " [ " + F(face.U[0]) + " " + F(face.U[1]) + " " + F(face.U[2]) + " " + F(face.XShift) + " ] [ " + F(face.V[0]) + " " + F(face.V[1]) + " " + F(face.V[2]) + " " + F(face.YShift) + " ] " + F(face.Rotation) + " " + F(face.XScale) + " " + F(face.YScale));
            return true;
        }

        private static string SanitizeTextureName(string texture, ConvertResult result)
        {
            string clean = texture ?? "";
            clean = clean.Trim();
            var sb = new StringBuilder(clean.Length);
            bool changed = false;
            foreach (char c in clean)
            {
                if (char.IsWhiteSpace(c) || c == 34 || c == 91 || c == 93 || c == 40 || c == 41)
                {
                    sb.Append((char)95);
                    changed = true;
                }
                else
                {
                    sb.Append(c);
                }
            }
            if (sb.Length == 0)
            {
                Warn(result, "Found a face with an empty texture name, written as NULL.");
                return "NULL";
            }
            if (changed)
                Warn(result, "Sanitized texture name.");
            return sb.ToString();
        }

        private static string Escape(string value)
        {
            if (value == null)
                return "";
            return value.Replace("\r", " ").Replace("\n", " ").Replace((char)34, (char)39);
        }

        private static string F(float value)
        {
            if (!float.IsFinite(value))
                return "0";
            string s = ((double)value).ToString("0.######", CultureInfo.InvariantCulture);
            if (s == "-0")
                return "0";
            return s;
        }
    }
}
