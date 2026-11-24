using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Leo;
using MonMP.Core.Session;
using MonMP.Core.World;
using UniStorm;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed class SharedClock : IDisposable
    {
        private static readonly FieldInfo total = AccessTools.Field(typeof(TickSystem), "totalTickSinceGameStart");
        private static readonly FieldInfo weekday = AccessTools.Field(typeof(TickSystem), "DayOfTheWeek");
        private static readonly FieldInfo days = AccessTools.Field(typeof(TickSystem), "NumberOfDays");
        private static readonly FieldInfo lastHour = AccessTools.Field(typeof(UniStormSystem), "m_LastHour");
        private static readonly FieldInfo nativeFastForward = AccessTools.Field(typeof(UniStormSystem), "coroutine_ThisAmountOfTick");
        private readonly UniStormSystem sky;
        private readonly TickSystem ticks;
        private readonly BasicNeeds_Manager needs;
        private readonly UI_ClockCalendar calendar;
        private readonly Gameplay? gameplay = UnityEngine.Object.FindObjectOfType<Gameplay>();
        private readonly bool host;
        private readonly ClockState original;
        private readonly UniStormSystem.EnableFeature originalRealTime;
        private readonly GuestPersonalClock personal = new GuestPersonalClock();
        private int computerTicks;
        private int sleepTicks, refillAt, refillComputerTicks, refillSleepTicks;
        private int fastForwardStart;
        private ushort fastForwardTarget;
        private bool fastForwardRunning;
        private bool previousNativeFastForward, previousTickFastForward, previousLeoFastForward;
        private Leo_Manager? fastForwardLeo;
        private readonly FastForwardPresentationCursor fastForward = new FastForwardPresentationCursor();
        internal bool FastForwarding => !disposed && fastForward.Active(Time.unscaledTime, !host && personal.HasPending);
        internal float FastForwardProgress => fastForward.Progress;
        internal bool NativeFastForwardAttached => !disposed && host && session?.IsRunning == true &&
            MonMPPlugin.Instance?.World?.Clock == this;
        internal void BeginNativeFastForward(int amount)
        {
            if (!host || disposed || session?.IsRunning != true) return;
            if (amount != 60 && amount != 1440) throw new InvalidDataException("Unsupported native time skip.");
            previousNativeFastForward = (bool)nativeFastForward.GetValue(sky);
            previousTickFastForward = ticks.isFastForwardTime;
            fastForwardLeo = gameplay?.Get_Leo_Manager;
            previousLeoFastForward = fastForwardLeo?.isFastForwardTime == true;
            fastForwardStart = ticks.Get_totalTickSinceGameStart;
            fastForwardTarget = (ushort)amount;
            fastForwardRunning = true;
            PublishNativeFastForward();
        }
        internal void PublishNativeFastForward()
        {
            if (!host || disposed || session?.IsRunning != true) return;
            current = Capture(); channel!.Publish(ClockState.Entity, current.Encode());
            fastForward.Observe(current, Time.unscaledTime);
            fastForward.RetireIfDrained(false);
            MonMPPlugin.Instance?.UpdateTimeSkipPresentation();
        }
        internal void EndNativeFastForward()
        {
            RestoreNativeFastForward();
            PublishNativeFastForward();
        }
        private void RestoreNativeFastForward()
        {
            if (fastForwardRunning)
            {
                if (sky != null) nativeFastForward.SetValue(sky, previousNativeFastForward);
                if (ticks != null) ticks.isFastForwardTime = previousTickFastForward;
                if (fastForwardLeo != null) fastForwardLeo.isFastForwardTime = previousLeoFastForward;
            }
            // Keep the last identity in ordinary clock records: a coalesced final
            // record must still identify the skip whose guest minutes are pending.
            fastForwardLeo = null; fastForwardRunning = false;
        }
        private uint computerUser = ItemLease.Unowned;
        internal void ComputerTick(uint user) { if (host) { computerTicks++; computerUser = user; } }
        internal void SleepTick() { if (host) sleepTicks++; }
        // TickSystem has incremented its total and the needs prefix has attributed
        // this minute. Each endpoint is retained FIFO by WorldChannel, including
        // during snapshot catch-up/repair, so mixed causes cannot be coalesced.
        internal void PublishNativeMinute()
        {
            if (!host || disposed || session?.IsRunning != true) return;
            current = Capture(); channel!.Publish(ClockState.Entity, current.Encode());
        }
        internal void NeedsRefilled()
        {
            if (!host || !NativeFastForwardScope.Active) return;
            refillAt = ticks.Get_totalTickSinceGameStart;
            refillComputerTicks = computerTicks; refillSleepTicks = sleepTicks;
            PublishNativeMinute();
        }
        internal void BeginPersonalSleep(int counter) { if (!host) personal.BeginSleep(counter); }
        internal void EndPersonalSleep(int counter) { if (!host) personal.EndSleep(counter); }
        internal bool PersonalSleepReady(int counter) => host || current.SleepTicks >= counter && !personal.HasPending;
        private ClockState current;
        private NetworkSession? session;
        private WorldChannel? channel;
        private float nextPublish;
        // Guests never run TickSystem.Tick, whose minute work also drives presentation: the auto
        // lights (street, bar and building lights) and the world reflection probe/GI refresh that
        // darkens materials at dusk. Those parts run here from the host's minute.
        private static readonly FieldInfo Probe = AccessTools.Field(typeof(TickSystem), "worldReflectionProbe");
        private static readonly int[] ProbeMarks = Enumerable.Range(0, 24).Select(h => h * 60)
            .Concat(new[] { 380, 390, 400, 410, 430, 440, 450, 1090, 1100, 1110, 1120, 1130, 1170 }).Distinct().OrderBy(m => m).ToArray();
        private int presentedMinute = -1;
        // Guests show the sky between the host's 4 Hz clock publishes: each publish eases the shown time
        // of day from where it is to the new value over the publish interval, so a sleep or fast-forward
        // skip turns the sun smoothly instead of stepping. A jump over half a day still snaps.
        private float shownFraction = -1f, easeFrom, easeDelta, easeTarget, easeStart, easeDuration, lastApplied;
        // Other minute subscribers that only present the world for the local player: distance-gated
        // NPC groups (dealers and their smoke), church bells and the cottage thermometer.
        private static readonly MethodInfo? DistanceTick = AccessTools.Method(typeof(DisableWhenPlayerTooFar), "Tick");
        private static readonly MethodInfo? ChurchTick = AccessTools.Method(typeof(SOUND_Church), "Tick");
        private static readonly MethodInfo? ThermometerTick = AccessTools.Method(typeof(HomeThermometer), "Tick");
        private readonly List<(MonoBehaviour Target, MethodInfo Tick)> minuteSubscribers = new List<(MonoBehaviour, MethodInfo)>();
        private readonly SharedStorePresence? stores;
        internal SharedStorePresence? Stores => stores;
        private bool disposed;
        public bool BlocksLocalWorldTicks => !host && session != null && !disposed;

#if MONMP_DEVELOPMENT
        internal ClockState TestState => current;


        internal ClockState TestNative => Capture();


        internal ClockState TestOriginal => original;


        internal int TestPersonalTicks { get; private set; }


        internal bool TestRestored => !host && sky != null && Capture().Encode().SequenceEqual(original.Encode()) && sky.RealWorldTime == originalRealTime;
#endif

        public SharedClock(bool host, string diagnostics)
        {
            this.host = host; sky = UnityEngine.Object.FindObjectOfType<UniStormSystem>();
            ticks = UnityEngine.Object.FindObjectOfType<TickSystem>(); needs = UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>();
            calendar = UnityEngine.Object.FindObjectOfType<UI_ClockCalendar>();
            if (sky == null || !sky.UniStormInitialized || ticks == null || needs == null || calendar == null || ticks.Get_TimeIsAccelerated)
                throw new InvalidOperationException("Wait until the world clock has initialized and saving/sleep/fast-forward finishes.");
            originalRealTime = sky.RealWorldTime; original = current = Capture();
            try { stores = new SharedStorePresence(host); }
            catch (Exception ex) { Debug.LogWarning("MonMP store presence unavailable: " + ex.Message); }
            if (!host)
            {
                foreach (var (type, tick) in new[] { (typeof(DisableWhenPlayerTooFar), DistanceTick), (typeof(SOUND_Church), ChurchTick), (typeof(HomeThermometer), ThermometerTick) })
                    if (tick != null)
                        foreach (var target in UnityEngine.Object.FindObjectsOfType(type, true).OfType<MonoBehaviour>())
                            if (target.gameObject.scene.IsValid()) minuteSubscribers.Add((target, tick));
            }
            _ = original.Encode();

#if MONMP_DEVELOPMENT
            File.WriteAllText(Path.Combine(diagnostics, "shared-clock.txt"), "MONMP_CLOCK_REGISTERED\nEntity=" + ClockState.Entity.ToString("X16") + "\nMinute=" + original.MinuteOfDay + "\nTicks=" + original.TotalTicks);
#endif

        }
        public IEnumerable<WorldRecord> InitialRecords()
        {
            yield return new WorldRecord(ClockState.Entity, 1, ClockState.Adapter, original.Encode());
            if (stores != null) yield return new WorldRecord(SharedStorePresence.Entity, 1, ClockState.Adapter, stores.Capture());
        }
        public void Attach(NetworkSession session, WorldChannel channel)
        { this.session = session; this.channel = channel; channel.SnapshotApplied += Snapshot; channel.Changed += Changed; }
        private ClockState Capture() => new ClockState(sky.m_TimeFloat, sky.Year, sky.Month, sky.Day,
            ticks.Get_DayOfTheWeek, ticks.Get_NumberOfDays, ticks.Get_totalTickSinceGameStart, sky.TimeFlow == UniStormSystem.EnableFeature.Enabled,
            computerTicks, computerUser, sleepTicks, refillAt, refillComputerTicks, refillSleepTicks, fastForwardStart, fastForwardTarget, fastForwardRunning);
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            if (records.TryGetValue(ClockState.Entity, out var record)) Apply(record, true);
            if (records.TryGetValue(SharedStorePresence.Entity, out var presence)) stores?.Apply(presence.Payload);
        }
        private void Changed(WorldRecord record)
        {
            if (host || record.Adapter != ClockState.Adapter) return;
            if (record.Id == SharedStorePresence.Entity) { stores?.Apply(record.Payload); return; }
            Apply(record);
        }
        // The host publishes every native minute plus a 4 Hz heartbeat, so a skip delivers
        // dozens of clock records per second (and Steam can batch several into one frame).
        // Each record still updates native time and personal attribution at once; the
        // presentation (calendar/season, sky easing, minute subscribers, probe refresh and
        // fire burn-down) runs once per frame for the newest record (#0.9.87 skip lag).
        private ClockState? presentedState;
        private bool presentationDue;
        private int pendingFireTicks;
        private void Apply(WorldRecord record, bool repair = false)
        {
            if (record.Id != ClockState.Entity || record.Adapter != ClockState.Adapter) throw new InvalidDataException("Unknown shared clock identity.");
            var previous = current;
            current = ClockState.Decode(record.Payload);
            personal.Observe(current, needs, gameplay, MonMPPlugin.Instance?.World?.PersonalProfileReady == true, repair); ApplyNative(current, false, false);
            fastForward.Observe(current, Time.unscaledTime);
            fastForward.RetireIfDrained(personal.HasPending);
            if (previous != null) pendingFireTicks = Math.Min(1440, pendingFireTicks + Math.Max(0, current.TotalTicks - previous.TotalTicks));
            presentationDue = true;
        }
        private void FlushPresentation()
        {
            if (!presentationDue) return;
            presentationDue = false;
            sky.GetDate(); sky.CalculateSeason(); calendar.Tick(current.MinuteOfDay, current.Weekday);
            MonMPPlugin.Instance?.UpdateTimeSkipPresentation();
            EaseTowards(current, presentedState);
            presentedState = current;
            PresentMinute(current.MinuteOfDay);
            int fireTicks = pendingFireTicks; pendingFireTicks = 0;
            if (fireTicks > 0) TickPresentationFires(fireTicks);
        }
        private void EaseTowards(ClockState target, ClockState? previous)
        {
            float now = Time.unscaledTime;
            float from = shownFraction >= 0 ? shownFraction : target.DayFraction;
            float delta = target.DayFraction - from;
            if (previous != null && target.TotalTicks >= previous.TotalTicks && delta < -0.5f) delta += 1f;
            if (previous == null || Math.Abs(delta) > 0.5f) { from = target.DayFraction; delta = 0; }
            easeFrom = from; easeDelta = delta; easeTarget = target.DayFraction; easeStart = now;
            easeDuration = Mathf.Clamp(now - lastApplied, 0.1f, 0.5f); lastApplied = now;
            shownFraction = from;
        }
        private float ShownFraction()
        {
            float t = easeDuration <= 0 ? 1f : Mathf.Clamp01((Time.unscaledTime - easeStart) / easeDuration);
            if (t >= 1f) return shownFraction = easeTarget;
            float f = easeFrom + easeDelta * t;
            return shownFraction = f - Mathf.Floor(f);
        }
        // Guests light their own copy of a host-burned log; burn it down at the host's tick rate
        // (native TickSystem.Tick does this per minute and never runs on guests).
        // A scene-wide search per clock record dominated guest skip cost; lit logs burn for
        // many minutes, so a list refreshed at most once a second misses nothing visible.
        private WoodOnFire[] fires = Array.Empty<WoodOnFire>();
        private float firesFoundAt = float.NegativeInfinity;
        private void TickPresentationFires(int count)
        {
            if (Time.unscaledTime - firesFoundAt >= 1f) { fires = UnityEngine.Object.FindObjectsOfType<WoodOnFire>(); firesFoundAt = Time.unscaledTime; }
            if (fires.Length == 0) return;
            SharedItems.GuestFireTicking = true;
            try
            {
                for (int n = 0; n < count; n++)
                    foreach (var fire in fires) if (fire != null) fire.Tick();
            }
            catch (Exception ex) { Debug.LogWarning("MonMP fire presentation tick failed: " + ex.Message); }
            finally { SharedItems.GuestFireTicking = false; }
        }
        private void ApplyNative(ClockState state, bool restore, bool refreshPresentation = true, float? shown = null)
        {
            sky.TimeFlow = restore && state.Flowing ? UniStormSystem.EnableFeature.Enabled : UniStormSystem.EnableFeature.Disabled;
            sky.RealWorldTime = restore ? originalRealTime : UniStormSystem.EnableFeature.Disabled;
            float fraction = shown ?? state.DayFraction;
            sky.m_TimeFloat = fraction; sky.Year = state.Year; sky.Month = state.Month; sky.Day = state.Day;
            sky.Hour = (int)(fraction * 24f); sky.Minute = shown.HasValue ? (int)(fraction * 1440f) % 60 : state.MinuteOfDay % 60;
            sky._cacheMinute = sky.Minute; lastHour.SetValue(sky, sky.Hour);
            total.SetValue(ticks, state.TotalTicks); weekday.SetValue(ticks, state.Weekday); days.SetValue(ticks, state.ElapsedDays);
            if (refreshPresentation) { sky.GetDate(); sky.CalculateSeason(); calendar.Tick(state.MinuteOfDay, state.Weekday); }
        }
        private static int ProbeBucket(int minute) { int b = 0; for (int i = 0; i < ProbeMarks.Length; i++) if (ProbeMarks[i] <= minute) b = i; return b; }
        private readonly HashSet<string> failedSubscribers = new HashSet<string>();
        private void PresentMinute(int minute)
        {
            if (minute == presentedMinute) return;
            bool refresh = presentedMinute < 0 || ProbeBucket(minute) != ProbeBucket(presentedMinute) || Math.Abs(minute - presentedMinute) > 30;
            presentedMinute = minute;
            try
            {
                ticks.GetComponent<Event_AutoLights>()?.Tick(minute);
                ticks.GetComponent<SOUND_Manager>()?.Tick(minute);
                // One failing native subscriber must not starve the others of their minute tick.
                foreach (var (target, tick) in minuteSubscribers)
                {
                    if (target == null || !target.isActiveAndEnabled || !NativeMinuteSubscriberReadiness.Prepare(target, gameplay)) continue;
                    try { tick.Invoke(target, new object[] { minute }); }
                    catch (System.Reflection.TargetInvocationException ex)
                    {
                        string key = target.GetType().Name + ": " + ex.InnerException?.GetType().Name;
                        if (failedSubscribers.Add(key)) Debug.LogWarning("MonMP clock minute tick failed for " + key + " " + ex.InnerException?.Message);
                    }
                }
                if (refresh)
                {
                    (Probe?.GetValue(ticks) as ReflectionProbe)?.RenderProbe(null);
                    DynamicGI.UpdateEnvironment();
                }
            }
            catch (Exception ex) { Debug.LogWarning("MonMP clock presentation failed: " + ex.Message); }
        }
        public void BeforeNativeUpdate(UniStormSystem instance)
        {
            if (!BlocksLocalWorldTicks || instance != sky) return;
            // Preserve native visual/audio updates while preventing its clock,
            // minute delegates and hourly world generation from advancing locally.
            ApplyNative(current, false, false, shownFraction >= 0 ? ShownFraction() : (float?)null);
        }

