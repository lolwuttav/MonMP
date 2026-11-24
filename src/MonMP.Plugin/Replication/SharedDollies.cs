using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // The dolly trailer's ramp and its strapped-down car. Natively both are local: a guest's ramp opened
    // only for the guest, and a car strapped on by the host had no straps on the guest's dolly. The host
    // runs the native switches (a guest's click is sent to it) and shares one record for every dolly:
    // whether its ramp is open and which car it holds. Guests replay the native ramp travel and show
    // the straps; the car stays jointed to the dolly only where the host simulates it.
    internal sealed class SharedDollies : IDisposable
    {
        public const ushort Adapter = 107;
        private static readonly ulong Entity = StaticId.Hash("shared-dollies/v1");
        private const float Reach = 5f;
        private enum Action : byte { Ramp = 1, Attach = 2 }
        private sealed class Binding
        {
            public ulong Id;
            public Vehicle_Trailer_Dolly Dolly = null!;
            public bool RampOpen;
            public ulong Car;
            public float LastTimer;
            public bool Straps;
            public bool Snapped;
            public bool RemoteStateKnown;
            public bool AwaitingSnapshot;
            public bool StrapsPending;
            public bool RestoreCaptured;
            public bool OriginalRampRunning;
            public float OriginalRampTimer;
            public Vector3 OriginalRampPosition;
            public Quaternion OriginalRampRotation;
            public bool OriginalStrapLeftActive;
            public bool OriginalStrapRightActive;
            public Vector3 OriginalStrapLeftPosition;
            public Vector3 OriginalStrapRightPosition;
            public Vector3 OriginalStrapLeftScale;
            public Vector3 OriginalStrapRightScale;
            public Quaternion OriginalStrapLeftRotation;
            public Quaternion OriginalStrapRightRotation;
            public Coroutine? RemoteRampCoroutine;
        }
        private static readonly FieldInfo? SwitchRamp = AccessTools.Field(typeof(Vehicle_Trailer_Dolly), "Switch_Ramp");
        private static readonly FieldInfo? SwitchAttach = AccessTools.Field(typeof(Vehicle_Trailer_Dolly), "Switch_AttachVehicle");
        private static readonly FieldInfo? RampOpenLocation = AccessTools.Field(typeof(Vehicle_Trailer_Dolly), "RampOpenLocation");
        private static readonly FieldInfo? RampOriginalPosition = AccessTools.Field(typeof(Vehicle_Trailer_Dolly), "Ramp_OriginalPosition");
        private static readonly FieldInfo? RampOriginalRotation = AccessTools.Field(typeof(Vehicle_Trailer_Dolly), "Ramp_OriginalRotation");
        private static readonly FieldInfo? RampTimer = AccessTools.Field(typeof(Vehicle_Trailer_Dolly), "Ramp_Timer");
        private static readonly FieldInfo? RampTotal = AccessTools.Field(typeof(Vehicle_Trailer_Dolly), "Ramp_Total");
        private static readonly FieldInfo? RampRunning = AccessTools.Field(typeof(Vehicle_Trailer_Dolly), "Coroutine_Ramp");
        private static readonly FieldInfo? StrapLeft = AccessTools.Field(typeof(Vehicle_Trailer_Dolly), "WheelAttach_Left");
        private static readonly FieldInfo? StrapRight = AccessTools.Field(typeof(Vehicle_Trailer_Dolly), "WheelAttach_Right");
        private static readonly FieldInfo? WheelsDetection = AccessTools.Field(typeof(Vehicle_Trailer_Dolly), "WheelsDetection");
        private static readonly MethodInfo? DetectMethod = AccessTools.Method(typeof(Vehicle_Trailer_Dolly), "Detect");
        private static readonly MethodInfo? OpenCloseRamp = AccessTools.Method(typeof(Vehicle_Trailer_Dolly), "OpenCloseRamp");
        private static bool Supported => SwitchRamp != null && SwitchAttach != null && RampOpenLocation != null && RampOriginalPosition != null &&
            RampOriginalRotation != null && RampTimer != null && RampTotal != null && RampRunning != null && StrapLeft != null && StrapRight != null &&
            WheelsDetection != null && DetectMethod != null && OpenCloseRamp != null;

        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly SharedVehicles vehicles;
        private readonly SharedTrailers trailers;
        private readonly bool host;
        private NetworkSession? session;
        private WorldChannel? channel;
        private byte[]? lastPublished;
        private bool executing;
        private bool disposed;
        private int boundCount = -1;
        private float nextScan, nextCapture;

        public SharedDollies(bool host, SharedVehicles vehicles, SharedTrailers trailers)
        {
            this.host = host; this.vehicles = vehicles; this.trailers = trailers;
            Scan(out _);
        }

        internal IEnumerable<WorldRecord> InitialRecords() { yield return new WorldRecord(Entity, 1, Adapter, Encode()); }

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed; channel.CommandRequested += Command;
            channel.ReplyReceived += Reply;
        }
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer) => presence.Remove(peer);
        private bool Near(uint peer, Vector3 at) =>
            presence.TryGetValue(peer, out var seen) && Time.unscaledTime - seen.Time <= 2f &&
            (new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z) - at).sqrMagnitude <= Reach * Reach;

        // Purchased dollies are bound later than construction. A dolly has no driver seat, so it has a
        // trailer id, never a vehicle id: looking it up among vehicles bound nothing (#213).
        private HashSet<ulong> Scan(out HashSet<ulong> discovered)
        {
            discovered = new HashSet<ulong>();
            if (!Supported) return new HashSet<ulong>();
            var controllers = trailers.BoundControllers.ToList();
            if (controllers.Count != boundCount)
            {
                boundCount = controllers.Count;
                foreach (var controller in controllers)
                {
                    if (controller == null) continue;
                    var dolly = controller.GetComponent<Vehicle_Trailer_Dolly>();
                    ulong id = trailers.IdOf(controller);
                    if (dolly == null || id == 0 || bindings.ContainsKey(id)) continue;
                    bindings.Add(id, new Binding { Id = id, Dolly = dolly, LastTimer = Timer(dolly), AwaitingSnapshot = !host });
                    discovered.Add(id);
                }
            }

            // A guest may discover a purchased dolly after its snapshot/delta has already
            // committed. Hydrate only bindings that have not received state yet, from the
            // receiver's current record rather than the event payload that caused a scan.
            if (host || channel == null || !bindings.Values.Any(binding => binding.AwaitingSnapshot) ||
                !channel.Records.TryGetValue(Entity, out var record)) return new HashSet<ulong>();
            if (record.Adapter != Adapter) throw new InvalidDataException("Invalid dolly state adapter.");
            var latest = Decode(record.Payload);
            var adopted = new HashSet<ulong>();
            foreach (var binding in bindings.Values)
            {
                if (!binding.AwaitingSnapshot || !latest.TryGetValue(binding.Id, out var state)) continue;
                ApplyState(binding, state.Open, state.Car, snap: true);
                adopted.Add(binding.Id);
            }
            return adopted;
        }

        private static float Timer(Vehicle_Trailer_Dolly dolly) => (float)RampTimer!.GetValue(dolly);
        private static bool Running(Vehicle_Trailer_Dolly dolly) => (bool)RampRunning!.GetValue(dolly);
        // The dolly's Start records the ramp's stowed pose; before it runs (a dolly spawned or activated
        // after the record arrived) that pose is still all zeros. Snapping or replaying the ramp then
        // moved it to the dolly's origin near the ground, and Start took that as the stowed pose (#213).
        private static bool Started(Vehicle_Trailer_Dolly dolly)
        {
            var stowed = (Quaternion)RampOriginalRotation!.GetValue(dolly);
            return dolly.isActiveAndEnabled && (stowed.x != 0f || stowed.y != 0f || stowed.z != 0f || stowed.w != 0f);
        }

        private byte[] Encode()
        {
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream))
            {
                var list = bindings.Values.Where(b => b.Dolly != null).OrderBy(b => b.Id).ToList();
                w.Write((ushort)list.Count);
                foreach (var b in list) { w.Write(b.Id); w.Write((byte)(b.RampOpen ? 1 : 0)); w.Write(b.Car); }
            }
            return stream.ToArray();
        }

        // A click on a dolly switch; true when MonMP handled it (the native listener must not run here).
        internal bool Intercept(Furniture_SwitchButton button)
        {
            if (executing || session?.IsRunning != true || host || channel?.Ready != true || button == null) return false;
            foreach (var b in bindings.Values)
            {
                if (b.Dolly == null) continue;
                Action action;
                if (ReferenceEquals(SwitchRamp!.GetValue(b.Dolly), button)) action = Action.Ramp;
                else if (ReferenceEquals(SwitchAttach!.GetValue(b.Dolly), button)) action = Action.Attach;
                else continue;
                // What the guest's copy can already tell: the same native refusals, shown locally.
                if (action == Action.Ramp && b.Car != 0)
                { Singleton<UI_ErrorMessage>.i.Show(TEXT.GetText("ERROR_TrailerDolly_CantOpenRampACarIsThere")); return true; }
                if (action == Action.Attach && b.Car == 0 && (Running(b.Dolly) || Timer(b.Dolly) > 0f))
                { Singleton<UI_ErrorMessage>.i.Show(TEXT.GetText("ERROR_TrailerDolly_CantAttachCarBecauseRampIsOpen")); return true; }
                try { Singleton<SOUND_Manager>.i.PlaySound_SwitchOn(); } catch (Exception) { }
                using var stream = new MemoryStream();
                using (var w = new BinaryWriter(stream)) { w.Write(b.Id); w.Write((byte)action); }
                channel.Request(Entity, stream.ToArray(), true);
                return true;
            }
            return false;
        }

        [MonMP.Core.World.WorldCommandAdapters(SharedDollies.Adapter)]

        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || peer == 0 || id != Entity || payload.Length != 9) return;
            ulong dollyId = BitConverter.ToUInt64(payload, 0);
            var action = (Action)payload[8];
            if (!bindings.TryGetValue(dollyId, out var b) || b.Dolly == null || !Near(peer, b.Dolly.transform.position)) return;
            executing = true;
            Patches.RemoteActionScope.LastError = null;
            try
            {
                // The guest's refusal messages are the guest's own (it checks what it can before sending).
                using var quiet = Patches.RemoteActionScope.Enter();
                using var sound = SpatialSounds.AtPeer(peer);
                if (action == Action.Ramp) b.Dolly.Clicked_Ramp();
                else if (action == Action.Attach) DetectMethod!.Invoke(b.Dolly, null);
            }
            catch (Exception ex) { Debug.LogWarning("MonMP dolly command failed: " + ex.Message); }
            finally { executing = false; }
            if (Patches.RemoteActionScope.LastError is string error)
            {
                // Reply bodies have a 128-byte channel limit. Send a bounded native key and
                // localize on the requester instead of forwarding the host's translated text.
                for (byte kind = 1; kind <= 4; kind++)
                    if (error == TEXT.GetText(ErrorKey(kind)!))
                    { channel!.Reply(peer, Entity, new byte[] { 0xD1, kind }); break; }
            }
            Capture(b);
            Publish();
        }

        private void Reply(ulong id, byte[] payload)
        {
            if (!host && id == Entity && payload.Length == 2 && payload[0] == 0xD1 && ErrorKey(payload[1]) is string key)
                Singleton<UI_ErrorMessage>.i?.Show(TEXT.GetText(key));
        }

        private static string? ErrorKey(byte kind)
        {
            return kind switch
            {
                1 => "ERROR_TrailerDolly_CantAttachCarBecauseRampIsOpen",
                2 => "ERROR_TrailerDolly_WheelsNotDetectedOnTheDolly",
                3 => "ERROR_TrailerDolly_NoVehicleDetectedOnTheDolly",
                4 => "ERROR_TrailerDolly_CantOpenRampACarIsThere",
                _ => null
            };
        }

        private void Capture(Binding b)
        {
            float timer = Timer(b.Dolly);
            // The ramp's direction while it moves; its resting side otherwise.
            b.RampOpen = Running(b.Dolly) && !Mathf.Approximately(timer, b.LastTimer) ? timer > b.LastTimer : timer > 0f;
            b.LastTimer = timer;
            var car = b.Dolly.isACarAttached ? b.Dolly.GetCarAttached : null;
            b.Car = car != null ? vehicles.EntityOf(car) : 0;
        }

        private void Publish()
        {
            if (channel == null) return;
            var payload = Encode();
            if (lastPublished != null && payload.AsSpan().SequenceEqual(lastPublished)) return;
            lastPublished = payload;
            channel.Publish(Entity, payload);
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host || !records.TryGetValue(Entity, out var record)) return;
            Apply(record.Payload, true);
        }
        private void Changed(WorldRecord record)
        {
            if (host || record.Id != Entity) return;
            Apply(record.Payload, false);
        }
        private void Apply(byte[] payload, bool snap)
        {
            var adopted = Scan(out var discovered);
            var states = Decode(payload);
            foreach (var pair in states)
            {
                if (discovered.Contains(pair.Key) || adopted.Contains(pair.Key) ||
                    !bindings.TryGetValue(pair.Key, out var binding) || binding.Dolly == null) continue;
                ApplyState(binding, pair.Value.Open, pair.Value.Car, snap);
            }
        }

        private static Dictionary<ulong, (bool Open, ulong Car)> Decode(byte[] payload)
        {
            try
            {
                using var r = new BinaryReader(new MemoryStream(payload));
                int count = r.ReadUInt16();
                if (count > (r.BaseStream.Length - r.BaseStream.Position) / 17)
                    throw new InvalidDataException("Invalid dolly state.");
                var states = new Dictionary<ulong, (bool Open, ulong Car)>(count);
                for (int i = 0; i < count; i++)
                {
                    ulong id = r.ReadUInt64();
                    byte open = r.ReadByte();
                    ulong car = r.ReadUInt64();
                    if (id == 0 || open > 1 || !states.TryAdd(id, (open != 0, car)))
                        throw new InvalidDataException("Invalid dolly state.");
                }
                if (r.BaseStream.Position != payload.Length) throw new InvalidDataException("Invalid dolly state.");
                return states;
            }
            catch (EndOfStreamException ex)
            {
                throw new InvalidDataException("Invalid dolly state.", ex);
            }
        }

        private void ApplyState(Binding b, bool open, ulong car, bool snap)
        {
            b.RemoteStateKnown = true;
            b.AwaitingSnapshot = false;
            b.RampOpen = open;
            bool carChanged = b.Car != car || snap;
            b.Car = car;
            if (!PrepareGuestPresentation(b))
            {
                if (carChanged) b.StrapsPending = true;
                return;
            }
            if (snap || !b.Snapped) SnapRamp(b);
            if (carChanged) { ShowStraps(b, !snap); b.StrapsPending = false; }
        }

        private bool PrepareGuestPresentation(Binding b)
        {
            if (!Started(b.Dolly)) return false;
            if (b.RestoreCaptured) return true;
            if (Running(b.Dolly)) return false;
            var switchButton = SwitchRamp?.GetValue(b.Dolly) as Furniture_SwitchButton;
            Transform? switchTransform = switchButton != null ? switchButton.transform : null;
            Transform? ramp = switchTransform != null ? switchTransform.parent : null;
            var left = StrapLeft?.GetValue(b.Dolly) as Transform;
            var right = StrapRight?.GetValue(b.Dolly) as Transform;
            if (ramp == null || left == null || right == null) return false;
            b.OriginalRampTimer = Timer(b.Dolly);
            b.OriginalRampRunning = Running(b.Dolly);
            b.OriginalRampPosition = ramp.localPosition;
            b.OriginalRampRotation = ramp.localRotation;
            b.OriginalStrapLeftActive = left.gameObject.activeSelf;
            b.OriginalStrapRightActive = right.gameObject.activeSelf;
            b.OriginalStrapLeftPosition = left.localPosition;
            b.OriginalStrapRightPosition = right.localPosition;
            b.OriginalStrapLeftScale = left.localScale;
            b.OriginalStrapRightScale = right.localScale;
            b.OriginalStrapLeftRotation = left.localRotation;
            b.OriginalStrapRightRotation = right.localRotation;
            b.RestoreCaptured = true;
            return true;
        }

        private static void SnapRamp(Binding b)
        {
            if (Running(b.Dolly) || !Started(b.Dolly)) return;
            b.Snapped = true;
            float total = (float)RampTotal!.GetValue(b.Dolly);
            RampTimer!.SetValue(b.Dolly, b.RampOpen ? total : 0f);
            var ramp = ((Furniture_SwitchButton)SwitchRamp!.GetValue(b.Dolly)).transform.parent;
            var open = (Transform)RampOpenLocation!.GetValue(b.Dolly);
            ramp.localPosition = b.RampOpen ? open.localPosition : (Vector3)RampOriginalPosition!.GetValue(b.Dolly);
            ramp.localRotation = b.RampOpen ? open.localRotation : (Quaternion)RampOriginalRotation!.GetValue(b.Dolly);
        }

        // The native straps sit under the two wheels on the dolly, scaled to their radius.
        private void ShowStraps(Binding b, bool sound)
        {
            var left = (Transform)StrapLeft!.GetValue(b.Dolly);
            var right = (Transform)StrapRight!.GetValue(b.Dolly);
            var car = b.Car != 0 ? vehicles.VehicleFor(b.Car) : null;
            bool show = car != null;
            if (show)
            {
                var detection = (BoxCollider)WheelsDetection!.GetValue(b.Dolly);
                var wheels = car!.Wheels.Where(w => w?.wheelController != null)
                    .OrderBy(w => (w.wheelController.transform.position - detection.transform.position).sqrMagnitude).Take(2)
                    .OrderBy(w => b.Dolly.transform.InverseTransformPoint(w.wheelController.transform.position).x).ToList();
                if (wheels.Count == 2)
                {
                    Place(b.Dolly.transform, left, wheels[0].wheelController.transform.position, wheels[0].wheelController.radius);
                    Place(b.Dolly.transform, right, wheels[1].wheelController.transform.position, wheels[1].wheelController.radius);
                }
                else show = false;
            }
            left.gameObject.SetActive(show); right.gameObject.SetActive(show);
            if (sound && show != b.Straps)
                try
                {
                    using (SpatialSounds.At(b.Dolly.transform.position))
                    { if (show) Singleton<SOUND_Manager>.i.PlaySound_PartInstalled(); else Singleton<SOUND_Manager>.i.PlaySound_PartRemoved(); }
                }
                catch (Exception) { }
            b.Straps = show;
        }
        private static void Place(Transform dolly, Transform strap, Vector3 wheel, float radius)
        {
            var local = strap.localPosition;
            strap.localPosition = new Vector3(dolly.InverseTransformPoint(wheel).x, local.y, local.z);
            strap.localScale = new Vector3(1f, radius / 0.315f, 1f);
        }

        public void Update()
        {
            if (session?.IsRunning != true || channel?.Ready != true) return;
            float now = Time.unscaledTime;
            bool scanned = false;
            if (now >= nextScan) { nextScan = now + 2f; Scan(out _); scanned = true; }
            if (bindings.Count == 0) return;
            if (host)
            {
                if (now < nextCapture) return;
                nextCapture = now + 0.1f;
                foreach (var b in bindings.Values) if (b.Dolly != null) Capture(b);
                Publish();
                return;
            }
            // Replay the native ramp travel toward the host's side; a move already under way finishes first.
            foreach (var b in bindings.Values)
            {
                if (b.Dolly == null) continue;
                if (b.AwaitingSnapshot || !b.RemoteStateKnown) continue;
                if (b.RemoteRampCoroutine != null && !Running(b.Dolly)) b.RemoteRampCoroutine = null;
                if (!PrepareGuestPresentation(b)) continue;
                if (b.StrapsPending && Started(b.Dolly))
                {
                    ShowStraps(b, false);
                    b.StrapsPending = false;
                }
                if (!b.Snapped) { SnapRamp(b); continue; }
                if (Running(b.Dolly) || b.RampOpen == Timer(b.Dolly) > 0f || b.Car != 0 && b.RampOpen) continue;
                try { b.RemoteRampCoroutine = b.Dolly.StartCoroutine(ReplayRamp(b)); }
                catch (Exception ex) { Debug.LogWarning("MonMP dolly ramp replay failed: " + ex.Message); }
            }
            // A car bound after its record arrived (a late vehicle binding).
            if (scanned) foreach (var b in bindings.Values)
                if (b.Dolly != null && b.RestoreCaptured && b.Car != 0 && !b.Straps && Started(b.Dolly)) ShowStraps(b, false);
        }

        private IEnumerator ReplayRamp(Binding b)
        {
            try { yield return (IEnumerator)OpenCloseRamp!.Invoke(b.Dolly, null); }
            finally { b.RemoteRampCoroutine = null; }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; channel.ReplyReceived -= Reply; }
            if (!host)
                foreach (var b in bindings.Values)
                {
                    if (b.Dolly == null) continue;
                    bool ownRampRunning = b.RemoteRampCoroutine != null && Running(b.Dolly);
                    bool ownRampStopped = false;
                    if (ownRampRunning)
                    {
                        try { b.Dolly.StopCoroutine(b.RemoteRampCoroutine!); ownRampStopped = true; }
                        catch (Exception ex) { Debug.LogWarning("MonMP dolly ramp cleanup failed: " + ex.Message); }
                        finally { b.RemoteRampCoroutine = null; }
                    }
                    bool unownedRampRunning = Running(b.Dolly) && !ownRampStopped;
                    if (b.RestoreCaptured)
                    {
                        if (!unownedRampRunning)
                        {
                            RampTimer?.SetValue(b.Dolly, b.OriginalRampTimer);
                            var switchButton = SwitchRamp?.GetValue(b.Dolly) as Furniture_SwitchButton;
                            Transform? switchTransform = switchButton != null ? switchButton.transform : null;
                            Transform? ramp = switchTransform != null ? switchTransform.parent : null;
                            if (ramp != null)
                            {
                                ramp.localPosition = b.OriginalRampPosition;
                                ramp.localRotation = b.OriginalRampRotation;
                            }
                            if ((ownRampStopped || !Running(b.Dolly)) && RampRunning != null)
                                RampRunning.SetValue(b.Dolly, b.OriginalRampRunning);
                        }
                        var left = StrapLeft?.GetValue(b.Dolly) as Transform;
                        var right = StrapRight?.GetValue(b.Dolly) as Transform;
                        if (left != null)
                        {
                            left.localPosition = b.OriginalStrapLeftPosition;
                            left.localScale = b.OriginalStrapLeftScale;
                            left.localRotation = b.OriginalStrapLeftRotation;
                            left.gameObject.SetActive(b.OriginalStrapLeftActive);
                        }
                        if (right != null)
                        {
                            right.localPosition = b.OriginalStrapRightPosition;
                            right.localScale = b.OriginalStrapRightScale;
                            right.localRotation = b.OriginalStrapRightRotation;
                            right.gameObject.SetActive(b.OriginalStrapRightActive);
                        }
                        b.RestoreCaptured = false;
                    }
                }
            session = null; channel = null; presence.Clear();
        }
    }
}
