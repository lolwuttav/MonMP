using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MonMP.Core.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Local cosmetic calibration only. The two rigs keep separate bone namespaces;
    // no native animation asset, gameplay transform or network record is edited.
    internal sealed class EmotePoseOverrides
    {
        private const int MaximumBytes = 256 * 1024;
        private const int MaximumPoses = 6;
        internal enum ViewKind { FirstPerson, Remote }

        internal sealed class BoneRotation
        {
            public float X, Y, Z;
            internal BoneRotation Copy() => (BoneRotation)MemberwiseClone();
        }

        internal sealed class Pose
        {
            public float X, Y, Z, Pitch, Yaw, Roll, ElbowX, ElbowY, ElbowZ;
            public float WaveScale = 1, WaveRate = 1;
            public Dictionary<string, BoneRotation> Bones = new Dictionary<string, BoneRotation>(StringComparer.Ordinal);
            internal Vector3 Position => new Vector3(X, Y, Z);
            internal Vector3 Euler => new Vector3(Pitch, Yaw, Roll);
            internal Vector3 Elbow => new Vector3(ElbowX, ElbowY, ElbowZ);

            internal Pose Copy()
            {
                var copy = (Pose)MemberwiseClone();
                copy.Bones = new Dictionary<string, BoneRotation>(StringComparer.Ordinal);
                foreach (var pair in Bones) copy.Bones.Add(pair.Key, pair.Value.Copy());
                return copy;
            }
        }

        private sealed class Record
        {
            public string View = "", Kind = "";
            public Pose Pose = new Pose();
        }

        private sealed class Document
        {
            public int Version = 1;
            public List<Record> Poses = new List<Record>();
        }

        private static readonly IReadOnlyList<string> firstPersonBones = Array.AsReadOnly(new[]
        {
            "Upper Arm.R", "Lower Arm.R", "Hand.R", "Hand.Finger00.R", "Hand.Finger01.R",
            "Hand.Finger10.R", "Hand.Finger11.R", "Hand.Finger20.R", "Hand.Finger21.R",
            "Hand.Finger30.R", "Hand.Finger31.R", "Thumb01.R", "Thumb02.R", "Thumb03.R"
        });
        private static readonly IReadOnlyList<string> remoteBones = Array.AsReadOnly(new[]
        {
            "Upper Arm.R", "Lower Arm.R", "Hand.R", "Fingers01.R", "Fingers02.R", "Thumb01.R", "Thumb02.R"
        });
        private Dictionary<(ViewKind View, PlayerEmoteKind Kind), Pose> saved = new Dictionary<(ViewKind, PlayerEmoteKind), Pose>();
#if MONMP_DEVELOPMENT
        private bool recoveredBackup;
#endif

        internal static EmotePoseOverrides? Active { get; set; }
        internal string Path { get; }
        internal int Count => saved.Count;
        internal string LoadStatus { get; private set; } = "No saved emote poses.";

        internal EmotePoseOverrides(string path)
        {
            Path = path;
            Reload();
        }

        // Borrowed render-loop data. Callers treat this as immutable; the editor
        // always uses GetCopy and Save detaches its draft before publishing it.
        internal Pose? Get(ViewKind view, PlayerEmoteKind kind) => saved.TryGetValue((view, kind), out var pose) ? pose : null;

        internal Pose GetCopy(ViewKind view, PlayerEmoteKind kind)
        {
            ValidateDomain(view, kind);
            return Get(view, kind)?.Copy() ?? new Pose();
        }

        internal static IReadOnlyList<string> BonesFor(ViewKind view)
        {
            if (view == ViewKind.FirstPerson) return firstPersonBones;
            if (view == ViewKind.Remote) return remoteBones;
            throw new InvalidDataException("Unsupported emote view.");
        }

        internal void Reload()
        {
#if MONMP_DEVELOPMENT
            recoveredBackup = false;
#endif
            if (TryRead(Path, out var next, out int skipped, out string primaryError))
            {
                saved = next;
                LoadStatus = "Loaded " + saved.Count + " emote poses." + SkippedMessage(skipped);
                return;
            }
            if (TryRead(Path + ".bak", out next, out skipped, out _))
            {
                saved = next;
#if MONMP_DEVELOPMENT
                recoveredBackup = true;
#endif
                LoadStatus = "Recovered " + saved.Count + " emote poses from backup." + SkippedMessage(skipped);
                return;
            }
            saved = new Dictionary<(ViewKind, PlayerEmoteKind), Pose>();
            LoadStatus = primaryError.Length == 0 ? "No saved emote poses." : "Emote poses were not loaded: " + primaryError;
        }

        private static string SkippedMessage(int skipped) => skipped == 0 ? "" : " Skipped " + skipped + " invalid records.";

        private static bool TryRead(string path, out Dictionary<(ViewKind View, PlayerEmoteKind Kind), Pose> result,
            out int skipped, out string error)
        {
            result = new Dictionary<(ViewKind, PlayerEmoteKind), Pose>(); skipped = 0; error = "";
            try
            {
                if (!File.Exists(path)) return false;
                using var stream = File.OpenRead(path);
                if (stream.Length > MaximumBytes) throw new InvalidDataException("Emote-pose file exceeds 256 KiB.");
                using var buffer = new MemoryStream();
                var bytes = new byte[4096];
                int read;
                while ((read = stream.Read(bytes, 0, bytes.Length)) != 0)
                {
                    if (buffer.Length + read > MaximumBytes) throw new InvalidDataException("Emote-pose file exceeds 256 KiB.");
                    buffer.Write(bytes, 0, read);
                }
                buffer.Position = 0;
                using var text = new StreamReader(buffer, new UTF8Encoding(false, true), true);
                using var reader = new JsonTextReader(text) { MaxDepth = 8, DateParseHandling = DateParseHandling.None };
                var root = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new InvalidDataException("Unexpected data after emote-pose document.");
                if (root.Properties().Any(p => p.Name != "Version" && p.Name != "Poses") ||
                    root["Version"]?.Type != JTokenType.Integer || (long)root["Version"]! != 1 ||
                    !(root["Poses"] is JArray records) || records.Count > MaximumPoses)
                    throw new InvalidDataException("Unsupported emote-pose document.");
                var seen = new HashSet<(ViewKind, PlayerEmoteKind)>();
                foreach (var token in records)
                {
                    try
                    {
                        if (!(token is JObject record) || record["View"]?.Type != JTokenType.String || record["Kind"]?.Type != JTokenType.String ||
                            !Enum.TryParse((string?)record["View"], false, out ViewKind view) || view.ToString() != (string?)record["View"] ||
                            !Enum.TryParse((string?)record["Kind"], false, out PlayerEmoteKind kind) || kind.ToString() != (string?)record["Kind"])
                            throw new InvalidDataException("Invalid emote-pose identity.");
                        ValidateDomain(view, kind);
                        var key = (view, kind);
                        // Duplicate identities are ambiguous. Drop that domain rather than
                        // allowing document order to select an unexpected calibration.
                        if (!seen.Add(key)) { result.Remove(key); skipped++; continue; }
                        if (record.Properties().Any(p => p.Name != "View" && p.Name != "Kind" && p.Name != "Pose") || !(record["Pose"] is JObject poseToken))
                            throw new InvalidDataException("Invalid emote-pose record.");
                        CheckNumberTokens(poseToken);
                        var serializer = JsonSerializer.Create(new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error });
                        var pose = poseToken.ToObject<Pose>(serializer);
                        Validate(view, kind, pose!);
                        result.Add(key, pose!.Copy());
                    }
                    catch (Exception) { skipped++; }
                }
                return true;
            }
            catch (Exception ex) { result.Clear(); error = ex.Message; return false; }
        }

        private static void CheckNumberTokens(JObject pose)
        {
            foreach (var property in pose.Properties())
            {
                if (property.Name == "Bones")
                {
                    if (!(property.Value is JObject bones)) throw new InvalidDataException("Invalid emote bones.");
                    foreach (var bone in bones.Properties())
                    {
                        if (!(bone.Value is JObject rotation)) throw new InvalidDataException("Invalid emote bone rotation.");
                        foreach (var axis in rotation.Properties())
                            if (axis.Value.Type != JTokenType.Float && axis.Value.Type != JTokenType.Integer)
                                throw new InvalidDataException("Emote bone rotations require numbers.");
                    }
                }
                else if (property.Value.Type != JTokenType.Float && property.Value.Type != JTokenType.Integer)
                    throw new InvalidDataException("Emote pose values require numbers.");
            }
        }