#if MONMP_DEVELOPMENT
        internal void TestRestoreFixtureClock(ClockState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            _ = state.Encode();
            current = state;
            ApplyNative(state, true);
        }
#endif

        public void Update()
        {
            if (disposed || session?.IsRunning != true) return;
            if (host)
            {
                if (Time.unscaledTime < nextPublish) return;
                nextPublish = Time.unscaledTime + 0.25f; current = Capture(); channel!.Publish(ClockState.Entity, current.Encode());
                if (stores?.HostPoll(Time.unscaledTime) is byte[] presence) channel.Publish(SharedStorePresence.Entity, presence);
            }
            else
            {
                FlushPresentation();
                if (channel!.Ready != true || MonMPPlugin.Instance?.World?.SaveGate.Frozen == true) return;
                stores?.GuestRetry();
                for (int i = 0; i < 16 && TakePersonalMinute(); i++) { }
                fastForward.RetireIfDrained(personal.HasPending);
            }
        }
        private bool TakePersonalMinute()
        {
            if (!personal.TryReplay(needs, gameplay, current.MinuteOfDay, session!.LocalPeerId,
                MonMPPlugin.Instance?.World?.PersonalProfileReady == true, out bool refill)) return false;
            fastForward.Progressed(Time.unscaledTime);

#if MONMP_DEVELOPMENT
            if (!refill) TestPersonalTicks++;
#endif

            return true;
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            RestoreNativeFastForward(); fastForward.Reset();
            fastForwardStart = 0; fastForwardTarget = 0;
            computerTicks = original.ComputerTicks; computerUser = original.ComputerUser;
            sleepTicks = original.SleepTicks; refillAt = original.RefillAt;
            refillComputerTicks = original.RefillComputerTicks; refillSleepTicks = original.RefillSleepTicks;
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; }
            if (!host && sky != null && ticks != null && calendar != null) { ApplyNative(original, true); PresentMinute(original.MinuteOfDay); }
            session = null; channel = null;
        }
    }
}
