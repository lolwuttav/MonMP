using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Scene machines whose click is one host transaction over already-shared things: the cannabis
    // grinder and packager (consume buds or pre-rolls, spawn a pack later), the French fry cutter, packager
    // and fryer power button. They were host-only. A guest's
    // click now runs the native click on the host, near the guest; the items it consumes and spawns
    // and the cottage values it changes replicate through their own adapters.
    internal sealed class SharedRemoteClicks : IDisposable
    {
        public const ushort Adapter = 108;
        private const float CommandRange = 5f;
        private const float CommandCooldown = 0.4f;
        private sealed class Binding
        {
            public ulong Id;
            public MonoBehaviour Target = null!;
            public Transform? Anchor;
            public Action Click = null!;
        }
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<MonoBehaviour, Binding> byTarget = new Dictionary<MonoBehaviour, Binding>();
        private readonly Dictionary<(Home_Bunker, byte), Binding> bunkerActions = new Dictionary<(Home_Bunker, byte), Binding>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Dictionary<(uint, ulong), float> lastCommand = new Dictionary<(uint, ulong), float>();
        private readonly bool host;
        internal bool NewActionsBlocked { get; set; }
        private static readonly FieldInfo GrinderWorking = AccessTools.Field(typeof(Furniture_CannabisGrinder), "Coroutine_BuildingRunning");
        private static readonly FieldInfo PackagerWorking = AccessTools.Field(typeof(Furniture_CannabisPackager), "Coroutine_BuildingRunning");
        private static readonly FieldInfo CutterWorking = AccessTools.Field(typeof(Furniture_FrenchFryCutter), "Coroutine_BuildingRunning");
        private static readonly FieldInfo FryPackagerWorking = AccessTools.Field(typeof(Furniture_FrenchFryPackager), "Coroutine_BuildingRunning");
        private static readonly FieldInfo BunkerPackWorking = AccessTools.Field(typeof(Home_Bunker), "isPackMachineRunning");
        private static readonly FieldInfo BunkerFriesWorking = AccessTools.Field(typeof(Home_Bunker), "isFrenchFriesPressMachineRunning");
        private static readonly FieldInfo CompostWorking = AccessTools.Field(typeof(Bunker_CompostMachine), "MachineWorking");
        // These finite native operations debit inputs before their delayed output. Let them
        // finish while collection still runs at normal time, before a save can freeze it.
        internal bool TransactionsSettled => !host || bindings.Values.All(b => ProductionIdle(b.Target));
        internal bool SettleForSave()
        {
            if (!host) return true;
            bool complete = true;
            foreach (var target in bindings.Values.Select(binding => binding.Target).Distinct())
                if (!ProductionIdle(target))
                {
                    if (target is Home_Bunker)
                    {
                        if ((bool)BunkerPackWorking.GetValue(target)!) complete &= NativeSaveProduction.Finish(target, "BuildCannabisPack");
                        if ((bool)BunkerFriesWorking.GetValue(target)!) complete &= NativeSaveProduction.Finish(target, "BuildFrenchFriesPack");
                        complete &= ProductionIdle(target);
                    }
                    else complete &= NativeSaveProduction.Finish(target,
                        target is Bunker_CompostMachine ? "BuildFertilizer" : "BuildPack") && ProductionIdle(target);
                }
            // The native tail creates ordinary items. Discover them before the frozen snapshot.
            MonMPPlugin.Instance?.Items?.DiscoverSoon();
            return complete;
        }
        private static bool ProductionIdle(MonoBehaviour target)
        {
            if (target == null) return true;
            if (target is Furniture_CannabisGrinder) return !(bool)GrinderWorking.GetValue(target)!;
            if (target is Furniture_CannabisPackager) return !(bool)PackagerWorking.GetValue(target)!;
            if (target is Furniture_FrenchFryCutter) return !(bool)CutterWorking.GetValue(target)!;
            if (target is Furniture_FrenchFryPackager) return !(bool)FryPackagerWorking.GetValue(target)!;
            if (target is Home_Bunker) return !(bool)BunkerPackWorking.GetValue(target)! && !(bool)BunkerFriesWorking.GetValue(target)!;
            if (target is Bunker_CompostMachine) return !(bool)CompostWorking.GetValue(target)!;
            return true;
        }
        private NetworkSession? session;
        private WorldChannel? channel;
        internal static SharedRemoteClicks? Active { get; private set; }
        internal static bool RunningForGuest { get; private set; }
        internal static uint ProductionPeer { get; private set; }
        public int Count => bindings.Count;

#if MONMP_DEVELOPMENT
        internal int TestAccepted { get; private set; }
#endif

        // Replies to the requesting guest (#14, #15): a native error the host kept off its own screen,
        // produced by the native click.
        // ReplyDone: the click ran and made its native sound, which the host only hears at the guest.
        private const byte ReplyError = 0, ReplyDone = 2;
        private static readonly AccessTools.FieldRef<Gameplay, PlayerState> PlayerStateOf = AccessTools.FieldRefAccess<Gameplay, PlayerState>("playerState");
        private const float SeatRange = 1.5f;

        public SharedRemoteClicks(bool host, string diagnostics, NativeEntityRegistry registry)
        {
            this.host = host;
            var lines = new List<string>();
            Register<Furniture_CannabisGrinder>("cannabis-grinder", t => t.MouseDown(), registry, lines);
            Register<Furniture_CannabisPackager>("cannabis-packager", t => t.MouseDown(), registry, lines);
            // Cottage collection uses its operation-aware protocol. Do not retain a
            // second one-byte command that can bypass collection replay protection.
            // The fry machines' state is shared by SharedFoodPrep; their clicks were host-only.
            Register<Furniture_FrenchFryCutter>("fry-cutter", t => t.MouseDown(), registry, lines);
            Register<Furniture_FrenchFryPackager>("fry-packager", t => t.MouseDown(), registry, lines);
            Register<Furniture_Fryer>("fryer-power", t => t.PowerOnButton(), registry, lines);
            Register<Bunker_CompostMachine>("bunker-compost", t => t.Button_MakeFertilizer(), registry, lines,
                t => t == SharedBunkerCompost.FindMachine(Singleton<Gameplay>.i));
            RegisterBunker(registry, lines);
            lines.Sort(StringComparer.Ordinal);

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-remote-clicks.tsv"), new[] { "id\tpath" }.Concat(lines));
#endif

            Active = this;
        }

        private void RegisterBunker(NativeEntityRegistry registry, List<string> lines)
        {
            foreach (var bunker in UnityEngine.Object.FindObjectsOfType<Home_Bunker>(true))
            {
                if (bunker == null || !bunker.gameObject.scene.IsValid() || Spawned(bunker.transform)) continue;
                Add(0, "bunker-fries", "SpawnFrenchFriesLocation", bunker.Button_FrenchFriesPress);
                Add(1, "bunker-large", "Packagers_SpawnBigPack", bunker.Button_BigPackMachine);
                Add(2, "bunker-huge-large", "Packagers_SpawnBigPackOnHuge", () => bunker.Button_HugePackMachine(false));
                Add(3, "bunker-huge", "Packagers_SpawnHugePack", () => bunker.Button_HugePackMachine(true));
                void Add(byte action, string kind, string anchorField, Action click)
                {
                    var anchor = AccessTools.Field(typeof(Home_Bunker), anchorField)?.GetValue(bunker) as Transform;
                    if (anchor == null) throw new InvalidOperationException("Unsupported bunker production anchor: " + anchorField);
                    string path = SharedBuildingDoors.StablePath(bunker.transform) + "/remote-click-v1/" + kind;
                    ulong id = StaticId.Hash(path);
                    var binding = new Binding { Id = id, Target = bunker, Anchor = anchor, Click = click };
                    bindings.Add(id, binding); bunkerActions.Add((bunker, action), binding);
                    registry.Bind(id, Adapter, anchor.gameObject, path, true, EntityPersistence.SessionOnly);
                    lines.Add(id.ToString("X16") + "\t" + path);
                }
            }
        }

        internal bool InterceptBunker(Home_Bunker bunker, byte action)
        {
            if (host || session?.IsRunning != true) return false;
            if (NewActionsBlocked || MonMPPlugin.Instance?.World?.SaveGate.Active == true)
            { MonMPPlugin.Instance?.ReportItemUseUnavailable("Wait for the multiplayer save or disconnect to finish."); return true; }
            if (channel?.FullySynchronized != true || !bunkerActions.TryGetValue((bunker, action), out var binding))
            { MonMPPlugin.Instance?.ReportItemUseUnavailable("Wait for the shared bunker before using this machine."); return true; }
            channel.Request(binding.Id, new byte[] { 1 });
            return true;
        }

        private static Vector3 PositionOf(Binding binding) => binding.Anchor != null ? binding.Anchor.position : binding.Target.transform.position;

        private void Register<T>(string kind, Action<T> click, NativeEntityRegistry registry, List<string> lines,
            Func<T, bool>? include = null) where T : MonoBehaviour
        {
            foreach (var target in UnityEngine.Object.FindObjectsOfType<T>(true))
            {
                if (target == null || !target.gameObject.scene.IsValid() || Spawned(target.transform)) continue;
                if (include != null && !include(target)) continue;
                string path = SharedBuildingDoors.StablePath(target.transform) + "/remote-click-v1/" + kind;
                ulong id = StaticId.Hash(path);
                if (bindings.ContainsKey(id)) continue;
                var b = new Binding { Id = id, Target = target, Click = () => click(target) };
                bindings.Add(id, b); byTarget[target] = b;
                registry.Bind(id, Adapter, target.gameObject, path, true, EntityPersistence.SessionOnly);
                lines.Add(id.ToString("X16") + "\t" + path);
            }
        }

        private static bool Spawned(Transform transform)
        {
            for (var current = transform; current != null; current = current.parent)
                if (current.name.Contains("(Clone)") || current.GetComponent<InteractableItems>() != null) return true;
            return false;
        }

        internal IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(b => new WorldRecord(b.Id, 1, Adapter, new byte[] { 0 }));

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.CommandRequested += Command;
            channel.ReplyReceived += Reply;
        }
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer) { presence.Remove(peer); foreach (var key in lastCommand.Keys.Where(k => k.Item1 == peer).ToArray()) lastCommand.Remove(key); }

        // True when the click belongs to a guest and was sent to the host instead of running here.
        internal bool Intercept(MonoBehaviour target)
        {
            if (host || session?.IsRunning != true || target == null || !byTarget.TryGetValue(target, out var b)) return false;
            if (NewActionsBlocked || MonMPPlugin.Instance?.World?.SaveGate.Active == true)
            { MonMPPlugin.Instance?.ReportItemUseUnavailable("Wait for the multiplayer save or disconnect to finish."); return true; }
            if (channel?.Ready != true) { MonMPPlugin.Instance?.ReportItemUseUnavailable("Wait for the shared world before using this machine."); return true; }
            // The native checks on this guest's own player, before the host runs the click (#14, #15).
            var gameplay = Singleton<Gameplay>.i;
            if (target is Furniture_CannabisGrinder || target is Furniture_CannabisPackager)
            {
                string kind = target is Furniture_CannabisGrinder ? "CannabisGrinder" : "CannabisPackager";
                if (gameplay.Get_PlayerState != PlayerState.InFurnitureChair) { ShowError(TEXT.GetText("ERROR_" + kind + "_NeedToBeSitDown")); return true; }
                if (Vector3.Distance(gameplay.Player.transform.position, target.transform.position) > 1f) { ShowError(TEXT.GetText("ERROR_" + kind + "_SittingTooFar")); return true; }
            }
            channel.Request(b.Id, new byte[] { 1 });
            return true;
        }

        private static void ShowError(string message)
        {
            if (!string.IsNullOrEmpty(message)) Singleton<UI_ErrorMessage>.i.Show(message);
        }

        private void Reply(ulong id, byte[] payload)
        {
            if (host || !bindings.ContainsKey(id) || payload.Length == 0) return;
            if (payload[0] == ReplyDone) DoneSound(bindings[id].Target);
            else if (payload[0] == ReplyError && payload.Length > 1) ShowError(Encoding.UTF8.GetString(payload, 1, Math.Min(payload.Length - 1, 120)));
        }

        // The guest's own ears for a machine it ran through the host (#240): the native sound of that click.
        private static void DoneSound(MonoBehaviour target)
        {
            byte? variant = DoneVariant(target);
            if (variant != null) PlayDone(variant.Value);
        }
        private static byte? DoneVariant(MonoBehaviour target) =>
            target is Furniture_CannabisGrinder ? (byte)0 :
            target is Furniture_CannabisPackager || target is Furniture_FrenchFryPackager || target is Bunker_CompostMachine ? (byte)1 : (byte?)null;
        private static bool playingDone;
        private static void PlayDone(byte variant)
        {
            var sound = Singleton<SOUND_Manager>.i;
            if (sound == null) return;
            playingDone = true;
            try { if (variant == 0) sound.Play_General_LeatherCrafting(); else sound.Play_General_TapePacking(); }
            finally { playingDone = false; }
        }
        // Everyone else near a machine hears its completion (#240): replayed at the machine.
        internal void PlayDoneEvent(PresentationEvent e)
        {
            if (!bindings.TryGetValue(e.Target, out var b) || b.Target == null) return;
            using (SpatialSounds.At(b.Target.transform.position)) PlayDone(e.Variant);
        }
        // A crafting or tape sound this machine's player made with their own click (the host at a
        // machine); a guest's clicks run through the host, which announces them.
        internal void NativeDone(byte variant)
        {
            if (playingDone || RunningForGuest || SharedPresentationEvents.Replaying) return;
            var events = PresentationReplays.Events;
            if (events?.IsHost != true) return;
            var camera = Camera.main;
            if (camera == null) return;
            Binding? best = null; float bestDistance = 3f * 3f;
            foreach (var b in bindings.Values)
            {
                if (b.Target == null || DoneVariant(b.Target) != variant) continue;
                float d = (b.Target.transform.position - camera.transform.position).sqrMagnitude;
                if (d < bestDistance) { bestDistance = d; best = b; }
            }
            if (best != null) events.Post(PresentationKind.MachineDone, variant, best.Id, best.Target.transform.position);
        }

        private void ReplyTo(uint peer, ulong id, byte status, string? message = null)
        {
            if (session?.Peers.ContainsKey(peer) != true || channel == null) return;
            var text = message == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(message.Length > 60 ? message.Substring(0, 60) : message);
            channel.Reply(peer, id, new[] { status }.Concat(text).ToArray());
        }

        [MonMP.Core.World.WorldCommandAdapters(SharedRemoteClicks.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || peer == 0 || !bindings.TryGetValue(id, out var b) || payload.Length != 1 || payload[0] != 1) return;
            if (b.Target == null || !b.Target.gameObject.activeInHierarchy) return;
            if (b.Anchor != null && !b.Anchor.gameObject.activeInHierarchy) return;
            if (!presence.TryGetValue(peer, out var p) || Time.unscaledTime - p.Time > 2) return;
            if ((new Vector3(p.Pose.X, p.Pose.Y, p.Pose.Z) - PositionOf(b)).sqrMagnitude > CommandRange * CommandRange) return;
            if (lastCommand.TryGetValue((peer, id), out float last) && Time.unscaledTime - last < CommandCooldown) return;
            lastCommand[(peer, id)] = Time.unscaledTime;
            if (NewActionsBlocked || MonMPPlugin.Instance?.World?.SaveGate.Active == true)
            { ReplyTo(peer, id, ReplyError, "Wait for the multiplayer save or disconnect to finish."); return; }
            bool seated = b.Target is Furniture_CannabisGrinder || b.Target is Furniture_CannabisPackager;
            if (seated)
            {
                // The native click tests the host's own chair and distance: the guest's pose decides.
                string kind = b.Target is Furniture_CannabisGrinder ? "CannabisGrinder" : "CannabisPackager";
                if (p.Pose.State != (byte)PlayerState.InFurnitureChair) { ReplyTo(peer, id, ReplyError, TEXT.GetText("ERROR_" + kind + "_NeedToBeSitDown")); return; }
                if ((new Vector3(p.Pose.X, p.Pose.Y, p.Pose.Z) - b.Target.transform.position).sqrMagnitude > SeatRange * SeatRange)
                { ReplyTo(peer, id, ReplyError, TEXT.GetText("ERROR_" + kind + "_SittingTooFar")); return; }
            }
            var gameplay = Singleton<Gameplay>.i;
            var player = gameplay.Player.transform;
            var hostState = PlayerStateOf(gameplay);
            var hostPosition = player.position;
            Patches.RemoteActionScope.LastError = null;
            int sounds = SpatialSounds.Rerouted + SpatialSounds.Muted;
            using (Patches.RemoteActionScope.Enter())
            using (SpatialSounds.AtPeer(peer))
            {
                RunningForGuest = true;
                uint previousPeer = ProductionPeer;
                try
                {
                    ProductionPeer = peer;
                    if (seated) { PlayerStateOf(gameplay) = PlayerState.InFurnitureChair; player.position = b.Target.transform.position; }
                    b.Click();
#if MONMP_DEVELOPMENT
TestAccepted++;
#endif

                }
                catch (Exception ex) { Debug.LogWarning("MonMP remote click on " + b.Target.GetType().Name + " failed: " + ex.Message); }
                finally
                {
                    ProductionPeer = previousPeer;
                    if (seated) { PlayerStateOf(gameplay) = hostState; player.position = hostPosition; }
                    RunningForGuest = false;
                }
            }
            if (Patches.RemoteActionScope.LastError is string error) ReplyTo(peer, id, ReplyError, error);
            else if (SpatialSounds.Rerouted + SpatialSounds.Muted != sounds)
            {
                ReplyTo(peer, id, ReplyDone);
                // The requester heard its reply and the host heard the native click; everyone else hears it at the machine.
                if (DoneVariant(b.Target) is byte variant)
                    PresentationReplays.Events?.HostAnnounce(PresentationKind.MachineDone, variant, b.Id, b.Target.transform.position, 0, peer, false);
            }
            Patches.RemoteActionScope.LastError = null;
            // The grinder and packagers capture their delayed native outputs directly, including
            // scheduling recovery discovery if binding fails. Fryer power creates no item. Keep
            // the two-frame discovery fallback for the cutter, which has no direct output hook.
            if (NeedsOutputDiscovery(b.Target)) MonMPPlugin.Instance?.Items?.DiscoverSoon();
        }

        private static bool NeedsOutputDiscovery(MonoBehaviour target) =>
            !(target is Furniture_Fryer || target is Furniture_CannabisGrinder ||
              target is Furniture_CannabisPackager || target is Furniture_FrenchFryPackager);

        public void Dispose()
        {
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null) { channel.CommandRequested -= Command; channel.ReplyReceived -= Reply; }
            if (Active == this) Active = null;
            session = null; channel = null; presence.Clear(); lastCommand.Clear();
        }
    }
}