#if MONMP_DEVELOPMENT
        internal bool Save(ViewKind view, PlayerEmoteKind kind, Pose pose, out string message)
        {
            try
            {
                Validate(view, kind, pose);
                var next = saved.ToDictionary(p => p.Key, p => p.Value.Copy());
                next[(view, kind)] = pose.Copy();
                Write(next);
                message = "Saved " + view + " " + kind + " pose."; return true;
            }
            catch (Exception ex) { message = "Emote pose was not saved: " + ex.Message; return false; }
        }

        internal bool Remove(ViewKind view, PlayerEmoteKind kind, out string message)
        {
            try
            {
                ValidateDomain(view, kind);
                if (!saved.ContainsKey((view, kind))) { message = "This emote already uses its default pose."; return true; }
                var next = saved.ToDictionary(p => p.Key, p => p.Value.Copy());
                next.Remove((view, kind)); Write(next);
                message = "Reset " + view + " " + kind + " pose."; return true;
            }
            catch (Exception ex) { message = "Emote pose was not reset: " + ex.Message; return false; }
        }

        private void Write(Dictionary<(ViewKind View, PlayerEmoteKind Kind), Pose> next)
        {
            if (next.Count > MaximumPoses) throw new InvalidDataException("Too many saved emote poses.");
            foreach (var pair in next) Validate(pair.Key.View, pair.Key.Kind, pair.Value);
            var document = new Document { Poses = next.OrderBy(p => p.Key.View).ThenBy(p => p.Key.Kind)
                .Select(p => new Record { View = p.Key.View.ToString(), Kind = p.Key.Kind.ToString(), Pose = p.Value }).ToList() };
            byte[] bytes = new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(document, Formatting.Indented));
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("Emote-pose file exceeds 256 KiB.");
            string directory = System.IO.Path.GetDirectoryName(Path) ?? throw new InvalidOperationException("Settings require a directory.");
            Directory.CreateDirectory(directory);
            string temporary = Path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
                }
                // When recovering a damaged primary, retain the known-good backup.
                if (File.Exists(Path)) File.Replace(temporary, Path, recoveredBackup ? null : Path + ".bak");
                else File.Move(temporary, Path);
                saved = next; recoveredBackup = false;
                LoadStatus = "Saved " + saved.Count + " emote poses.";
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
#endif

        internal static void Validate(ViewKind view, PlayerEmoteKind kind, Pose pose)
        {
            ValidateDomain(view, kind);
            if (pose == null || pose.Bones == null) throw new InvalidDataException("Missing emote pose or bones.");
            ValidateValue(pose.X, -0.6f, 0.6f); ValidateValue(pose.Y, -0.6f, 0.6f); ValidateValue(pose.Z, -0.6f, 0.6f);
            ValidateValue(pose.Pitch, -180, 180); ValidateValue(pose.Yaw, -180, 180); ValidateValue(pose.Roll, -180, 180);
            ValidateValue(pose.ElbowX, -2, 2); ValidateValue(pose.ElbowY, -2, 2); ValidateValue(pose.ElbowZ, -2, 2);
            ValidateValue(pose.WaveScale, 0, 2); ValidateValue(pose.WaveRate, 0.25f, 3);
            var allowed = BonesFor(view);
            if (pose.Bones.Count > allowed.Count) throw new InvalidDataException("Too many emote bone rotations.");
            foreach (var pair in pose.Bones)
            {
                if (!allowed.Contains(pair.Key, StringComparer.Ordinal) || pair.Value == null)
                    throw new InvalidDataException("Unsupported bone for this emote view.");
                ValidateValue(pair.Value.X, -180, 180); ValidateValue(pair.Value.Y, -180, 180); ValidateValue(pair.Value.Z, -180, 180);
            }
        }

        private static void ValidateDomain(ViewKind view, PlayerEmoteKind kind)
        {
            if ((view != ViewKind.FirstPerson && view != ViewKind.Remote) || !PlayerEmotePolicy.IsCosmeticGesture(kind))
                throw new InvalidDataException("Unsupported emote view or gesture.");
        }

        private static void ValidateValue(float value, float minimum, float maximum)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < minimum || value > maximum)
                throw new InvalidDataException("Emote-pose value outside its supported finite range.");
        }
    }
}
