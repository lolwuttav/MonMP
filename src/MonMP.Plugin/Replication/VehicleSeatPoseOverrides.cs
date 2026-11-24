using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Local avatar presentation settings. A calibration never moves a native seat,
    // changes vehicle ownership or changes another player's settings.
    internal sealed class VehicleSeatPoseOverrides
    {
        private const int MaximumBytes = 1024 * 1024;
        private const int MaximumPoses = 2048;

        // Seat-only editor state stays out of the held-item settings format. Grip.Copy
        // preserves this subtype and its value fields when the shared editor saves undo.

#if MONMP_DEVELOPMENT
        internal sealed class SeatDraft : HeldGripOverrides.Grip
        {
            [JsonIgnore] internal bool CustomFeet, FeetInitialized;
            [JsonIgnore] internal float RightFootX, RightFootY, RightFootZ, RightFootPitch, RightFootYaw, RightFootRoll;
            [JsonIgnore] internal float LeftFootX, LeftFootY, LeftFootZ, LeftFootPitch, LeftFootYaw, LeftFootRoll;
            internal Vector3 RightFootPosition => new Vector3(RightFootX, RightFootY, RightFootZ);
            internal Vector3 RightFootEuler => new Vector3(RightFootPitch, RightFootYaw, RightFootRoll);
            internal Vector3 LeftFootPosition => new Vector3(LeftFootX, LeftFootY, LeftFootZ);
            internal Vector3 LeftFootEuler => new Vector3(LeftFootPitch, LeftFootYaw, LeftFootRoll);

            internal void SetRightFoot(Vector3 position, Vector3 euler)
            {
                RightFootX = position.x; RightFootY = position.y; RightFootZ = position.z;
                RightFootPitch = euler.x; RightFootYaw = euler.y; RightFootRoll = euler.z; FeetInitialized = true;
            }

            internal void SetLeftFoot(Vector3 position, Vector3 euler)
            {
                LeftFootX = position.x; LeftFootY = position.y; LeftFootZ = position.z;
                LeftFootPitch = euler.x; LeftFootYaw = euler.y; LeftFootRoll = euler.z; FeetInitialized = true;
            }

            internal void CopyFeetFrom(SeatDraft source)
            {
                CustomFeet = source.CustomFeet; FeetInitialized = source.FeetInitialized;
                RightFootX = source.RightFootX; RightFootY = source.RightFootY; RightFootZ = source.RightFootZ;
                RightFootPitch = source.RightFootPitch; RightFootYaw = source.RightFootYaw; RightFootRoll = source.RightFootRoll;
                LeftFootX = source.LeftFootX; LeftFootY = source.LeftFootY; LeftFootZ = source.LeftFootZ;
                LeftFootPitch = source.LeftFootPitch; LeftFootYaw = source.LeftFootYaw; LeftFootRoll = source.LeftFootRoll;
            }

            internal static bool SameFeet(HeldGripOverrides.Grip a, HeldGripOverrides.Grip b)
            {
                var left = a as SeatDraft; var right = b as SeatDraft;
                return (left?.CustomFeet ?? false) == (right?.CustomFeet ?? false) &&
                    (left?.FeetInitialized ?? false) == (right?.FeetInitialized ?? false) &&
                    (left?.RightFootX ?? 0) == (right?.RightFootX ?? 0) &&
                    (left?.RightFootY ?? 0) == (right?.RightFootY ?? 0) &&
                    (left?.RightFootZ ?? 0) == (right?.RightFootZ ?? 0) &&
                    (left?.RightFootPitch ?? 0) == (right?.RightFootPitch ?? 0) &&
                    (left?.RightFootYaw ?? 0) == (right?.RightFootYaw ?? 0) &&
                    (left?.RightFootRoll ?? 0) == (right?.RightFootRoll ?? 0) &&
                    (left?.LeftFootX ?? 0) == (right?.LeftFootX ?? 0) &&
                    (left?.LeftFootY ?? 0) == (right?.LeftFootY ?? 0) &&
                    (left?.LeftFootZ ?? 0) == (right?.LeftFootZ ?? 0) &&
                    (left?.LeftFootPitch ?? 0) == (right?.LeftFootPitch ?? 0) &&
                    (left?.LeftFootYaw ?? 0) == (right?.LeftFootYaw ?? 0) &&
                    (left?.LeftFootRoll ?? 0) == (right?.LeftFootRoll ?? 0);
            }
        }
#endif


        internal sealed class Pose
        {
            [JsonProperty(Required = Required.Always)] public string Vehicle = "";
            [JsonProperty(Required = Required.Always)] public string Seat = "";
            [JsonProperty(Required = Required.Always)] public string Shape = "";
            public float BodyX, BodyY, BodyZ, BodyPitch, BodyYaw, BodyRoll;
            public bool CustomHands;
            public float RightX, RightY, RightZ, RightPitch, RightYaw, RightRoll;
            public float LeftX, LeftY, LeftZ, LeftPitch, LeftYaw, LeftRoll;
            // Optional v1 extension. Missing fields retain the native seated feet.
            public bool CustomFeet;
            public float RightFootX, RightFootY, RightFootZ, RightFootPitch, RightFootYaw, RightFootRoll;
            public float LeftFootX, LeftFootY, LeftFootZ, LeftFootPitch, LeftFootYaw, LeftFootRoll;

            internal Pose Copy() => (Pose)MemberwiseClone();

            // Copy only the calibration; the selected destination keeps its identity.

#if MONMP_DEVELOPMENT
            internal void CopyPoseFrom(Pose source)
            {
                Validate(source);
                BodyX = source.BodyX; BodyY = source.BodyY; BodyZ = source.BodyZ;
                BodyPitch = source.BodyPitch; BodyYaw = source.BodyYaw; BodyRoll = source.BodyRoll;
                CustomHands = source.CustomHands;
                RightX = source.RightX; RightY = source.RightY; RightZ = source.RightZ;
                RightPitch = source.RightPitch; RightYaw = source.RightYaw; RightRoll = source.RightRoll;
                LeftX = source.LeftX; LeftY = source.LeftY; LeftZ = source.LeftZ;
                LeftPitch = source.LeftPitch; LeftYaw = source.LeftYaw; LeftRoll = source.LeftRoll;
                CustomFeet = source.CustomFeet;
                RightFootX = source.RightFootX; RightFootY = source.RightFootY; RightFootZ = source.RightFootZ;
                RightFootPitch = source.RightFootPitch; RightFootYaw = source.RightFootYaw; RightFootRoll = source.RightFootRoll;
                LeftFootX = source.LeftFootX; LeftFootY = source.LeftFootY; LeftFootZ = source.LeftFootZ;
                LeftFootPitch = source.LeftFootPitch; LeftFootYaw = source.LeftFootYaw; LeftFootRoll = source.LeftFootRoll;
            }
#endif


            // The shared editor uses item offset fields for the seated body, and palm
            // fields for hands in the seat frame. Legacy grip width is never used here.

#if MONMP_DEVELOPMENT
            internal SeatDraft ToGrip()
            {
                Validate(this);
                return new SeatDraft
                {
                    Prefab = Vehicle, Variant = Seat, Shape = Shape,
                    ItemX = BodyX, ItemY = BodyY, ItemZ = BodyZ,
                    ItemPitch = BodyPitch, ItemYaw = BodyYaw, ItemRoll = BodyRoll,
                    TwoHanded = CustomHands, Width = 0,
                    X = RightX, Y = RightY, Z = RightZ,
                    Pitch = RightPitch, Yaw = RightYaw, Roll = RightRoll,
                    LeftX = LeftX, LeftY = LeftY, LeftZ = LeftZ,
                    LeftPitch = LeftPitch, LeftYaw = LeftYaw, LeftRoll = LeftRoll,
                    LeftInitialized = CustomHands || LeftX != 0 || LeftY != 0 || LeftZ != 0 ||
                        LeftPitch != 0 || LeftYaw != 0 || LeftRoll != 0,
                    CustomFeet = CustomFeet,
                    RightFootX = RightFootX, RightFootY = RightFootY, RightFootZ = RightFootZ,
                    RightFootPitch = RightFootPitch, RightFootYaw = RightFootYaw, RightFootRoll = RightFootRoll,
                    LeftFootX = LeftFootX, LeftFootY = LeftFootY, LeftFootZ = LeftFootZ,
                    LeftFootPitch = LeftFootPitch, LeftFootYaw = LeftFootYaw, LeftFootRoll = LeftFootRoll,
                    FeetInitialized = CustomFeet || RightFootX != 0 || RightFootY != 0 || RightFootZ != 0 ||
                        RightFootPitch != 0 || RightFootYaw != 0 || RightFootRoll != 0 ||
                        LeftFootX != 0 || LeftFootY != 0 || LeftFootZ != 0 ||
                        LeftFootPitch != 0 || LeftFootYaw != 0 || LeftFootRoll != 0
                };
            }
#endif


#if MONMP_DEVELOPMENT
            internal static Pose FromGrip(string vehicle, string seat, string shape, HeldGripOverrides.Grip grip)
            {
                if (grip == null || grip.Prefab != vehicle || grip.Variant != seat || grip.Shape != shape)
                    throw new InvalidDataException("Seat-pose draft identity does not match its destination.");
                var pose = new Pose
                {
                    Vehicle = vehicle, Seat = seat, Shape = shape,
                    BodyX = grip.ItemX, BodyY = grip.ItemY, BodyZ = grip.ItemZ,
                    BodyPitch = grip.ItemPitch, BodyYaw = grip.ItemYaw, BodyRoll = grip.ItemRoll,
                    CustomHands = grip.TwoHanded,
                    RightX = grip.X, RightY = grip.Y, RightZ = grip.Z,
                    RightPitch = grip.Pitch, RightYaw = grip.Yaw, RightRoll = grip.Roll,
                    LeftX = grip.LeftX, LeftY = grip.LeftY, LeftZ = grip.LeftZ,
                    LeftPitch = grip.LeftPitch, LeftYaw = grip.LeftYaw, LeftRoll = grip.LeftRoll
                };
                if (grip is SeatDraft draft)
                {
                    pose.CustomFeet = draft.CustomFeet;
                    pose.RightFootX = draft.RightFootX; pose.RightFootY = draft.RightFootY; pose.RightFootZ = draft.RightFootZ;
                    pose.RightFootPitch = draft.RightFootPitch; pose.RightFootYaw = draft.RightFootYaw; pose.RightFootRoll = draft.RightFootRoll;
                    pose.LeftFootX = draft.LeftFootX; pose.LeftFootY = draft.LeftFootY; pose.LeftFootZ = draft.LeftFootZ;
                    pose.LeftFootPitch = draft.LeftFootPitch; pose.LeftFootYaw = draft.LeftFootYaw; pose.LeftFootRoll = draft.LeftFootRoll;
                }
                Validate(pose);
                return pose;
            }
#endif

        }

        private sealed class Document
        {
            [JsonProperty(Required = Required.Always)] public int Version = 1;
            [JsonProperty(Required = Required.Always)] public List<Pose> Seats = new List<Pose>();
        }

        // The shipped Mono runtime intermittently throws in ValueTuple<string,string>.GetHashCode
        // during remote presentation. Keep the pair allocation-free without that generic hash path.
        private readonly struct SeatKey : IEquatable<SeatKey>
        {
            private readonly string? vehicle, seat;
            internal SeatKey(string vehicle, string seat) { this.vehicle = vehicle; this.seat = seat; }
            public bool Equals(SeatKey other) => string.Equals(vehicle, other.vehicle, StringComparison.Ordinal) &&
                string.Equals(seat, other.seat, StringComparison.Ordinal);
            public override bool Equals(object? other) => other is SeatKey key && Equals(key);
            public override int GetHashCode()
            {
                unchecked { return ((vehicle == null ? 0 : StringComparer.Ordinal.GetHashCode(vehicle)) * 397) ^
                    (seat == null ? 0 : StringComparer.Ordinal.GetHashCode(seat)); }
            }
        }

        private static readonly Dictionary<SeatKey, Pose> defaults = ReadDefaults();
        private readonly Dictionary<SeatKey, Pose> saved = new Dictionary<SeatKey, Pose>();
        internal string Path { get; }
        internal string LoadStatus { get; private set; } = "No saved seat poses.";
        internal int Count => saved.Count;
        internal static int DefaultCount => defaults.Count;

        internal VehicleSeatPoseOverrides(string path)
        {
            Path = path;
            if (!File.Exists(path)) return;
            try
            {
                // Read no more than the allowed bytes, even if the file grows while open.
                using var stream = File.OpenRead(path);
                if (stream.Length > MaximumBytes) throw new InvalidDataException("Seat-pose file exceeds 1 MiB.");
                using var buffer = new MemoryStream();
                var bytes = new byte[4096];
                int read;
                while ((read = stream.Read(bytes, 0, bytes.Length)) != 0)
                {
                    if (buffer.Length + read > MaximumBytes) throw new InvalidDataException("Seat-pose file exceeds 1 MiB.");
                    buffer.Write(bytes, 0, read);
                }
                buffer.Position = 0;
                using var reader = new StreamReader(buffer, Encoding.UTF8, true);
                foreach (var pair in ReadDocument(reader.ReadToEnd())) saved.Add(pair.Key, pair.Value);
                LoadStatus = "Loaded " + saved.Count + " saved seat poses.";
            }
            catch (Exception ex)
            {
                saved.Clear(); LoadStatus = "Seat poses were not loaded: " + ex.Message;
            }
        }

        private static Dictionary<SeatKey, Pose> ReadDefaults()
        {
            // Shared read-only presentation data. Missing/broken defaults must never
            // prevent startup or affect loading and saving personal overrides.
            try
            {
                using var stream = typeof(VehicleSeatPoseOverrides).Assembly.GetManifestResourceStream("MonMP.VehicleSeatPoseDefaults.json");
                if (stream == null || stream.Length > MaximumBytes) return new Dictionary<SeatKey, Pose>();
                using var reader = new StreamReader(stream);
                return ReadDocument(reader.ReadToEnd());
            }
            catch (Exception) { return new Dictionary<SeatKey, Pose>(); }
        }

        private static Dictionary<SeatKey, Pose> ReadDocument(string text)
        {
            var settings = new JsonSerializerSettings { MaxDepth = 8, CheckAdditionalContent = true };
            var document = JsonConvert.DeserializeObject<Document>(text, settings);
            if (document == null || document.Version != 1 || document.Seats == null || document.Seats.Count > MaximumPoses)
                throw new InvalidDataException("Unsupported seat-pose document.");
            var result = new Dictionary<SeatKey, Pose>();
            foreach (var pose in document.Seats)
            {
                Validate(pose);
                var key = Key(pose.Vehicle, pose.Seat);
                if (result.ContainsKey(key)) throw new InvalidDataException("Duplicate seat-pose key.");
                result.Add(key, pose.Copy());
            }
            return result;
        }

        private static SeatKey Key(string vehicle, string seat) => new SeatKey(vehicle, seat);

        private bool TryFind(string vehicle, string seat, string shape, out Pose pose)
        {
            var key = Key(vehicle, seat);
            if (saved.TryGetValue(key, out pose!) && pose.Shape == shape) return true;
            if (defaults.TryGetValue(key, out pose!) && pose.Shape == shape) return true;
            pose = null!; return false;
        }

        internal bool HasSaved(string vehicle, string seat, string shape) =>
            saved.TryGetValue(Key(vehicle, seat), out var pose) && pose.Shape == shape;

        internal bool TryGet(string vehicle, string seat, string shape, out Pose pose)
        {
            if (TryFind(vehicle, seat, shape, out var value)) { pose = value.Copy(); return true; }
            pose = null!; return false;
        }

        // Borrowed read-only data for the render loop. Callers must never modify it;
        // edits use detached lookups/chooser snapshots and activate only on Save.
        internal bool TryGetRuntime(string vehicle, string seat, string shape, out Pose pose)
            => TryFind(vehicle, seat, shape, out pose);

#if MONMP_DEVELOPMENT
        internal void TestRuntimeLookups()
        {
            int count = 0;
            for (int repeat = 0; repeat < 500; repeat++)
                foreach (var expected in defaults.Values)
                {
                    if (!TryGetRuntime(expected.Vehicle, expected.Seat, expected.Shape, out var pose) ||
                        !TryGetRuntime(expected.Vehicle, expected.Seat, expected.Shape, out var again) ||
                        !ReferenceEquals(pose, again) || pose.Shape != expected.Shape)
                        throw new InvalidOperationException("Native seat-pose lookup lost its identity or shape gate.");
                    count += 2;
                }
            if (count == 0) throw new InvalidOperationException("Native seat-pose defaults are unavailable.");
            Debug.Log("MonMP native seat-pose lookup passed: " + count + " allocation-free key lookups, saved/default shape gates retained.");
        }
#endif


#if MONMP_DEVELOPMENT
        internal IReadOnlyList<Pose> GetSavedPoses() => saved.Values.OrderBy(p => p.Vehicle, StringComparer.Ordinal)
            .ThenBy(p => p.Seat, StringComparer.Ordinal).Select(p => p.Copy()).ToArray();
#endif


#if MONMP_DEVELOPMENT
        internal IReadOnlyList<Pose> GetCalibratedPoses() => saved.Values.Concat(defaults.Values.Where(p => !HasSaved(p.Vehicle, p.Seat, p.Shape)))
            .OrderBy(p => p.Vehicle, StringComparer.Ordinal).ThenBy(p => p.Seat, StringComparer.Ordinal)
            .ThenBy(p => p.Shape, StringComparer.Ordinal).Select(p => p.Copy()).ToArray();
#endif


#if MONMP_DEVELOPMENT
        internal void Save(Pose pose)
        {
            Validate(pose);
            var next = saved.ToDictionary(p => p.Key, p => p.Value.Copy());
            next[Key(pose.Vehicle, pose.Seat)] = pose.Copy(); Write(next);
        }
#endif


#if MONMP_DEVELOPMENT
        internal void Remove(string vehicle, string seat)
        {
            if (!saved.ContainsKey(Key(vehicle, seat))) return;
            var next = saved.ToDictionary(p => p.Key, p => p.Value.Copy());
            next.Remove(Key(vehicle, seat)); Write(next);
        }
#endif


        // Finish a validated, bounded replacement before changing active settings.
        // A .bak file retains the previous settings, and failed writes retain the cache.

#if MONMP_DEVELOPMENT
        private void Write(Dictionary<SeatKey, Pose> next)
        {
            if (next.Count > MaximumPoses) throw new InvalidDataException("Too many saved seat poses.");
            var document = new Document { Seats = next.Values.OrderBy(p => p.Vehicle, StringComparer.Ordinal)
                .ThenBy(p => p.Seat, StringComparer.Ordinal).ToList() };
            string text = JsonConvert.SerializeObject(document, Formatting.Indented);
            if (Encoding.UTF8.GetByteCount(text) > MaximumBytes) throw new InvalidDataException("Seat-pose file exceeds 1 MiB.");
            string directory = System.IO.Path.GetDirectoryName(Path) ?? throw new InvalidOperationException("Settings require a directory.");
            Directory.CreateDirectory(directory);
            string temporary = Path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, text, new UTF8Encoding(false));
                if (File.Exists(Path)) File.Replace(temporary, Path, Path + ".bak"); else File.Move(temporary, Path);
                saved.Clear(); foreach (var pair in next) saved.Add(pair.Key, pair.Value);
                LoadStatus = "Saved " + saved.Count + " seat poses.";
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
#endif


        internal static void Validate(Pose pose)
        {
            if (pose == null || !ValidIdentity(pose.Vehicle) || !ValidIdentity(pose.Seat) ||
                pose.Shape == null || pose.Shape.Length != 64 || !pose.Shape.All(Uri.IsHexDigit))
                throw new InvalidDataException("Invalid seat-pose identity.");
            ValidateValue(pose.BodyX, 2); ValidateValue(pose.BodyY, 2); ValidateValue(pose.BodyZ, 2);
            ValidateValue(pose.BodyPitch, 360); ValidateValue(pose.BodyYaw, 360); ValidateValue(pose.BodyRoll, 360);
            ValidateValue(pose.RightX, 10); ValidateValue(pose.RightY, 10); ValidateValue(pose.RightZ, 10);
            ValidateValue(pose.RightPitch, 360); ValidateValue(pose.RightYaw, 360); ValidateValue(pose.RightRoll, 360);
            ValidateValue(pose.LeftX, 10); ValidateValue(pose.LeftY, 10); ValidateValue(pose.LeftZ, 10);
            ValidateValue(pose.LeftPitch, 360); ValidateValue(pose.LeftYaw, 360); ValidateValue(pose.LeftRoll, 360);
            ValidateValue(pose.RightFootX, 10); ValidateValue(pose.RightFootY, 10); ValidateValue(pose.RightFootZ, 10);
            ValidateValue(pose.RightFootPitch, 360); ValidateValue(pose.RightFootYaw, 360); ValidateValue(pose.RightFootRoll, 360);
            ValidateValue(pose.LeftFootX, 10); ValidateValue(pose.LeftFootY, 10); ValidateValue(pose.LeftFootZ, 10);
            ValidateValue(pose.LeftFootPitch, 360); ValidateValue(pose.LeftFootYaw, 360); ValidateValue(pose.LeftFootRoll, 360);
        }

        private static bool ValidIdentity(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);

        private static void ValidateValue(float value, float limit)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidDataException("Non-finite seat-pose value.");
            if (Math.Abs(value) > limit) throw new InvalidDataException("Seat-pose value outside supported range.");
        }
    }
}
