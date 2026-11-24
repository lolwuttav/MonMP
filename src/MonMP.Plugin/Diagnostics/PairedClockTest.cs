using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UniStorm;
using UnityEngine;
using UnityEngine.UI;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedClockTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly UniStormSystem sky;
        private readonly TickSystem ticks;
        private readonly BasicNeeds_Manager needs;
        private int phase, acknowledgment, minuteEvents, dayEvents, initialNeedsTick, repairs, needsApplied;
        private float next, fraction;
        private ulong food;
        private readonly UI_BasicNeeds needsUi;
        private readonly Image syrupWarning;
        private readonly Image[] warningImages;
        private Color[]? warningBaseline;
        private float? hostSyrupBaseline;
        private static readonly string[] SkipFloatFields = { "MappleSyrup", "Poutine", "Energy", "Anxiety", "THCLevel", "Drunkness", "StomachAlcoolContent" };
        private static readonly string[] SkipTimerFields = { "LastTime_Drink", "LastTime_Ate", "LastTime_Slept", "EnergyDrinkInBlood", "HangoverDebuff", "DeathDebuff", "Cooldown_Vomit" };
        private static PairedClockTest? skipProbe;
        private const float SkipNeedBaseline = 0.2f;
        private const int SkipTimerBaseline = 40;
        private int skipStart, skipPersonalStart, skipNeedsStart, skipRefills, repairedRefills, skipComputerStart, skipSleepStart;
        private byte[]? skipResult, repairNeeds;
        private FieldInfo[]? skipFixtureFields;
        private object[]? skipFixtureValues;
        private uint guest;
        private byte pendingSignal;
        private float nextSignalRetry;
        private WorldChannel? signalChannel;
        public string Status => "clock phase " + phase + "; pendingSignal=" + pendingSignal + "; ack=" + acknowledgment +
            "; nativeSkip=" + (sky != null ? NativeSkipRunning.ToString() : "missing");
        internal static void Prepare(Gameplay gameplay, bool host)
        {
            var sky = UniStormSystem.Instance; var ticks = UnityEngine.Object.FindObjectOfType<TickSystem>();
            gameplay.Get_isHardMode = true;
            sky.TimeFlow = host ? UniStormSystem.EnableFeature.Disabled : UniStormSystem.EnableFeature.Enabled;
            sky.RealWorldTime = UniStormSystem.EnableFeature.Disabled;
            sky.m_TimeFloat = host ? 1438.25f / 1440f : 0.25f;
            sky.Year = host ? 2024 : 2023; sky.Month = host ? 6 : 1; sky.Day = host ? 29 : 5;
            sky.Hour = (int)(sky.m_TimeFloat * 24); sky.Minute = sky.GetCurrentTick % 60; sky._cacheMinute = sky.Minute;
            AccessTools.Field(typeof(UniStormSystem), "m_LastHour").SetValue(sky, sky.Hour); sky.GetDate();
            AccessTools.Field(typeof(TickSystem), "totalTickSinceGameStart").SetValue(ticks, host ? 1000 : 10);
            AccessTools.Field(typeof(TickSystem), "DayOfTheWeek").SetValue(ticks, host ? 3 : 1);
            AccessTools.Field(typeof(TickSystem), "NumberOfDays").SetValue(ticks, host ? 4 : 1);
        }
        internal PairedClockTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay; sky = UniStormSystem.Instance;
            ticks = UnityEngine.Object.FindObjectOfType<TickSystem>(); needs = UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>();
            needsUi = UnityEngine.Object.FindObjectOfType<UI_BasicNeeds>();
            syrupWarning = (Image)AccessTools.Field(typeof(UI_BasicNeeds), "WarningImage_MapleSyrup").GetValue(needsUi);
            warningImages = new[] { "WarningImage_MapleSyrup", "WarningImage_Poutine", "WarningImage_Energy", "WarningImage_Anxiety" }
                .Select(name => (Image)AccessTools.Field(typeof(UI_BasicNeeds), name).GetValue(needsUi)).ToArray();
            needs.GetPoutine = 0.25f; needs.GetMappleSyrup = 0.25f; needs.GetEnergy = 0.25f; needs.GetAnxiety = 0.1f;
            initialNeedsTick = LastAte();
            sky.OnMinuteChangeEvent += MinuteEvent; ticks.NewDayEvent += DayEvent;
            if (plugin.TestOptions.IsHost) plugin.World!.Channel.CommandRequested += Acknowledge;
        }
        private void MinuteEvent(int tick) => minuteEvents++;
        private void DayEvent(int day) => dayEvents++;
        private void Acknowledge(uint peer, ulong entity, byte[] payload)
        { if (peer != 0 && entity == ClockState.Entity && payload.Length == 1) { guest = peer; acknowledgment = Math.Max(acknowledgment, payload[0]); } }
        private void SkipResult(ulong entity, byte[] payload)
        { if (entity == ClockState.Entity && payload.Length == 70 && payload[0] == 0xc7) skipResult = payload; }
        private void Signal(byte stage)
        {
            signalChannel = plugin.World!.Channel;
            pendingSignal = stage; nextSignalRetry = Time.unscaledTime + 0.5f;
            signalChannel.Request(ClockState.Entity, new[] { stage });
        }
        private void ClearSignalRetry()
        { pendingSignal = 0; nextSignalRetry = 0; signalChannel = null; }
        private void RetrySignal(WorldChannel channel)
        {
            if (pendingSignal == 0) return;
            if (!ReferenceEquals(signalChannel, channel)) { ClearSignalRetry(); return; }
            // Clock revisions change while native skips and their cached-minute callbacks
            // drain. A stale control command is correctly rejected by WorldChannel; repeat
            // this idempotent fixture signal against the latest received revision.
            if (Time.unscaledTime >= nextSignalRetry) Signal(pendingSignal);
        }
        private int LastAte() => (int)AccessTools.Field(typeof(BasicNeeds_Manager), "LastTime_Ate").GetValue(needs);
        internal bool Tick()
        {
            if (pendingSignal != 0 && (plugin.Session?.IsRunning != true || !ReferenceEquals(signalChannel, plugin.World?.Channel)))
                ClearSignalRetry();
            if (Time.unscaledTime < next) return false;
            if (!plugin.TestOptions.IsHost && phase == 6) { ClearSignalRetry(); plugin.BeginPairedTestSession(); phase = 7; return false; }
            var world = plugin.World; if (world?.Channel.FullySynchronized != true) return false;
            if (plugin.TestOptions.IsHost) return Host(world);
            RetrySignal(world.Channel);
            var clock = world.Clock; var state = clock.TestState;
            if (phase == 0)
            {
                food = world.Items.TestTaggedItem(world.Items.TestPrefab("Food/Poutine"), 0.64f); if (food == 0) return false;
                Require(state.TotalTicks == 1000 && state.Year == 2024 && state.Month == 6 && state.Day == 29 && state.Weekday == 3 && state.ElapsedDays == 4,
                    "Initial host calendar/counters did not replace guest values.");
                Require(clock.TestPersonalTicks == 0 && LastAte() == initialNeedsTick, "Initial snapshot replayed historical needs ticks.");
                fraction = state.DayFraction;
                sky.TimeFlow = UniStormSystem.EnableFeature.Enabled; sky.m_TimeFloat = 0.7f;
                AccessTools.Method(typeof(UniStormSystem), "Update").Invoke(sky, null);
                sky.AddOneSecond(); sky.AddOneHour(); sky.AddOneDay();
                AccessTools.Method(typeof(TickSystem), "Tick").Invoke(ticks, new object[] { 123 });
                AccessTools.Method(typeof(TickSystem), "DayChanged").Invoke(ticks, null);
                AccessTools.Method(typeof(UniStormSystem), "CalculateDays").Invoke(sky, null);
                // Guest sleep is coordinated and covered by the Sleep suite.
                Require(sky.m_TimeFloat == fraction && ticks.Get_totalTickSinceGameStart == 1000 && !gameplay.Get_IsSleeping,
                    "Guest clock controls advanced time.");
                phase = 1; Delay(0.7f);
            }
            else if (phase == 1)
            {
                Require(sky.m_TimeFloat == fraction && minuteEvents == 0 && dayEvents == 0, "Guest clock ran independently while host was stopped.");
                Require(syrupWarning.color.a == 0 && needs.GetMappleSyrup < 0.8f, "Guest warning baseline is not healthy and clear.");
                Signal(1); phase = 2;
            }
            else if (phase == 2 && state.TotalTicks > 1000 && state.Day == 1 && world.Items.TestState(food).Values.Value1 < 0.8f)
            {
                Require(state.Weekday == 4 && state.ElapsedDays == 5 && clock.TestPersonalTicks == state.TotalTicks - 1000 && LastAte() - initialNeedsTick == clock.TestPersonalTicks,
                    "Midnight or personal needs counter did not follow the host exactly once.");
                Require(minuteEvents == 0 && dayEvents == 0, "Host update replayed guest world delegates.");
                Require(needs.GetMappleSyrup < 0.8f && syrupWarning.color.a == 0,
                    "Host critical syrup warning leaked to the healthy guest during clock replication.");
                var item = (Item_Poutine)world.Items.TestItem(food)!;
                Require(item.Value1 < 0.8f && item.Value1 > 0.79f && item.Value1 == world.Items.TestState(food).Values.Value1, "Host food spoilage did not follow canonical clock ticks.");
                var text = UnityEngine.Object.FindObjectOfType<UI_ClockCalendar>().Get_DateText();
                Require(text.Contains(TimeSpan.FromMinutes(state.MinuteOfDay).ToString(@"hh\:mm")), "Native clock UI did not update.");
                needsApplied = clock.TestPersonalTicks; repairs = world.Channel.RepairsApplied;
                Require(world.Channel.RequestResync(SnapshotScope.Section(ClockState.Adapter), "Clock repair no replay"), "Clock repair request failed.");
                phase = 3;
            }
            else if (phase == 3 && world.Channel.RepairsApplied > repairs)
            {
                Require(clock.TestPersonalTicks == needsApplied && minuteEvents == 0, "Clock repair repeated personal/world ticks.");
                ProbeLocalSyrupWarning();
                RestoreWarningFixture();
                Signal(2); phase = 4; Delay(0.4f);
            }
            else if (phase == 4)
            {
                sky.OnMinuteChangeEvent -= MinuteEvent; ticks.NewDayEvent -= DayEvent;
                ClearSignalRetry();
                plugin.EndPairedTestSession();
                Require(clock.TestRestored && sky.TimeFlow == UniStormSystem.EnableFeature.Enabled, "Disconnect failed to restore offline clock/calendar/flow.");
                int before = ticks.Get_totalTickSinceGameStart; sky.AddOneSecond();
                Require(ticks.Get_totalTickSinceGameStart == before + 1, "Offline minute path remained blocked.");
                phase = 6; Delay(1);
            }
            else if (phase == 7)
            {
                Require(state.Day == 1 && state.TotalTicks > 1000 && clock.TestPersonalTicks == 0, "Reconnect lost calendar or replayed elapsed personal needs.");
                world.Channel.ReplyReceived += SkipResult;
                fraction = state.DayFraction; Signal(3); phase = 8;
            }
            else if (phase == 8 && state.Flowing) phase = 9;
            else if (phase == 9 && !state.Flowing && state.DayFraction > fraction)
            {
                Require(sky.TimeFlow == UniStormSystem.EnableFeature.Disabled && Math.Abs(sky.m_TimeFloat - state.DayFraction) < 0.000001f,
                    "Guest native presentation clock did not follow live host flow.");
                BeginSkipProbe(); Signal(4); phase = 10;
            }
            else if (phase == 10 && SkipDrained(60))
            { CheckSkipMinutes(60, 0); Signal(5); phase = 11; }
            else if (phase == 11 && skipResult?[1] == 5)
            {
                CompareSkipResult(); BeginSkipProbe(); Signal(6); phase = 12;
            }
            else if (phase == 12 && SkipDrained(1440))
            { CheckSkipMinutes(1440, 48); Signal(7); phase = 13; }
            else if (phase == 13 && skipResult?[1] == 7)
            {
                CompareSkipResult(); needsApplied = clock.TestPersonalTicks; repairedRefills = skipRefills;
                repairNeeds = CaptureSkipResult(7); repairs = world.Channel.RepairsApplied;
                Require(world.Channel.RequestResync(SnapshotScope.Section(ClockState.Adapter), "Day skip refill no replay"), "Day skip clock repair request failed.");
                phase = 14;
            }
            else if (phase == 14 && world.Channel.RepairsApplied > repairs)
            {
                Require(clock.TestPersonalTicks == needsApplied && skipRefills == repairedRefills &&
                    CaptureSkipResult(7).SequenceEqual(repairNeeds!), "Repair repeated a day-skip minute/refill or changed personal needs.");
                BeginSkipProbe(); needs.GetAnxiety = 0.9999f; Signal(8); phase = 15;
            }
            else if (phase == 15 && state.TotalTicks >= skipStart + 6 && clock.TestPersonalTicks - skipPersonalStart == state.TotalTicks - skipStart)
            {
                CheckSkipMinutes(6, 0);
                CheckMixedSavingChronology();
                Require(state.TotalTicks == skipStart + 6 && needs.GetAnxiety < 0.999f && !gameplay.Get_IsDead,
                    "A saving-first mixed native burst was reordered into a hard-mode death.");
                Signal(9); phase = 16;
            }
            else if (phase == 16 && skipResult?[1] == 9)
            {
                CompareSkipResult(savingFirst: true); Signal(10); phase = 17;
            }
            else if (phase == 17 && skipResult?[1] == 10)
            {
                ClearSignalRetry(); world.Channel.ReplyReceived -= SkipResult; skipProbe = null; return true;
            }
            return false;
        }
        private bool Host(SharedWorld world)
        {
            if (phase == 0)
            {
                var item = (Item_Poutine)world.Items.TestSpawn(gameplay.PlayerWalking.transform.position + new Vector3(2, 1, 1), world.Items.TestPrefab("Food/Poutine"));
                item.Value1 = 0.8f; item.transform.localScale = Vector3.one * 0.64f; phase = 1;
            }
            else if (phase == 1 && acknowledgment >= 1)
            {
                ProbeLocalSyrupWarning();
                // Keep host thirst critical until the healthy guest has checked the replicated
                // clock and its own native warning image, rather than only testing a brief pulse.
                hostSyrupBaseline = needs.GetMappleSyrup;
                needs.GetMappleSyrup = 0.9f;
                for (int i = 0; i < 5; i++) sky.AddOneSecond(); phase = 2;
            }
            else if (phase == 2 && acknowledgment >= 2)
            {
                Require(minuteEvents >= 5 && dayEvents == 1 && ticks.Get_NumberOfDays == 5, "Host native minute/day events failed.");
                RestoreWarningFixture(); phase = 3;
            }
            else if (phase == 3 && plugin.Session!.Peers.Count == 1) phase = 4;
            else if (phase == 4 && acknowledgment >= 3)
            { sky.TimeFlow = UniStormSystem.EnableFeature.Enabled; phase = 5; Delay(1.5f); }
            else if (phase == 5)
            {
                sky.TimeFlow = UniStormSystem.EnableFeature.Disabled;
                phase = 6;
            }
            else if (phase == 6 && acknowledgment >= 4)
            { BeginSkipProbe(); sky.AddOneHour(); phase = 7; }
            else if (phase == 7 && !NativeSkipRunning && acknowledgment >= 5)
            {
                CheckSkipMinutes(60, 0); world.Channel.Reply(guest, ClockState.Entity, CaptureSkipResult(5)); phase = 8;
            }
            else if (phase == 8 && acknowledgment >= 6)
            { BeginSkipProbe(); sky.AddOneDay(); phase = 9; }
            else if (phase == 9 && !NativeSkipRunning && acknowledgment >= 7)
            {
                CheckSkipMinutes(1440, 48); world.Channel.Reply(guest, ClockState.Entity, CaptureSkipResult(7)); phase = 10;
            }
            else if (phase == 10 && acknowledgment >= 8)
            {
                BeginSkipProbe(); needs.GetAnxiety = 0.9999f;
                // Exercise the real native saving-minute callback, then ordinary minutes in
                // the same frame. This is a chronology probe; PC/handheld admission and
                // completion remain covered by PairedAnxietyGameplayTest's native actions.
                ticks.AddOneTickFromSave();
                for (int i = 0; i < 5; i++) sky.AddOneSecond();
                sky.Hour = (int)(sky.m_TimeFloat * 24f); sky.Minute = sky.GetCurrentTick % 60; sky._cacheMinute = sky.Minute;
                CheckSkipMinutes(6, 0); CheckMixedSavingChronology(); phase = 11;
            }
            else if (phase == 11 && acknowledgment >= 9)
            { world.Channel.Reply(guest, ClockState.Entity, CaptureSkipResult(9)); phase = 12; }
            else if (phase == 12 && acknowledgment >= 10)
            {
                world.Channel.Reply(guest, ClockState.Entity, CaptureSkipResult(10));
                skipProbe = null;
                sky.OnMinuteChangeEvent -= MinuteEvent; ticks.NewDayEvent -= DayEvent;
                world.Channel.CommandRequested -= Acknowledge; return true;
            }
            return false;
        }
        private void Delay(float seconds) => next = Time.unscaledTime + seconds;
        private bool NativeSkipRunning => (bool)AccessTools.Field(typeof(UniStormSystem), "coroutine_ThisAmountOfTick").GetValue(sky);
        private void BeginSkipProbe()
        {
            Require(gameplay.Get_PlayerState == PlayerState.Walking && !gameplay.Get_IsSleeping && !needs.Get_isSaving && !ticks.isSaving && !ComputerTimeScope.Active,
                "Native skip fixture requires walking/non-saving personal baselines; player=" + gameplay.Get_PlayerState +
                "; sleeping=" + gameplay.Get_IsSleeping + "; needsSaving=" + needs.Get_isSaving + "; tickSaving=" + ticks.isSaving + "; computerScope=" + ComputerTimeScope.Active);
            if (skipFixtureFields == null)
            {
                skipFixtureFields = SkipFloatFields.Concat(SkipTimerFields).Select(name => AccessTools.Field(typeof(BasicNeeds_Manager), name)).ToArray();
                skipFixtureValues = skipFixtureFields.Select(field => field.GetValue(needs)).ToArray();
            }
            // Both endpoints start with identical native personal conditions. Fill_Everything
            // intentionally leaves these elapsed timers/drug effects intact during a day skip.
            foreach (string name in SkipFloatFields) AccessTools.Field(typeof(BasicNeeds_Manager), name).SetValue(needs, SkipNeedBaseline);
            foreach (string name in SkipTimerFields) AccessTools.Field(typeof(BasicNeeds_Manager), name).SetValue(needs, name.StartsWith("LastTime_", StringComparison.Ordinal) ? SkipTimerBaseline : 0);
            skipStart = ticks.Get_totalTickSinceGameStart; skipPersonalStart = plugin.World!.Clock.TestPersonalTicks;
            var state = plugin.TestOptions.IsHost ? plugin.World.Clock.TestNative : plugin.World.Clock.TestState;
            skipComputerStart = state.ComputerTicks; skipSleepStart = state.SleepTicks;
            skipNeedsStart = LastAte(); skipRefills = 0; skipResult = null; skipProbe = this;
        }
        private bool SkipDrained(int amount)
        {
            var clock = plugin.World!.Clock; var state = clock.TestState;
            return state.FastForwardTarget == amount && !state.FastForwardRunning && state.TotalTicks >= skipStart + amount &&
                clock.TestPersonalTicks - skipPersonalStart == state.TotalTicks - skipStart && LastAte() - skipNeedsStart == state.TotalTicks - skipStart;
        }
        private void CheckSkipMinutes(int amount, int refills)
        {
            int elapsed = ticks.Get_totalTickSinceGameStart - skipStart;
            // Native Update can emit its own cached-minute event between coroutine yields.
            // Account for every actual host minute, rather than calling relief/refill helpers.
            Require(elapsed >= amount && LastAte() - skipNeedsStart == elapsed && skipRefills == refills && !needs.Dead && !gameplay.Get_IsDead,
                "Native " + amount + "-minute skip lost/duplicated elapsed work or refill callbacks: elapsed=" + elapsed + "; refills=" + skipRefills);
        }
        private byte[] CaptureSkipResult(byte stage)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)0xc7); writer.Write(stage); writer.Write(ticks.Get_totalTickSinceGameStart); writer.Write(skipStart); writer.Write(skipRefills);
                foreach (string name in SkipFloatFields) writer.Write((float)AccessTools.Field(typeof(BasicNeeds_Manager), name).GetValue(needs));
                foreach (string name in SkipTimerFields) writer.Write((int)AccessTools.Field(typeof(BasicNeeds_Manager), name).GetValue(needs));
                return stream.ToArray();
            }
        }
        private void CheckMixedSavingChronology()
        {
            var state = plugin.TestOptions.IsHost ? plugin.World!.Clock.TestNative : plugin.World!.Clock.TestState;
            Require(state.TotalTicks == skipStart + 6 && state.ComputerTicks == skipComputerStart + 1 && state.ComputerUser == 0 && state.SleepTicks == skipSleepStart && skipRefills == 0,
                "Mixed skip must contain one host-owned saving minute then five ordinary minutes: total=" + state.TotalTicks + "/start=" + skipStart +
                "; computer=" + state.ComputerTicks + "/start=" + skipComputerStart + "/user=" + state.ComputerUser +
                "; sleep=" + state.SleepTicks + "/start=" + skipSleepStart + "; refills=" + skipRefills);
        }
        private static float NonSavingFirstMinuteOffset(string name)
        {
            // Verified native BasicNeeds_Manager.Tick IL: the saving owner returns at
            // IL_0298, before syrup/food/energy additions. Other players retain their
            // personal decay. Drug/timer updates precede that return on both peers.
            // The fixture starts walking, with the separate isSaving backing flag false.
            float elapsed = SkipTimerBaseline + 1;
            if (name == "MappleSyrup") return 0.00125f * Mathf.Clamp(elapsed / 100f, 0.3f, 1f);
            if (name == "Poutine") return (1f + (SkipNeedBaseline - 0.005f) * 2.5f) / 1440f * Mathf.Clamp(elapsed / 200f, 0.5f, 1f);
            if (name == "Energy") return 0.00090277777f * Mathf.Clamp(elapsed / 150f, 0.6f, 1f);
            return 0;
        }
        private void CompareSkipResult(bool savingFirst = false)
        {
            using (var reader = new BinaryReader(new MemoryStream(skipResult!)))
            {
                reader.ReadByte(); byte stage = reader.ReadByte();
                int hostTotal = reader.ReadInt32(), hostStart = reader.ReadInt32(), hostRefills = reader.ReadInt32();
                var hostFloats = SkipFloatFields.Select(_ => reader.ReadSingle()).ToArray();
                var hostTimers = SkipTimerFields.Select(_ => reader.ReadInt32()).ToArray();
                var localFloats = SkipFloatFields.Select(name => (float)AccessTools.Field(typeof(BasicNeeds_Manager), name).GetValue(needs)).ToArray();
                var localTimers = SkipTimerFields.Select(name => (int)AccessTools.Field(typeof(BasicNeeds_Manager), name).GetValue(needs)).ToArray();
                var expected = SkipFloatFields.Select((name, index) => hostFloats[index] + (savingFirst ? NonSavingFirstMinuteOffset(name) : 0)).ToArray();
                string details = "stage=" + stage + "; total host/local=" + hostTotal + "/" + ticks.Get_totalTickSinceGameStart +
                    "; start host/local=" + hostStart + "/" + skipStart + "; refills host/local=" + hostRefills + "/" + skipRefills +
                    "; personalMinutes=" + (plugin.World!.Clock.TestPersonalTicks - skipPersonalStart) +
                    "; savingFirst=" + savingFirst + "; " + string.Join("; ", SkipFloatFields.Select((name, index) => name +
                        " host=" + hostFloats[index].ToString("R", CultureInfo.InvariantCulture) +
                        "/expected=" + expected[index].ToString("R", CultureInfo.InvariantCulture) +
                        "/local=" + localFloats[index].ToString("R", CultureInfo.InvariantCulture))) +
                    "; " + string.Join("; ", SkipTimerFields.Select((name, index) => name + " host/local=" + hostTimers[index] + "/" + localTimers[index]));
                Debug.Log("MONMP_CLOCK_SKIP_COMPARE: " + details);
                Require(hostTotal == ticks.Get_totalTickSinceGameStart && hostStart == skipStart && hostRefills == skipRefills,
                    "Host/guest native skip chronology or refill count differs: " + details);
                if (savingFirst) { Require(stage == 9, "Saving-first offsets applied outside the mixed six-minute probe."); CheckMixedSavingChronology(); }
                for (int i = 0; i < SkipFloatFields.Length; i++) Require(Math.Abs(expected[i] - localFloats[i]) < 0.00002f,
                    "Native skip personal need differs from its native owner/non-owner expectation: " + SkipFloatFields[i] + "; " + details);
                for (int i = 0; i < SkipTimerFields.Length; i++) Require(hostTimers[i] == localTimers[i],
                    "Native skip reset or duplicated a non-refilled elapsed timer: " + SkipTimerFields[i] + "; " + details);
            }
        }
        internal static void ObserveNativeRefill(BasicNeeds_Manager manager)
        { if (skipProbe != null && manager == skipProbe.needs) skipProbe.skipRefills++; }
        // Native Tick starts WarningText synchronously. Keep that visual alive across the host's
        // clock publish, but restore every scalar needs field immediately so counters/profile
        // samples and the existing exactly-once clock assertions are unchanged by this probe.
        private void ProbeLocalSyrupWarning()
        {
            Require(warningImages.All(image => image.color.a == 0), "A needs warning was already active before the local critical-stat probe.");
            warningBaseline = warningImages.Select(image => image.color).ToArray();
            var fields = typeof(BasicNeeds_Manager).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => f.FieldType == typeof(float) || f.FieldType == typeof(int) || f.FieldType == typeof(bool)).ToArray();
            var saved = fields.Select(f => f.GetValue(needs)).ToArray();
            try
            {
                needs.GetMappleSyrup = 0.9f;
                AccessTools.Field(typeof(BasicNeeds_Manager), "Last_SyrupWarning").SetValue(needs, -100000f);
                AccessTools.Field(typeof(BasicNeeds_Manager), "Last_Warning").SetValue(needs, -100000f);
                needs.Tick(false, false);
                Require(syrupWarning.color.a > 0.9f, "Local critical syrup Tick failed to start the native warning glow.");
            }
            finally
            {
                for (int i = 0; i < fields.Length; i++) fields[i].SetValue(needs, saved[i]);
                AccessTools.Method(typeof(BasicNeeds_Manager), "UpdateUI", Type.EmptyTypes).Invoke(needs, null);
            }
        }
        internal void RestoreWarningFixture()
        {
            ClearSignalRetry();
            if (skipFixtureFields != null)
            {
                for (int i = 0; i < skipFixtureFields.Length; i++) skipFixtureFields[i].SetValue(needs, skipFixtureValues![i]);
                skipFixtureFields = null; skipFixtureValues = null;
                AccessTools.Method(typeof(BasicNeeds_Manager), "UpdateUI", Type.EmptyTypes).Invoke(needs, null);
            }
            if (ReferenceEquals(skipProbe, this)) skipProbe = null;
            if (plugin.World != null) plugin.World.Channel.ReplyReceived -= SkipResult;
            if (hostSyrupBaseline.HasValue)
            {
                if (needs != null)
                {
                    needs.GetMappleSyrup = hostSyrupBaseline.Value;
                    AccessTools.Method(typeof(BasicNeeds_Manager), "UpdateUI", Type.EmptyTypes).Invoke(needs, null);
                }
                hostSyrupBaseline = null;
            }
            if (warningBaseline == null) return;
            // UI_BasicNeeds only runs its four warning coroutines. The probe requires a clear
            // warning baseline; stop the fixture's animation before putting its color back.
            if (needsUi != null) needsUi.StopAllCoroutines();
            for (int i = 0; i < warningImages.Length; i++)
                if (warningImages[i] != null) warningImages[i].color = warningBaseline[i];
            warningBaseline = null;
        }
        private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException("Clock test: " + reason); }
    }
    [HarmonyPatch(typeof(BasicNeeds_Manager), nameof(BasicNeeds_Manager.Fill_Everything))]
    internal static class PairedClockNativeRefillProbe
    {
        private static void Postfix(BasicNeeds_Manager __instance) => PairedClockTest.ObserveNativeRefill(__instance);
    }
}
