using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonMP.Core.Session;
using MonMP.Core.Protocol;
using MonMP.Core.World;
using UniStorm;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MonMP.Plugin.Replication
{
    // Adapter 16: unanimous coordinated sleep. Each player's native sleep coroutine runs locally
    // but waits in bed until every connected player is asleep; only the host advances world time.
    internal sealed class SharedSleep : IDisposable
    {
        private const float TicksPerSecond = 100;
        private const int MaxTicksPerFrame = 25;
        // Native sleep loops call AddOneSecond; during a coordinated sleep only the host's own
        // advance may move the world clock. Every wrapped native step stays suppressed.
        internal static bool BlockNativeAdvance { get; private set; }
        internal static bool AdvancingWorldTime { get; private set; }

        private readonly bool host;
        private readonly Gameplay gameplay;
        private readonly BasicNeeds_Manager needs;
        private readonly TickSystem ticks;
        private readonly UniStormSystem sky;
        private readonly SleepCoordinator coordinator = new SleepCoordinator();
        private readonly List<uint> participants = new List<uint>();
        private SleepState? publishedState;
        private SleepState state = SleepState.Initial;
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool held, cancelRequested, disposed;
        private uint heldRound;
        private bool completionBaseline;
        private uint observedCompletion, hangoverRound;
        private bool awakeHangover;
        private readonly Queue<(int Ticks, uint Count, bool Hangover)> awakeCompletions = new Queue<(int, uint, bool)>();
        private (int Ticks, uint Count, bool Hangover) applyingCompletion;
        private string cancelPrompt = "Use or Jump";
        private float accumulator;
        // The skip eases in over its first second and out over its last ticks, so time speeds up and
        // slows down instead of starting and stopping at full rate.
        private const float EaseInSeconds = 1f, EaseOutTicks = 150f, MinEase = 0.15f;
        private const float ReadyResendSeconds = 2f;
        private float advancingSince = -1f;
        private readonly Dictionary<uint, PlayerPose> sleeperPoses = new Dictionary<uint, PlayerPose>();
        // The bed each sleeper lay down in, fixed for the whole sleep. Sleeping eyes bob, and beds
        // stand close together: taken fresh every frame, a sleeper hopped between beds, the shared
        // bed's count flipped between one and two, and its sleepers jumped side to side (#115).
        private readonly Dictionary<uint, Furniture_Bed?> sleeperBeds = new Dictionary<uint, Furniture_Bed?>();
        private Furniture_Bed? localBed; private bool localBedKnown;
        private readonly SleeperBedLayout<Furniture_Bed> bedLayout = new SleeperBedLayout<Furniture_Bed>();
        private bool bedLayoutDirty = true;
        private uint bedLayoutLocalPeer;
        public bool Active => !disposed && session?.IsRunning == true && channel != null;
        public bool Ready => channel?.Ready == true;
        public bool LocalWaiting => held;
        internal Furniture_Bed? LocalSleepingBed => gameplay.Get_IsSleeping && localBedKnown ? localBed : null;
        internal SleepState State => host ? coordinator.State : state;
        internal Func<uint, bool>? IsPermadead { get; set; }

        public SharedSleep(bool host, Gameplay gameplay)
        {
            this.host = host; this.gameplay = gameplay;
            needs = UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>(); ticks = UnityEngine.Object.FindObjectOfType<TickSystem>(); sky = UniStormSystem.Instance;
            if (needs == null || ticks == null || sky == null) throw new InvalidOperationException("World clock and needs are not initialized.");
            if (gameplay.Get_IsSleeping) throw new InvalidOperationException("Wake up before hosting or joining.");
        }
        public IEnumerable<WorldRecord> InitialRecords() { yield return new WorldRecord(SleepState.Entity, 1, SleepState.Adapter, SleepState.Initial.Encode()); }
        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PeerLeft += OnLeft;
            session.PoseReceived += OnPose;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed; channel.CommandRequested += Command;
        }
        private void OnLeft(uint peer)
        {
            if (sleeperPoses.Remove(peer) | sleeperBeds.Remove(peer)) bedLayoutDirty = true;
            if (host) coordinator.Remove(peer);
        }
        private void OnPose(uint peer, PlayerPose pose)
        {
            if (pose.Rest == PlayerRest.Sleeping)
            {
                if (!sleeperPoses.ContainsKey(peer)) bedLayoutDirty = true;
                sleeperPoses[peer] = pose;
                if (!sleeperBeds.TryGetValue(peer, out var bed) || bed == null)
                {
                    sleeperBeds[peer] = RemotePlayers.NearestBed(new Vector3(pose.X, pose.Y, pose.Z));
                    bedLayoutDirty = true;
                }
            }
            else if (sleeperPoses.Remove(peer) | sleeperBeds.Remove(peer)) bedLayoutDirty = true;
        }
        internal Furniture_Bed? BedOf(uint peer) => sleeperBeds.TryGetValue(peer, out var bed) ? bed : null;
        internal (int Slot, int Count) BedOccupancy(uint peer, Furniture_Bed bed)
        {
            // Everyone ranks the native sleep anchors by authenticated peer ID, including their
            // own sleeper. Visual offsets never enter the poses or coordinated time requests.
            RefreshBedLayout();
            return bedLayout.Occupancy(bed, peer);
        }

        private void RefreshBedLayout()
        {
            bool known = localBedKnown;
            var previous = localBed;
            if (!gameplay.Get_IsSleeping) localBedKnown = false;
            else if (!localBedKnown && gameplay.Player?.mainCamera != null)
            { localBed = RemotePlayers.NearestBed(gameplay.Player.mainCamera.transform.position); localBedKnown = localBed != null; }
            uint local = session?.LocalPeerId ?? 0;
            if (known != localBedKnown || previous != localBed || bedLayoutLocalPeer != local)
                bedLayoutDirty = true;
            if (!bedLayoutDirty) return;
            bedLayout.Clear();
            foreach (var sleeper in sleeperBeds)
                if (sleeper.Value != null && sleeperPoses.ContainsKey(sleeper.Key)) bedLayout.Add(sleeper.Value, sleeper.Key);
            if (session != null && localBedKnown && localBed != null) bedLayout.Add(localBed, local);
            bedLayout.Sort();
            bedLayoutLocalPeer = local;
            bedLayoutDirty = false;
        }
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            // Scoped repairs carry only the repaired records.
            if (!records.TryGetValue(SleepState.Entity, out var record)) return;
            var decoded = Decode(record);
            if (!host) Observe(decoded);
        }
        private void Changed(WorldRecord record) { if (!host && record.Id == SleepState.Entity) Observe(Decode(record)); }
        private void Observe(SleepState decoded)
        {
            state = decoded;
            if (decoded.Phase == SleepPhase.Advancing && decoded.Round > hangoverRound)
            { hangoverRound = decoded.Round; awakeHangover = needs.GetRealDrunkness >= 0.3f; }
            if (!completionBaseline)
            { completionBaseline = true; observedCompletion = decoded.Completed; return; }
            if (decoded.Completed <= observedCompletion) return;
            uint count = decoded.Completed - observedCompletion;
            observedCompletion = decoded.Completed;
            // A sleeper's native coroutine owns Fill_Sleep or SkipNight. Awake/AFK
            // players share the completed sleep's recovery, once, after its minutes.
            if (!held && !gameplay.Get_IsSleeping && !needs.Dead)
                awakeCompletions.Enqueue((decoded.TotalTicks, count, hangoverRound == decoded.Completed ? awakeHangover : needs.GetRealDrunkness >= 0.3f));
        }
        private void ApplyAwakeCompletions()
        {
            if (!Active || MonMPPlugin.Instance?.World?.SaveGate.Frozen == true) return;
            var clock = MonMPPlugin.Instance?.World?.Clock;
            if (clock == null) return;
            for (int budget = 0; budget < 16; budget++)
            {
                if (applyingCompletion.Count == 0)
                {
                    if (awakeCompletions.Count == 0) break;
                    applyingCompletion = awakeCompletions.Dequeue();
                }
                if (!clock.PersonalSleepReady(applyingCompletion.Ticks)) break;
                applyingCompletion.Count--;
                if (!needs.Dead) needs.Fill_Sleep(true, false, applyingCompletion.Hangover);
            }
        }
        private static SleepState Decode(WorldRecord record)
        {
            if (record.Id != SleepState.Entity || record.Adapter != SleepState.Adapter) throw new InvalidDataException("Unknown shared sleep identity.");
            return SleepState.Decode(record.Payload);
        }
        [MonMP.Core.World.WorldCommandAdapters(SleepState.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || id != SleepState.Entity || IsPermadead?.Invoke(peer) == true) return;
            coordinator.Request(peer, SleepCommand.Decode(payload));
        }
        private void Send(SleepCommand command)
        {
            if (!Active) return;
            if (host) coordinator.Request(session!.LocalPeerId, command);
            // Readiness is this player's own intent, not an edit of the aggregate record the host
            // republishes every frame: a revision check would drop it as stale.
            else channel!.Request(SleepState.Entity, command.Encode(), useLease: true);
        }

        public bool CanStart(out string reason)
        {
            reason = "";
            if (!Active) return true;
            if (!Ready || MonMPPlugin.Instance?.World?.SaveGate.Active == true) { reason = "Wait until the shared world is ready before sleeping."; return false; }
            if (State.Phase == SleepPhase.Advancing) { reason = "The other players are already asleep. Wait until they wake up."; return false; }
            return true;
        }
        public void RequestGetUp() { if (held) cancelRequested = true; }
        public string LocalStatus => !held ? "" : State.Phase == SleepPhase.Advancing && State.Round > heldRound
            ? "Sleeping... " + (State.Target == 0 ? 100 : State.Advanced * 100 / State.Target) + "%"
            : "Waiting for every player to sleep (" + State.Ready + "/" + State.Required + ").\nPress " + cancelPrompt + " to get up.";

        private static InputAction? CreateCancelAction(out string prompt)
        {
            prompt = "Use or Jump";
            var native = Singleton<GameAssets>.i?.inputs?.Player;
            if (native == null) return null;
            var action = new InputAction("MonMP sleep cancel", InputActionType.Button);
            CopyBindings(native.Value.Use, action);
            CopyBindings(native.Value.Jump, action);
            string use = DisplayBindings(native.Value.Use), jump = DisplayBindings(native.Value.Jump);
            prompt = "Use (" + use + ") or Jump (" + jump + ")";
            if (action.bindings.Count == 0) { action.Dispose(); return null; }
            action.Enable();
            return action;
        }

        private static void CopyBindings(InputAction source, InputAction target)
        {
            foreach (var binding in source.bindings)
                if (!binding.isComposite && !binding.isPartOfComposite && !string.IsNullOrWhiteSpace(binding.effectivePath))
                    target.AddBinding(binding.effectivePath);
        }

        private static string DisplayBindings(InputAction action)
        {
            var labels = new List<string>();
            foreach (var group in new[] { "Keyboard", "Gamepad" })
                for (int i = 0; i < action.bindings.Count; i++)
                {
                    var binding = action.bindings[i];
                    if (binding.isComposite || string.IsNullOrWhiteSpace(binding.effectivePath) || string.IsNullOrWhiteSpace(binding.groups) ||
                        !binding.groups.Split(',', ';').Any(value => string.Equals(value.Trim(), group, StringComparison.OrdinalIgnoreCase))) continue;
                    string label = action.GetBindingDisplayString(i);
                    if (label.Length > 0 && !labels.Contains(label)) labels.Add(label);
                }
            return labels.Count == 0 ? action.GetBindingDisplayString() : string.Join(" / ", labels);
        }

        internal IEnumerator Coordinate(IEnumerator native, bool skipNight, Furniture_Bed? nativeBed = null)
        {
            // The coroutine factory knows the selected bed before native entry starts moving
            // the camera. Publish its stable anchor even during the first sleeping pose.
            if (nativeBed != null) { localBed = nativeBed; localBedKnown = true; bedLayoutDirty = true; }
            bool hangover = needs.GetRealDrunkness >= 0.3f;
            var walker = gameplay.PlayerWalking.transform;
            Vector3 origin = walker.position; Quaternion rotation = walker.rotation;
            bool waiting = false, personalSleepEntered = false;
            var personalClock = MonMPPlugin.Instance?.World?.Clock;
            InputAction? cancelAction = null;
            try
            {
                cancelAction = CreateCancelAction(out cancelPrompt);
                // Run the native bed entry and eye closing until its time-advance loop begins.
                while (true)
                {
                    if (!Active) { GetUp(origin, rotation); yield break; }
                    if (!Step(native)) yield break;
                    if (native.Current is WaitForFixedUpdate) break;
                    yield return native.Current;
                }
                if (Active)
                {
                    heldRound = State.Round; held = waiting = true; cancelRequested = false;
                    personalClock?.BeginPersonalSleep(State.TotalTicks);
                    personalSleepEntered = true;
                    var readyCommand = new SleepCommand(true, DesiredTicks(skipNight, hangover), heldRound);
                    Send(readyCommand);
                    float resendAt = Time.unscaledTime + ReadyResendSeconds;
                    // Edge-detect from the key state: the game's input system may update in fixed
                    // steps, where wasPressedThisFrame is missed by this per-frame coroutine.
                    bool wasDown = true;
                    while (Active && !State.Releases(heldRound))
                    {
                        bool started = State.Phase == SleepPhase.Advancing && State.Round > heldRound;
                        // The request is idempotent; resending covers one lost on reconnect or a host reload.
                        if (!started && Time.unscaledTime >= resendAt) { Send(readyCommand); resendAt = Time.unscaledTime + ReadyResendSeconds; }
                        bool down = cancelAction?.IsPressed() == true;
                        bool pressed = down && !wasDown; wasDown = down;
                        if (!started && (cancelRequested || pressed && MonMPPlugin.Instance?.SocialUiBlocked != true))
                        {
                            Send(new SleepCommand(false, 0)); waiting = false;
                            GetUp(origin, rotation);
                            yield break;
                        }
                        yield return null;
                    }
                    // Disconnect restores the guest's local profile before this coroutine resumes.
                    // Exit without replaying native personal sleep effects or its bed-save request.
                    if (!Active) { waiting = false; GetUp(origin, rotation); yield break; }
                    personalClock?.EndPersonalSleep(State.TotalTicks);
                    personalSleepEntered = false;
                    // Sleep completion owns its native Fill_Sleep/SkipNight. Wait for
                    // the matching minute effects, including a coalesced final clock,
                    // before the native recovery resets hangover and elapsed timers.
                    while (Active && personalClock?.PersonalSleepReady(State.TotalTicks) == false) yield return null;
                    if (!Active) { waiting = false; GetUp(origin, rotation); yield break; }
                    waiting = false; held = false;
                }
                // The world clock already moved; finish the native loop's iterations at once.
                while (native.Current is WaitForFixedUpdate) if (!Step(native)) yield break;
                yield return native.Current;
                while (Step(native)) yield return native.Current;
            }
            finally
            {
                if (personalSleepEntered) personalClock?.EndPersonalSleep(State.TotalTicks);
                if (waiting) Send(new SleepCommand(false, 0));
                held = false; cancelRequested = false;
                cancelAction?.Disable(); cancelAction?.Dispose(); cancelPrompt = "Use or Jump";
                (native as IDisposable)?.Dispose();
            }
        }
        private static bool Step(IEnumerator native)
        {
            BlockNativeAdvance = true;
            try { return native.MoveNext(); }
            finally { BlockNativeAdvance = false; }
        }
        private ushort DesiredTicks(bool skipNight, bool hangover)
        {
            int desired;
            if (!skipNight) desired = Mathf.CeilToInt(needs.GetEnergy * 600f);
            else
            {
                int tick = ticks.Get_Tick, stop = hangover ? GameManager.Tick_SkipNight_Hangover_Stop : GameManager.Tick_SkipNight_Stop;
                desired = tick <= stop ? stop - tick : 1440 - tick + stop;
            }
            return (ushort)Mathf.Clamp(desired, 1, SleepState.MaxTicks);
        }
        private void GetUp(Vector3 origin, Quaternion rotation)
        {
            gameplay.SleepingOverlay.gameObject.SetActive(false);
            AudioListener.volume = gameplay.MasterVolume;
            gameplay.PlayerWalking.transform.SetPositionAndRotation(origin, rotation);
            gameplay.PlayerWalking.ExitBed();
            gameplay.PlayerControl_Enable();
            gameplay.Sleep_Stop();
        }

        public void Update()
        {
            if (!Active) return;
            if (!host) { ApplyAwakeCompletions(); return; }
            if (!completionBaseline) { completionBaseline = true; observedCompletion = coordinator.State.Completed; }
            // A player away from the keyboard doesn't hold up the night; one already in bed still counts.
            var activity = MonMPPlugin.Instance?.Activity;
            uint local = session!.LocalPeerId;
            participants.Clear();
            foreach (uint peer in session.Peers.Keys)
                if (IsPermadead?.Invoke(peer) != true && (coordinator.IsReady(peer) || activity?.IsAfk(peer, local) != true)) participants.Add(peer);
            var s = coordinator.Update(participants, MonMPPlugin.Instance?.World?.SaveGate.Active == true || MonMPPlugin.Instance?.HostExitPending == true);
            Observe(s);
            if (s.Phase == SleepPhase.Advancing)
            {
                if (advancingSince < 0) advancingSince = Time.unscaledTime;
                var st = coordinator.State;
                float ease = Mathf.Clamp01((Time.unscaledTime - advancingSince) / EaseInSeconds) *
                    Mathf.Clamp01((st.Target - st.Advanced) / EaseOutTicks);
                ease = Mathf.Max(MinEase, Mathf.SmoothStep(0f, 1f, ease));
                accumulator = Mathf.Min(accumulator + Time.unscaledDeltaTime * TicksPerSecond * ease, MaxTicksPerFrame);
                int run = coordinator.Advance((int)accumulator);
                accumulator -= run;
                bool previousAdvance = AdvancingWorldTime;
                try
                {
                    AdvancingWorldTime = true;
                    for (int i = 0; i < run; i++)
                    { MonMPPlugin.Instance?.World?.Clock.SleepTick(); sky.AddOneSecond(); }
                }
                finally { AdvancingWorldTime = previousAdvance; }
                // AddOneSecond raises its own minute event; without this UniStorm.Update raises a duplicate next frame.
                if (run > 0) sky._cacheMinute = (int)(sky.m_TimeFloat * 24f * 60f) % 60;
                if (coordinator.State.Phase == SleepPhase.Idle)
                {
                    accumulator = 0; advancingSince = -1f;
                }
            }
            else advancingSince = -1f;
            Observe(coordinator.State);
            ApplyAwakeCompletions();
            // Coordinator states are immutable and retain identity when unchanged.
            // Commit this marker only after publication succeeds, so a failure retries.
            var publication = coordinator.State;
            if (!ReferenceEquals(publishedState, publication))
            {
                channel!.Publish(SleepState.Entity, publication.Encode());
                publishedState = publication;
            }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (session != null) { session.PeerLeft -= OnLeft; session.PoseReceived -= OnPose; }
            sleeperPoses.Clear(); sleeperBeds.Clear();
            participants.Clear(); publishedState = null;
            awakeCompletions.Clear();
            applyingCompletion = default;
            bedLayout.Clear(); bedLayoutDirty = true;
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; }
            session = null; channel = null;
        }


#if MONMP_DEVELOPMENT
        internal bool TestSleeping => gameplay.Get_IsSleeping;


        internal float TestEnergy => needs.GetEnergy;


        internal bool TestIsReady(uint peer) => host && coordinator.IsReady(peer);
#endif

    }
}
