using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Shared physical computer stages and handset occupancy. Native camera/control/saving
    // effects run only on the user of the furniture, never on an observing player's controller.
    internal sealed class SharedSceneFurniture : IDisposable
    {
        internal const ushort Adapter = 151;
        private sealed class Binding
        {
            internal ulong Id;
            internal Component Native = null!;
            internal uint Owner = ItemLease.Unowned;
            internal bool Pending, SavingRequested, LocalUse, LocalFinishing;
            internal float Seen, Started, PendingAt;
            internal int Ticks;
            internal byte[] Original = Array.Empty<byte>();
        }
        private static readonly FieldInfo ComputerState = AccessTools.Field(typeof(Furniture_Computer), "_State");
        private static readonly FieldInfo Screen = AccessTools.Field(typeof(Furniture_Computer), "Mat_Screen");
        private static readonly FieldInfo ComputerLight = AccessTools.Field(typeof(Furniture_Computer), "Mat_Light_Computer");
        private static readonly FieldInfo MonitorLight = AccessTools.Field(typeof(Furniture_Computer), "Mat_Light_Monitor");
        private static readonly FieldInfo Handset = AccessTools.Field(typeof(Furniture_Phone), "Apareille");
        private static readonly MethodInfo UpdateScreen = AccessTools.Method(typeof(Furniture_Computer), "UpdateScreen");
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<Component, Binding> byNative = new Dictionary<Component, Binding>();
        private readonly Dictionary<uint, (PlayerPose Pose, float At)> poses = new Dictionary<uint, (PlayerPose, float)>();
        private readonly bool host;
        private readonly Gameplay gameplay;
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool applying;
        private float next;
        internal SharedSceneFurniture(bool host, Gameplay gameplay, NativeEntityRegistry registry)
        {
            this.host = host; this.gameplay = gameplay;
            var natives = UnityEngine.Object.FindObjectsOfType<Furniture_Computer>(true).Cast<Component>()
                .Concat(UnityEngine.Object.FindObjectsOfType<Furniture_Phone>(true));
            foreach (var native in natives)
            {
                if (!native.gameObject.scene.IsValid()) continue;
                string path = SharedBuildingDoors.StablePath(native.transform) + "/scene-furniture-v1/" + native.GetType().Name;
                var b = new Binding { Id = StaticId.Hash(path), Native = native };
                b.Original = Capture(b); bindings.Add(b.Id, b); byNative.Add(native, b);
                registry.Bind(b.Id, Adapter, native.gameObject, path, true, EntityPersistence.SessionOnly);
            }
        }
        internal IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(b => new WorldRecord(b.Id, 1, Adapter, b.Original));
        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += Pose; session.PeerLeft += Left;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed; channel.CommandRequested += Command; channel.ReplyReceived += Reply;
        }
        private void Pose(uint peer, PlayerPose pose) => poses[peer] = (pose, Time.unscaledTime);
        private void Left(uint peer)
        {
            poses.Remove(peer);
            if (host) foreach (var b in bindings.Values.Where(b => b.Owner == peer)) Release(b);
        }
        private bool Live => session?.IsRunning == true && channel?.FullySynchronized == true;
        private bool Blocked => !Live || MonMPPlugin.Instance?.World?.SaveGate.Active == true ||
            SharedPortableConsoleCharger.Active?.ConsoleSaveInProgress == true;
        internal uint ComputerUser => bindings.Values.FirstOrDefault(b => b.Owner != ItemLease.Unowned &&
            b.Native is Furniture_Computer pc && State(pc) == Computer_State.isSaving)?.Owner ?? ItemLease.Unowned;
        private static Computer_State State(Furniture_Computer pc) => (Computer_State)ComputerState.GetValue(pc);
        private static bool HasPower(Furniture_Computer pc) => pc.Get_House_Location != House_Location.Roulotte ||
            pc.Get_HomeGenerator?.Get_isEngineRunning == true;
        private bool ContinueSaving(Binding b, Furniture_Computer pc)
        {
            if (State(pc) == Computer_State.isSaving && HasPower(pc)) return true;
            // A native shutdown revokes this use immediately. In-flight screen frames and
            // save ticks must not restore the old saving stage or restart an unpowered PC.
            Release(b);
            return false;
        }
        internal bool AllowPower(Furniture_Computer pc)
        {
            if (applying || !byNative.TryGetValue(pc, out var b) || session?.IsRunning != true) return true;
            if (host) return true;
            if (!Blocked) channel!.Request(b.Id, new byte[] { 0 }, true);
            return false;
        }
        internal bool AllowUse(Furniture_Computer pc)
        {
            if (applying || !byNative.TryGetValue(pc, out var b) || session?.IsRunning != true) return true;
            if (bindings.Values.Any(other => other != b && other.Native is Furniture_Computer && other.Owner != ItemLease.Unowned))
            { Error("Someone is already using a computer."); return false; }
            if (b.Owner != ItemLease.Unowned && b.Owner != session.LocalPeerId)
            { Error("Someone is already using this computer."); return false; }
            // Leave native failure messages intact; request authority only for a usable computer.
            if (State(pc) != Computer_State.isOn || gameplay.Get_PlayerState != PlayerState.InFurnitureChair ||
                (gameplay.Player.transform.position - pc.transform.position).sqrMagnitude > 1f) return true;
            if (host) { b.Owner = 0; b.Started = Time.unscaledTime; b.LocalUse = true; return true; }
            if (!Blocked && !b.Pending)
            { b.Pending = true; b.PendingAt = Time.unscaledTime; channel!.Request(b.Id, new byte[] { 1 }, true); }
            return false;
        }
        internal bool AllowTake(Furniture_Phone phone)
        {
            if (applying || !byNative.TryGetValue(phone, out var b) || session?.IsRunning != true) return true;
            if (b.Owner != ItemLease.Unowned)
            { if (b.Owner != session.LocalPeerId) Error("Someone is already using this phone."); return false; }
            if (host) { b.Owner = 0; b.LocalUse = true; return true; }
            if (!Blocked && !b.Pending)
            { b.Pending = true; b.PendingAt = Time.unscaledTime; channel!.Request(b.Id, new byte[] { 1 }, true); }
            return false;
        }
        internal bool NativeSave(House_Location location)
        {
            if (host || !Live) return false;
            var b = bindings.Values.FirstOrDefault(b => b.LocalUse && b.Native is Furniture_Computer pc && pc.Get_House_Location == location);
            if (b == null) return false;
            channel!.Request(b.Id, new byte[] { 3 }, true); return true;
        }
        internal bool AllowSaveTick()
        {
            if (host || !Live) return true;
            var b = bindings.Values.FirstOrDefault(b => b.LocalUse && b.Native is Furniture_Computer pc && State(pc) == Computer_State.isSaving);
            if (b == null) return true;
            channel!.Request(b.Id, new byte[] { 2 }, true); return false;
        }
        private bool Near(uint peer, Binding b, float range, bool seated = false)
        {
            return poses.TryGetValue(peer, out var observed) && Time.unscaledTime - observed.At <= 2f &&
                observed.Pose.Rest == PlayerRest.None && !observed.Pose.Spectating &&
                (observed.Pose.State == (byte)PlayerState.InFurnitureChair || observed.Pose.State == (byte)PlayerState.Walking &&
                    (!seated || MonMPPlugin.Instance?.World?.FurnitureSeats.PeerSeatedNear(peer, b.Native.transform.position, 2.5f) == true)) &&
                (new Vector3(observed.Pose.X, observed.Pose.Y + (observed.Pose.Crouched ? 1.05f : 1.6f), observed.Pose.Z) - b.Native.transform.position).sqrMagnitude <= range * range;
        }
        [WorldCommandAdapters(Adapter)]
        private void Command(uint peer, ulong id, byte[] data)
        {
            if (!host || !Live || peer == 0 || data.Length == 0 || !bindings.TryGetValue(id, out var b)) return;
            byte action = data[0];
            if (b.Owner == peer && b.Native is Furniture_Computer current && !ContinueSaving(b, current)) return;
            if (action == 5 && data.Length == 1) { if (b.Owner == peer) Release(b); return; }
            if (action == 4 && b.Native is Furniture_Phone && b.Owner == peer && data.Length == 1 && Near(peer, b, 2f))
            { b.Seen = Time.unscaledTime; return; }
            if (action == 4 && b.Owner == peer && data.Length > 1)
            {
                if (!Near(peer, b, 2f)) return;
                if (b.Native is Furniture_Computer)
                {
                    var state = data.Skip(1).ToArray();
                    if (!ValidComputer(state) || BitConverter.ToUInt32(state, 1) != peer || state[5] != (byte)Computer_State.isSaving) return;
                    Apply(b, state, false); channel!.Publish(id, state);
                }
                b.Seen = Time.unscaledTime; return;
            }
            if (data.Length != 1 || !Near(peer, b, action == 1 ? 1.5f : 2.5f, action == 1 && b.Native is Furniture_Computer)) return;
            if (action == 0 && b.Native is Furniture_Computer power && !Blocked)
            {
                Patches.RemoteActionScope.LastError = null;
                using (Patches.RemoteActionScope.Enter()) power.PowerOn();
                if (Patches.RemoteActionScope.LastError is string error) ReplyError(peer, id, error);
                channel!.Publish(id, Capture(b)); return;
            }
            if (action == 1)
            {
                if (Blocked || b.Owner != ItemLease.Unowned || bindings.Values.Any(other => other.Owner == peer ||
                    b.Native is Furniture_Computer && other.Native is Furniture_Computer && other.Owner != ItemLease.Unowned) ||
                    b.Native is Furniture_Computer computer && (State(computer) != Computer_State.isOn || !HasPower(computer)))
                { ReplyError(peer, id, "This furniture is busy or unavailable."); return; }
                b.Owner = peer; b.Seen = b.Started = Time.unscaledTime; b.Ticks = 0; b.SavingRequested = false;
                if (b.Native is Furniture_Computer pc)
                { ComputerState.SetValue(pc, Computer_State.isSaving); UpdateScreen.Invoke(pc, new object[] { 5 }); pc.GetComponent<SOUND_Computer>()?.StartWebConnection(); }
                else if (Handset.GetValue(b.Native) is GameObject handset) handset.SetActive(false);
                channel!.Publish(id, Capture(b)); channel.Reply(peer, id, new byte[] { 1 }); return;
            }
            if (b.Owner != peer || b.Native is not Furniture_Computer saving) return;
            b.Seen = Time.unscaledTime;
            if (action == 2 && !Blocked && !b.SavingRequested && b.Ticks < 240 && Time.unscaledTime - b.Started >= 10.5f &&
                b.Ticks < Mathf.FloorToInt((Time.unscaledTime - b.Started - 10.5f) * 45f))
            { b.Ticks++; Singleton<TickSystem>.i.AddOneTickFromSave(); }
            if (action == 3 && !Blocked && !b.SavingRequested && b.Ticks >= 160 && Time.unscaledTime - b.Started >= 16f)
            { b.SavingRequested = true; MonMPPlugin.Instance?.World?.Saves?.Request(saving.Get_House_Location); }
        }
        private void ReplyError(uint peer, ulong id, string error) => channel!.Reply(peer, id, NativeErrorReply.Encode(error));
        private static void Error(string text) => Singleton<UI_ErrorMessage>.i?.Show(text);
        private void Reply(ulong id, byte[] data)
        {
            if (host || !bindings.TryGetValue(id, out var b) || data.Length == 0) return;
            if (NativeErrorReply.TryDecode(data, out string error)) { b.Pending = false; Error(error); return; }
            if (data.Length != 1 || data[0] != 1 || !b.Pending) return;
            b.Pending = false; b.Owner = session!.LocalPeerId; b.LocalUse = true; b.LocalFinishing = false; applying = true;
            try
            {
                if (b.Native is Furniture_Computer pc) { ComputerState.SetValue(pc, Computer_State.isOn); pc.EnterSaveMode(); }
                else if (b.Native is Furniture_Phone phone) phone.TakeApareille();
            }
            finally { applying = false; }
        }
        private static void MaterialWrite(BinaryWriter w, Material? material)
        {
            w.Write(material != null && material.IsKeywordEnabled("_EMISSION"));
            Color color = material != null ? material.GetColor("_EmissionColor") : Color.black;
            w.Write(color.r); w.Write(color.g); w.Write(color.b); w.Write(color.a);
        }
        private static void MaterialRead(BinaryReader r, Material? material)
        {
            bool emission = r.ReadBoolean(); var color = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (material == null) return;
            if (emission) material.EnableKeyword("_EMISSION"); else material.DisableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", color);
        }
        private static byte[] Capture(Binding b)
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write((byte)(b.Native is Furniture_Computer ? 1 : 2)); w.Write(b.Owner);
            if (b.Native is Furniture_Computer pc)
            {
                w.Write((byte)State(pc));
                var screen = Screen.GetValue(pc) as Material;
                var offset = screen != null ? screen.GetTextureOffset("_MainTex") : Vector2.zero;
                w.Write(offset.x); w.Write(offset.y);
                MaterialWrite(w, screen); MaterialWrite(w, ComputerLight.GetValue(pc) as Material); MaterialWrite(w, MonitorLight.GetValue(pc) as Material);
            }
            return stream.ToArray();
        }
        private static bool ValidComputer(byte[] data)
        {
            if (data.Length != 65 || data[0] != 1 || data[5] > 4) return false;
            for (int i = 6; i < 14; i += 4)
            { float value = BitConverter.ToSingle(data, i); if (float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value) > 8) return false; }
            for (int i = 14; i < data.Length; i += 17)
                for (int j = 1; j <= 13; j += 4)
                { float value = BitConverter.ToSingle(data, i + j); if (float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value) > 128) return false; }
            return true;
        }
        private void Apply(Binding b, byte[] data, bool audio = true)
        {
            if (b.Native is Furniture_Computer && !ValidComputer(data) || b.Native is Furniture_Phone && (data.Length != 5 || data[0] != 2)) return;
            using var r = new BinaryReader(new MemoryStream(data, false)); r.ReadByte();
            uint owner = r.ReadUInt32();
            // The native zoom-out can finish before the host receives release. Ignore the
            // last cached saving frame until release is acknowledged, including its modem audio.
            if (!host && b.Native is Furniture_Computer && b.LocalFinishing && owner == session?.LocalPeerId) return;
            if (owner != session?.LocalPeerId) b.LocalFinishing = false;
            if (!host && b.LocalUse && owner != session?.LocalPeerId)
            {
                b.LocalUse = false;
                if (b.Native is Furniture_Phone localPhone) localPhone.PutApareilleBack();
            }
            b.Owner = owner;
            if (b.Native is Furniture_Phone)
            { if (!b.LocalUse && Handset.GetValue(b.Native) is GameObject handset) handset.SetActive(owner == ItemLease.Unowned); return; }
            var pc = (Furniture_Computer)b.Native;
            var state = (Computer_State)r.ReadByte();
            var previous = State(pc);
            var previousScreen = Screen.GetValue(pc) as Material;
            Vector2 previousOffset = previousScreen != null ? previousScreen.GetTextureOffset("_MainTex") : Vector2.zero;
            // Local native SaveMode owns this player's camera and controls until its final zoom-out.
            if (!host && b.LocalUse && owner == session?.LocalPeerId) return;
            var offset = new Vector2(r.ReadSingle(), r.ReadSingle());
            ComputerState.SetValue(pc, state);
            var screen = Screen.GetValue(pc) as Material; screen?.SetTextureOffset("_MainTex", offset);
            MaterialRead(r, screen); MaterialRead(r, ComputerLight.GetValue(pc) as Material); MaterialRead(r, MonitorLight.GetValue(pc) as Material);
            int frame = Mathf.RoundToInt(offset.x / 0.1366f) + 7 * Mathf.RoundToInt(-offset.y / 0.1387f);
            int previousFrame = Mathf.RoundToInt(previousOffset.x / 0.1366f) + 7 * Mathf.RoundToInt(-previousOffset.y / 0.1387f);
            if (state == Computer_State.isSaving && previousFrame == 5 && frame >= 6)
                pc.GetComponent<SOUND_Computer>()?.KeyboardMusicWhileSaving();
            if (!audio || previous == state || b.Pending && owner == session?.LocalPeerId) return;
            var sound = pc.GetComponent<SOUND_Computer>();
            if (sound == null) return;
            if (state == Computer_State.isBooting) sound.StartComputer();
            else if (state == Computer_State.isOn) sound.ComputerLoop_Start();
            else if (state == Computer_State.isShutingDown) { sound.StopComputer(); sound.ComputerLoop_Stop(); }
            else if (state == Computer_State.isOff) sound.ForcedShutdown();
            else if (state == Computer_State.isSaving) sound.StartWebConnection();
        }
        private void Release(Binding b)
        {
            b.Owner = ItemLease.Unowned; b.LocalUse = false;
            if (b.Native is Furniture_Computer pc)
            {
                if (!HasPower(pc)) pc.ForcedShutdown();
                else if (State(pc) == Computer_State.isSaving)
                { ComputerState.SetValue(pc, Computer_State.isOn); UpdateScreen.Invoke(pc, new object[] { 4 }); pc.GetComponent<SOUND_Computer>()?.ComputerLoop_Start(); }
            }
            if (b.Native is Furniture_Phone && Handset.GetValue(b.Native) is GameObject handset) handset.SetActive(true);
            channel?.Publish(b.Id, Capture(b));
        }
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records) { if (!host) foreach (var record in records.Values) Changed(record); }
        private void Changed(WorldRecord record) { if (!host && record.Adapter == Adapter && bindings.TryGetValue(record.Id, out var b)) Apply(b, record.Payload); }
        internal void Update()
        {
            if (!Live || Time.unscaledTime < next) return;
            next = Time.unscaledTime + 0.2f;
            foreach (var b in bindings.Values)
            {
                if (b.Pending && Time.unscaledTime - b.PendingAt > 5f) { b.Pending = false; Error("Furniture use confirmation timed out."); }
                if (host && b.Owner != ItemLease.Unowned && b.Native is Furniture_Computer activeComputer)
                    ContinueSaving(b, activeComputer);
                bool inUse = b.Native is Furniture_Computer pc ? State(pc) == Computer_State.isSaving :
                    Handset.GetValue(b.Native) is GameObject handset && !handset.activeSelf;
                if (b.LocalUse)
                {
                    if (!inUse)
                    { if (host) Release(b); else { channel!.Request(b.Id, new byte[] { 5 }, true); b.LocalUse = false; b.LocalFinishing = b.Native is Furniture_Computer; } }
                    else if (!host)
                        channel!.Request(b.Id, new byte[] { 4 }.Concat(b.Native is Furniture_Computer ? Capture(b) : Array.Empty<byte>()).ToArray(), true);
                }
                if (host)
                {
                    if (b.Owner != 0 && b.Owner != ItemLease.Unowned && Time.unscaledTime - b.Seen > 5f) Release(b);
                    channel!.Publish(b.Id, Capture(b));
                }
                else if (!b.LocalUse && channel!.Records.TryGetValue(b.Id, out var record)) Apply(b, record.Payload);
            }
        }
        public void Dispose()
        {
            if (session != null) { session.PoseReceived -= Pose; session.PeerLeft -= Left; }
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; channel.ReplyReceived -= Reply; }
            foreach (var b in bindings.Values)
            {
                if (b.LocalUse && b.Native is Furniture_Phone phone) phone.PutApareilleBack();
                if (!host) { b.LocalUse = false; Apply(b, b.Original); }
            }
            Patches.GuestPhoneCall.Phone = null; Patches.GuestPhoneCall.MotherRequestId = Guid.Empty;
            channel = null; session = null; poses.Clear();
        }
    }
}
