using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using MonMP.Plugin.Testing;
using NWH;
using NWH.DWP2.ShipController;
using NWH.VehiclePhysics2;
using UniStorm;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Diagnostics
{
    // Real native clock events, item use, smoke coroutine and seat admission in two
    // isolated processes. Ride motion is a controlled dynamic-body trajectory;
    // this tests the replicated speed/needs path, not engine or water handling.
    internal sealed class PairedAnxietyGameplayTest : IDisposable
    {
        private enum Activity { Wine, Smoke, TruckDriver, TruckCab, Cruiser, FishingBoat, Sleep, Crash, Computer, Handheld }
        private const int CaseCount = 20;
        private const byte Marker = 0xad;
        private readonly MonMPPlugin plugin;
        private readonly Gameplay game;
        private readonly BasicNeeds_Manager needs;
        private readonly WorldChannel channel;
        private readonly UniStormSystem sky;
        private readonly UniStormSystem.EnableFeature originalFlow;
        private readonly bool host, originalHard;
        private readonly Vector3 home;
        private readonly FieldInfo[] fields;
        private readonly object[] original;
        private readonly string table;
        private readonly Dictionary<Rigidbody, BodyState> bodies = new Dictionary<Rigidbody, BodyState>();
        private readonly List<string> failures = new List<string>();
        private readonly Dictionary<Activity, float> hostResults = new Dictionary<Activity, float>();
        private int number, stage, enteredNumber = -1, enteredStage = -1, peerNumber = -1, peerStage = -1;
        private int target, minuteBefore, personalBefore, step, peerFailures;
        private bool ready, peerReady, used, disposed;
        private float enteredAt, nextSend, nextMinute, before, after, spent, peerAfter, peerSpent, minSpeed, distance;
        private float smokeStart;
        private float actionAfter;
        private float adjustment, peerAdjustment;
        private float nextDoor;
        private float finishingAt;
        private Vector3 rideStart;
        private ulong item, cab;
        private ushort winePrefab;
        private Vehicle? vehicle;
        private Vehicle_DriverSeat? boatSeat;
        private WeedJoint? joint;
        private Furniture_Computer? computer;
        private Furniture_Chair? computerChair;
        private int computerBefore, actionMinutes, actionNativeBefore, actionPersonalBefore;
        private float expectedSkipAnxiety;
        private int expectedSkipMinutes;
        private ClockState clockObserved;
        private int clockEndpoints, savingEndpoints, clockEndpointsBefore, actionEndpointsBefore, actionSavingBefore;
        private static PairedAnxietyGameplayTest? active;
        private GameObject? wall;
        private AnxietyContactProbe? contact;
        private string? error;
        private Activity Kind => (Activity)(number / 2);
        private bool Actor => (number % 2 == 0) == host;
        private bool Ride => Kind >= Activity.TruckDriver && Kind <= Activity.FishingBoat;
        private bool Boat => Kind == Activity.Cruiser || Kind == Activity.FishingBoat;
        private bool SharedSkip => Kind == Activity.Computer || Kind == Activity.Handheld;
        // SharedSceneFurniture admits completion after 160 accepted saving ticks;
        // native requests can exceed that count and are rate-limited by the host.
        private int MinimumSaveTicks => Kind == Activity.Computer ? 160 : 120;
        private int Duration => Ride ? 10 : Kind == Activity.Crash || SharedSkip ? 0 : 120;
        private int MinuteCount => (int)AccessTools.Field(typeof(BasicNeeds_Manager), "LastTime_Ate").GetValue(needs);
        private int HostTicks => Singleton<TickSystem>.i.Get_totalTickSinceGameStart;
        internal string Status => "anxiety gameplay case=" + number + "/" + CaseCount + " " + Kind + " stage=" + stage + " step=" + step +
            " local=" + game.Get_PlayerState + " ready=" + ready + " peer=" + peerNumber + ":" + peerStage + ":" + peerReady +
            " anxiety=" + needs.GetAnxiety.ToString("F5") + " saving=" + needs.Get_isSaving + " savePending=" + plugin.World!.Saves?.Pending + " pc=" + (computer == null ? "-" : AccessTools.Field(typeof(Furniture_Computer), "_State").GetValue(computer).ToString()) + " computerMinutes=" + (plugin.World.Clock.TestState.ComputerTicks - computerBefore) + " ticks=" + plugin.World!.Clock.TestState.TotalTicks + "/" + target;

        internal PairedAnxietyGameplayTest(MonMPPlugin plugin, Gameplay game, string resultPath)
        {
            if (!LocalTestIsolation.Installed || !plugin.TestOptions.Enabled) throw new InvalidOperationException("Isolated fixture required.");
            this.plugin = plugin; this.game = game; host = plugin.TestOptions.IsHost;
            active = this;
            string? selection = Environment.GetCommandLineArgs().FirstOrDefault(arg => arg.StartsWith("--monmp-anxiety-start=", StringComparison.Ordinal));
            if (selection != null)
            {
                if (!int.TryParse(selection.Substring("--monmp-anxiety-start=".Length), out int activity) || activity < 0 || activity > 9)
                    throw new InvalidOperationException("Anxiety start activity must be 0 through 9.");
                number = activity * 2;
            }
            needs = game.GetComponent<BasicNeeds_Manager>(); channel = plugin.World!.Channel; sky = UniStormSystem.Instance;
            originalFlow = sky.TimeFlow; originalHard = game.Get_isHardMode; home = game.PlayerWalking.transform.position;
            fields = typeof(BasicNeeds_Manager).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(f => !f.IsInitOnly && (f.FieldType == typeof(float) || f.FieldType == typeof(int) || f.FieldType == typeof(bool))).ToArray();
            original = fields.Select(f => f.GetValue(needs)).ToArray();
            table = Path.Combine(Path.GetDirectoryName(resultPath)!, "anxiety-gameplay.tsv");
            File.WriteAllText(table, "case\tactor\tlocal_role\tbefore_action\tafter_action\tafter_minutes\tnative_minutes\tguest_replayed_minutes\tnative_speed\tdisplacement\tpuff_seconds\tchecks\treceived_clock_minutes_total\treceived_saving_minutes_total\n");
            clockObserved = plugin.World.Clock.TestState;
            if (!host) channel.Changed += ObserveClockEndpoint;
            if (host) channel.CommandRequested += Command; else channel.ReplyReceived += Reply;
            enteredAt = Time.unscaledTime;
        }

        private byte[] Packet(bool report)
        {
            using (var stream = new MemoryStream()) using (var w = new BinaryWriter(stream))
            {
                w.Write(Marker); w.Write(report); w.Write(number); w.Write(stage);
                if (report) { w.Write(ready); w.Write(after); w.Write(spent); w.Write(failures.Count); w.Write(adjustment); }
                else w.Write(target);
                return stream.ToArray();
            }
        }
        private void Command(uint peer, ulong entity, byte[] data)
        {
            if (peer == 0 || entity != ClockState.Entity || data.Length != 27 || data[0] != Marker) return;
            try
            {
                using (var r = new BinaryReader(new MemoryStream(data)))
                { r.ReadByte(); if (!r.ReadBoolean()) return; peerNumber = r.ReadInt32(); peerStage = r.ReadInt32();
                    peerReady = r.ReadBoolean(); peerAfter = r.ReadSingle(); peerSpent = r.ReadSingle(); peerFailures = r.ReadInt32(); peerAdjustment = r.ReadSingle(); }
                channel.Reply(peer, entity, Packet(false));
            }
            catch (Exception ex) { error = ex.ToString(); }
        }
        private void Reply(ulong entity, byte[] data)
        {
            if (entity != ClockState.Entity || data.Length != 14 || data[0] != Marker) return;
            try
            {
                using (var r = new BinaryReader(new MemoryStream(data)))
                { r.ReadByte(); if (r.ReadBoolean()) return; number = r.ReadInt32(); stage = r.ReadInt32(); target = r.ReadInt32(); }
            }
            catch (Exception ex) { error = ex.ToString(); }
        }
        private void Check(bool condition, string message)
        {
            if (condition) return;
            string text = number + "/" + stage + " " + Kind + ": " + message;
            if (!failures.Contains(text)) { failures.Add(text); File.AppendAllText(table, "FAIL\t" + text + "\n"); }
        }
        private void ObserveClockEndpoint(WorldRecord record)
        {
            if (record.Id != ClockState.Entity || record.Adapter != ClockState.Adapter) return;
            var state = ClockState.Decode(record.Payload);
            int minutes = state.TotalTicks - clockObserved.TotalTicks;
            int saving = state.ComputerTicks - clockObserved.ComputerTicks;
            int sleeping = state.SleepTicks - clockObserved.SleepTicks;
            Check(record.Payload.Length == 49 && minutes >= 0 && minutes <= 1 && saving >= 0 && sleeping >= 0 && saving + sleeping <= minutes,
                "Live native clock endpoints were coalesced, reordered, or lost their saving/sleep attribution");
            clockEndpoints += Math.Max(0, minutes); savingEndpoints += Math.Max(0, saving); clockObserved = state;
        }
        private void Move(Vector3 point)
        {
            game.Get_Player_Rigidbody.velocity = Vector3.zero;
            game.PlayerWalking.transform.position = point; game.Get_Player_Rigidbody.position = point;
        }
        private void Normalize()
        {
            game.Get_isHardMode = false; // Native Hard Mode kills a player at 100%.
            needs.GetAnxiety = Kind == Activity.Crash ? .4f : .9995f;
            needs.GetPoutine = needs.GetMappleSyrup = needs.GetEnergy = .2f;
            needs.GetTHCLevel = needs.GetDrunkness = 0; needs.Dead = false;
            foreach (string name in new[] { "EnergyDrinkInBlood", "HangoverDebuff", "DeathDebuff", "Cooldown_Vomit" })
                AccessTools.Field(typeof(BasicNeeds_Manager), name).SetValue(needs, 0);
        }
        private void Enter()
        {
            enteredNumber = number; enteredStage = stage; enteredAt = Time.unscaledTime; ready = false; used = false; step = 0;
            nextDoor = 0;
            if (stage == 0)
            {
                Normalize(); Move(home + (host ? Vector3.right : Vector3.left) * 15);
                vehicle = null; boatSeat = null; item = 0; spent = adjustment = 0; minSpeed = Ride ? float.MaxValue : 0; distance = 0;
                if (Kind == Activity.Wine)
                {
                    winePrefab = plugin.Items!.TestPrefabs(typeof(Item_MapleWineBottle)).Single();
                    if (host) plugin.Items.TestSpawn(home + Vector3.up, winePrefab).transform.localScale = Vector3.one * (.731f + number * .001f);
                }
                if (Ride || Kind == Activity.Crash)
                {
                    plugin.Vehicles!.TestType = Vehicle_Type.OlTruck;
                    if (Boat)
                    {
                        var type = Kind == Activity.Cruiser ? Vehicle_Type.SmallCruiser : Vehicle_Type.FishingBoat;
                        vehicle = Object.FindObjectsOfType<AdvancedShipController>(true).Single(v => v.vehicle_type == type);
                        boatSeat = vehicle.GetComponentsInChildren<Vehicle_DriverSeat>(true).Single(v => v.vehicleController == vehicle);
                        if (host) AccessTools.Method(typeof(ToDoList_Manager), "ToDoListCompleted").Invoke(Singleton<ToDoList_Manager>.i,
                            new object[] { Kind == Activity.Cruiser ? ToDoList_Line.Boat_SmallCruiserBought : ToDoList_Line.Boat_Fishing_Bought });
                    }
                    else vehicle = plugin.Vehicles.TestVehicle;
                    cab = plugin.World!.ExtraSeats.TestSeats(SharedExtraSeats.SeatKind.Cab).First();
                    var body = vehicle.vehicleRigidbody;
                    if (!bodies.ContainsKey(body)) bodies.Add(body, new BodyState(body));
                    if (host)
                    {
                        body.position = bodies[body].Position + Vector3.up * 150; body.rotation = bodies[body].Rotation;
                        vehicle.transform.SetPositionAndRotation(body.position, body.rotation);
                        if (vehicle is AdvancedShipController ship) ship.Anchor.Weigh();
                    }
                    // Only controlled-physics rides/crash staging change these flags.
                    body.useGravity = false; body.constraints = RigidbodyConstraints.FreezeRotation;
                    body.detectCollisions = Kind == Activity.Crash;
                    if (vehicle is VehicleController car)
                    { car.vehicle_PartsManager.ParkingBrake.LoadFromSave = 1; car.brakes.handbrakeValue = 1; }
                }
            }
            else if (stage == 1 || stage == 3)
            {
                minuteBefore = MinuteCount; personalBefore = plugin.World!.Clock.TestPersonalTicks;
                clockEndpointsBefore = clockEndpoints;
                if (stage == 3)
                {
                    if (!Actor && !SharedSkip) Check(Math.Abs(needs.GetAnxiety - before) < .00002f, "Other player's action changed this observer's anxiety");
                    actionAfter = after = needs.GetAnxiety; rideStart = vehicle != null ? vehicle.transform.position : home;
                }
            }
            else if (stage == 2)
            {
                before = needs.GetAnxiety; rideStart = vehicle != null ? vehicle.transform.position : home;
                computerBefore = plugin.World!.Clock.TestState.ComputerTicks;
                actionNativeBefore = MinuteCount; actionPersonalBefore = plugin.World.Clock.TestPersonalTicks;
                actionEndpointsBefore = clockEndpoints; actionSavingBefore = savingEndpoints;
                expectedSkipAnxiety = before; expectedSkipMinutes = 0;
            }
        }

        internal bool Tick()
        {
            if (error != null) throw new InvalidOperationException(error);
            if (plugin.World == null) throw new InvalidOperationException("Shared world closed before the anxiety fixture completed.");
            if (host) sky.TimeFlow = UniStormSystem.EnableFeature.Disabled;
            if (plugin.World!.Clock.TestState.Flowing || !plugin.World.PersonalProfileReady) return false;
            if (!host && Time.unscaledTime >= nextSend)
            { channel.Request(ClockState.Entity, Packet(true)); nextSend = Time.unscaledTime + .1f; }
            if (number >= CaseCount)
            {
                // The host must send the final case number before closing its
                // session. Keep the last report alive long enough to flush too.
                if (host && peerNumber < CaseCount) return false;
                if (finishingAt == 0) finishingAt = Time.unscaledTime + (host ? 1.5f : .5f);
                if (Time.unscaledTime < finishingAt) return false;
                if (failures.Count != 0 || host && peerFailures != 0)
                    throw new InvalidOperationException("Scripted anxiety mismatches: local=" + failures.Count + ", peer=" + peerFailures + "; see anxiety-gameplay.tsv. " + string.Join(" | ", failures));
                return true;
            }
            if (enteredNumber != number || enteredStage != stage) Enter();
            if (Time.unscaledTime - enteredAt > 90) throw new InvalidOperationException("Scripted anxiety timed out: " + Status);
            MaintainMotion();
            if (stage == 0) ready = Setup();
            else if (stage == 1 || stage == 3) ClockPhase();
            else if (stage == 2) ready = Act();
            else if (stage == 4) ready = Cleanup();
            if (host && ready && peerReady && peerNumber == number && peerStage == stage)
            {
                if (stage == 4)
                {
                    float actorAfter = Actor ? after : peerAfter;
                    if (Actor) hostResults[Kind] = actorAfter + adjustment;
                    else if (Kind != Activity.Crash)
                        Check(Math.Abs(hostResults[Kind] - actorAfter - peerAdjustment) < .001f,
                            "Host/client result differs: " + hostResults[Kind] + " vs " + actorAfter);
                    number++; stage = 0;
                }
                else { stage++; if (stage == 1 || stage == 3) target = HostTicks + (stage == 1 ? 5 : Duration); }
            }
            return false;
        }
        private bool Setup()
        {
            if (Kind == Activity.Computer)
            {
                computer ??= Object.FindObjectsOfType<Furniture_Computer>(true).Where(pc => pc.Get_House_Location != House_Location.Roulotte && pc.gameObject.activeInHierarchy)
                    .OrderBy(pc => RuntimeProbe.CanonicalPath(pc.transform), StringComparer.Ordinal).First();
                computerChair ??= Object.FindObjectsOfType<Furniture_Chair>(true).Where(chair => !plugin.World!.ExtraSeats.Handles(chair) && chair.gameObject.activeInHierarchy)
                    .OrderBy(chair => Vector3.Distance(chair.Get_SeatLocation.position, computer.transform.position)).First();
                if (Actor && !used)
                { Move(computer.transform.position + Vector3.up * .2f); if (!computer.Get_IsOn) computer.PowerOn(); used = true; }
                if ((Computer_State)AccessTools.Field(typeof(Furniture_Computer), "_State").GetValue(computer) != Computer_State.isOn) return false;
            }
            if (Kind == Activity.Handheld)
            {
                var shared = plugin.World!.PortableConsoleCharger;
                if (shared.TestState.Location != PortableConsoleLocation.Hand)
                {
                    if (Actor && !used)
                    { var charger = Object.FindObjectsOfType<Furniture_PortableGameConsole_Charger>(true).Single(c => c.isConsoleCharging);
                        Move(charger.transform.position + Vector3.up * .2f); charger.MouseDown(); used = true; }
                    return false;
                }
                if (host)
                {
                    game.Player.Get_VisualHands.GameConsole.Get_BatteryLife = 3;
                    game.Player.Get_VisualHands.GameConsole.Battery_Charging = 0;
                }
            }
            if (host && vehicle is VehicleController)
            {
                var doors = plugin.Vehicles!.Doors;
                if (!used || Time.unscaledTime >= nextDoor)
                {
                    vehicle.Wake();
                    Move(doors.TestPosition + Vector3.right); if (!doors.TestState().Open) doors.TestClick();
                    Move(doors.TestPositionFor(false) + Vector3.right); if (!doors.TestState(false).Open) doors.TestClick(false);
                    used = true; nextDoor = Time.unscaledTime + .5f; Move(home + Vector3.right * 15);
                }
                if (vehicle.isDriverDoorClosed && vehicle.isPassengerDoorClosed) return false;
            }
            if (Kind == Activity.Wine)
            { item = plugin.Items!.TestTaggedItem(winePrefab, .731f + number * .001f); if (item == 0) return false; }
            return Time.unscaledTime - enteredAt > 1.2f;
        }
        private void ClockPhase()
        {
            bool otherEntered = peerNumber == number && peerStage == stage;
            if (host && otherEntered && Time.unscaledTime >= nextMinute && HostTicks < target)
            {
                sky.AddOneSecond();
                // AddOneSecond emits its native event without updating the frame
                // cache. Keep Update from emitting the same manual minute again.
                sky.Hour = (int)(sky.m_TimeFloat * 24f);
                sky.Minute = (int)(sky.m_TimeFloat * 24f * 60f) % 60;
                sky._cacheMinute = sky.Minute;
                nextMinute = Time.unscaledTime + .045f;
            }
            if (plugin.World!.Clock.TestState.TotalTicks < target || MinuteCount - minuteBefore < (stage == 1 ? 5 : Duration)) return;
            if (!host && plugin.World.Clock.TestPersonalTicks - personalBefore < (stage == 1 ? 5 : Duration)) return;
            if (ready) return;
            if (stage == 3 && SharedSkip)
            {
                actionMinutes = plugin.World.Clock.TestState.ComputerTicks - computerBefore;
                if (actionMinutes < MinimumSaveTicks || MinuteCount - actionNativeBefore < actionMinutes ||
                    !host && plugin.World.Clock.TestPersonalTicks - actionPersonalBefore < actionMinutes) return;
                Check(expectedSkipMinutes == MinuteCount - actionNativeBefore, "Native saving-minute probe missed a tick");
                if (!host) Check(clockEndpoints - actionEndpointsBefore == MinuteCount - actionNativeBefore &&
                    savingEndpoints - actionSavingBefore == actionMinutes,
                    "Received FIFO saving endpoints do not match exactly-once native personal replay");
                float expected = expectedSkipAnxiety;
                // Native save lengths vary and can hit zero. Compare residual
                // error against each actual duration, not an unclamped offset.
                adjustment = -expected;
                Check(Math.Abs(needs.GetAnxiety - expected) < .0005f,
                    "Shared " + Kind + " relief=" + needs.GetAnxiety + ", elapsed=" + actionMinutes);
                after = needs.GetAnxiety;
                File.AppendAllText(table, Kind + "\t" + (number % 2 == 0 ? "host" : "client") + "\t" + (host ? "host" : "client") + "\t" + F(before) + "\t" + F(after) + "\t" + F(after) + "\t" + actionMinutes + "\t" + (plugin.World.Clock.TestPersonalTicks - actionPersonalBefore) + "\t0\t0\t0\t" + failures.Count + "\t" + clockEndpoints + "\t" + savingEndpoints + "\n");
                ready = true; return;
            }
            int elapsed = MinuteCount - minuteBefore;
            Check(elapsed == (stage == 1 ? 5 : Duration), "Native needs minute count=" + elapsed);
            if (!host) Check(clockEndpoints - clockEndpointsBefore == elapsed, "Native minute replay differs from received FIFO endpoint count");
            if (stage == 1) Check(Math.Abs(needs.GetAnxiety - (Kind == Activity.Crash ? .4f + 5 * .00020833334f : 1f)) < .00002f, "Did not reach expected live starting anxiety");
            else
            {
                float expected = Actor && Ride ? before - .01f * Duration + .00020833334f * Duration :
                    Actor && Kind == Activity.Sleep ? after : Mathf.Clamp01(after + .00020833334f * Duration);
                Check(Math.Abs(needs.GetAnxiety - expected) < .0005f, "After native minutes=" + needs.GetAnxiety + ", expected=" + expected);
                if (Ride && Actor) Check(distance > 1, "Ride lacked measured displacement: " + distance);
                after = needs.GetAnxiety;
                if (Kind == Activity.Smoke && Actor) adjustment = spent / 20;
                File.AppendAllText(table, Kind + "\t" + (number % 2 == 0 ? "host" : "client") + "\t" + (host ? "host" : "client") + "\t" + F(before) + "\t" + F(actionAfter) + "\t" + F(after) + "\t" + elapsed + "\t" + (plugin.World.Clock.TestPersonalTicks - personalBefore) + "\t" + F(minSpeed) + "\t" + F(distance) + "\t" + F(spent) + "\t" + failures.Count + "\t" + clockEndpoints + "\t" + savingEndpoints + "\n");
            }
            ready = true;
        }
        private bool Act()
        {
            if (!Actor) return true;
            if (Kind == Activity.Computer)
            {
                if (step == 0) { Move(computerChair!.transform.position + Vector3.up * .2f); computerChair.MouseDown(); step++; return false; }
                if (step == 1)
                { if (game.Get_PlayerState != PlayerState.InFurnitureChair || Time.unscaledTime - enteredAt < 1.2f) return false;
                    computer!.EnterSaveMode(); step++; return false; }
                return !needs.Get_isSaving && plugin.World!.Clock.TestState.ComputerTicks - computerBefore >= MinimumSaveTicks &&
                    plugin.World.Saves?.Pending != true;
            }
            if (Kind == Activity.Handheld)
            {
                if (step == 0)
                { AccessTools.Method(typeof(PlayerVisualHands), "GameConsole_Toggle").Invoke(game.Player.Get_VisualHands, new object[] { false }); step++; return false; }
                if (step == 1)
                { if (!game.Player.Get_VisualHands.Get_isGameConsoleOpen || Time.unscaledTime - enteredAt < 1) return false;
                    game.Player.Get_VisualHands.GameConsole.Default_Game.SaveMonBazou(); step++; return false; }
                return !needs.Get_isSaving && !plugin.World!.PortableConsoleCharger.ConsoleSaveInProgress &&
                    plugin.World.Clock.TestState.ComputerTicks - computerBefore >= 120 && plugin.World.Saves?.Pending != true;
            }
            if (Kind == Activity.Wine)
            {
                if (!used) { Move(plugin.Items!.TestItem(item)!.transform.position + Vector3.right); used = true; return false; }
                if (Time.unscaledTime - enteredAt < .8f) return false;
                if (step++ == 0) plugin.Items!.TestUse(item);
                if (plugin.Items!.TestState(item).Exists || needs.IsInvoking("Alchool_FinishedDrinking")) return false;
                Check(Math.Abs(needs.GetAnxiety - (before - .25f)) < .00002f, "Wine use did not reduce actor anxiety by 25 points");
                return true;
            }
            if (Kind == Activity.Smoke)
            {
                joint ??= Object.FindObjectOfType<WeedJoint>();
                if (step == 0) { GameManager.Get_WeedJointAmount += 1; Invoke(joint, "SmokeKeyDown"); step++; return false; }
                if (step == 1)
                {
                    if (Time.unscaledTime - enteredAt < 1.3f) return false;
                    smokeStart = (float)AccessTools.Field(typeof(WeedJoint), "CurrentJointLeft").GetValue(joint);
                    Invoke(joint, "SmokeKeyDown"); step++; return false;
                }
                spent = smokeStart - (float)AccessTools.Field(typeof(WeedJoint), "CurrentJointLeft").GetValue(joint);
                if (spent < 5) return false;
                Invoke(joint, "SmokeKeyUp"); Check(Math.Abs(needs.GetAnxiety - before + spent / 20f) < .0001f, "Smoke coroutine did not apply puff relief");
                return true;
            }
            if (Kind == Activity.Sleep)
            {
                if (!used)
                {
                    var bed = Object.FindObjectsOfType<Furniture_Bed>(true).First(b => b.GetType() == typeof(Furniture_Bed) && b.gameObject.activeInHierarchy);
                    Move(bed.transform.position + Vector3.up * .2f); bed.MouseDown(); used = true;
                }
                return game.Get_IsSleeping && plugin.World!.Sleep.LocalWaiting;
            }
            if (vehicle == null) return false;
            bool seated = Kind == Activity.TruckCab ? plugin.World!.ExtraSeats.TestSeatedHere(cab) : game.Get_PlayerState == PlayerState.InVehicle_Driver && game.Get_CurrentVehicleController == vehicle;
            if (!seated)
                Move((Kind == Activity.TruckCab ? plugin.World!.ExtraSeats.TestAnchor(cab).position : Boat ? boatSeat!.transform.position : plugin.Vehicles!.TestSeat) + Vector3.up * .2f);
            if (!used)
            {
                Move((Kind == Activity.TruckCab ? plugin.World!.ExtraSeats.TestAnchor(cab).position : Boat ? boatSeat!.transform.position : plugin.Vehicles!.TestSeat) + Vector3.up * .2f);
                used = true; return false;
            }
            if (Time.unscaledTime - enteredAt < 1.2f) return false;
            if (!seated)
            {
                if (step == 0 || Time.unscaledTime >= nextDoor)
                { if (Kind == Activity.TruckCab) plugin.World!.ExtraSeats.Click(plugin.World.ExtraSeats.TestChair(cab)); else if (Boat) boatSeat!.MouseDown(); else plugin.Vehicles!.TestEnter(); step++; nextDoor = Time.unscaledTime + .5f; }
                return false;
            }
            if (Kind == Activity.Crash)
            {
                if (wall == null)
                {
                    wall = GameObject.CreatePrimitive(PrimitiveType.Cube); wall.name = "MonMPAnxietyContactWall";
                    wall.transform.position = vehicle.transform.position + vehicle.transform.forward * 9;
                    wall.transform.rotation = vehicle.transform.rotation; wall.transform.localScale = new Vector3(12, 12, 1);
                    contact = vehicle.gameObject.AddComponent<AnxietyContactProbe>();
                    vehicle.vehicleRigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                    return false;
                }
                if (contact?.Hit != true) return false;
                Check(needs.GetAnxiety > before + .01f, "Physical crash did not raise driver's anxiety"); return true;
            }
            return Time.unscaledTime - enteredAt > 3f;
        }
        private void MaintainMotion()
        {
            if (vehicle == null) return;
            bool drive = Kind == Activity.Crash ? stage == 2 && wall != null && contact?.Hit != true : stage == 3 || stage == 2 && step > 0;
            bool authority = Boat ? host : Kind == Activity.TruckCab ? host : Actor;
            if (authority && !vehicle.vehicleRigidbody.isKinematic)
            {
                float speed = drive ? Kind == Activity.Crash ? 15 : 8 : 0;
                if (vehicle is VehicleController car)
                { car.vehicle_PartsManager.ParkingBrake.LoadFromSave = speed > 0 ? 0 : 1; car.brakes.handbrakeValue = speed > 0 ? 0 : 1; }
                vehicle.vehicleRigidbody.velocity = vehicle.transform.forward * speed;
                vehicle.vehicleRigidbody.angularVelocity = Vector3.zero;
            }
            if (stage == 3 && Ride && Actor)
            { minSpeed = Math.Min(minSpeed, vehicle.Speed); distance = Math.Max(distance, Vector3.Distance(rideStart, vehicle.transform.position)); }
        }
        private bool Cleanup()
        {
            if (Actor && !used)
            {
                if (joint != null) { Invoke(joint, "SmokeKeyUp"); joint.IfJointInHandTrashIt(); }
                used = true;
            }
            // Native entry/motion can close the doors. Exit refuses while both
            // doors are closed, so open the real shared door before requesting it.
            if (Actor && Time.unscaledTime - enteredAt > .6f && (game.Get_PlayerState != PlayerState.Walking || game.Get_IsSleeping))
            {
                if (vehicle is VehicleController && vehicle.isDriverDoorClosed && vehicle.isPassengerDoorClosed)
                {
                    if (Time.unscaledTime >= nextDoor)
                    { plugin.Vehicles!.Doors.TestClick(); nextDoor = Time.unscaledTime + .5f; }
                    return false;
                }
                if (Kind == Activity.Sleep) plugin.World!.Sleep.RequestGetUp();
                else if (Kind == Activity.Computer) computerChair!.MouseDown();
                else if (Kind == Activity.TruckCab) plugin.World!.ExtraSeats.Click(plugin.World.ExtraSeats.TestChair(cab));
                else if (vehicle != null) { if (Boat) boatSeat!.MouseDown(); else plugin.Vehicles!.TestExit(); }
            }
            if (game.Get_PlayerState != PlayerState.Walking || game.Get_IsSleeping) return false;
            if (wall != null) { Object.Destroy(wall); wall = null; }
            if (contact != null) { Object.Destroy(contact); contact = null; }
            return Time.unscaledTime - enteredAt > 1;
        }
        private static void Invoke(object? obj, string method) => AccessTools.Method(obj!.GetType(), method).Invoke(obj, null);
        private static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);
        internal static void ObserveMinute(BasicNeeds_Manager manager, bool sleeping, bool saving)
        {
            var test = active;
            if (test == null || manager != test.needs || !test.SharedSkip || test.stage != 2 && test.stage != 3) return;
            test.expectedSkipMinutes++;
            if (sleeping) return;
            // Follow the actual event order: ordinary minutes at 100% are
            // clipped before later saving relief. Aggregating both loses that.
            test.expectedSkipAnxiety = Mathf.Clamp01(test.expectedSkipAnxiety +
                (ComputerTimeScope.Active || saving ? -.0029166667f : .00020833334f));
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true; channel.CommandRequested -= Command; channel.ReplyReceived -= Reply; channel.Changed -= ObserveClockEndpoint;
            if (ReferenceEquals(active, this)) active = null;
            if (joint != null) { Invoke(joint, "SmokeKeyUp"); joint.IfJointInHandTrashIt(); }
            if (wall != null) Object.Destroy(wall);
            if (contact != null) Object.Destroy(contact);
            foreach (var pair in bodies) pair.Value.Restore(pair.Key);
            for (int i = 0; i < fields.Length; i++) fields[i].SetValue(needs, original[i]);
            game.Get_isHardMode = originalHard; if (host) sky.TimeFlow = originalFlow;
        }
        private sealed class BodyState
        {
            internal readonly Vector3 Position, Velocity, Angular;
            internal readonly Quaternion Rotation;
            private readonly bool gravity, collisions;
            private readonly RigidbodyConstraints constraints;
            private readonly CollisionDetectionMode detection;
            internal BodyState(Rigidbody body)
            { Position = body.position; Rotation = body.rotation; Velocity = body.velocity; Angular = body.angularVelocity;
                gravity = body.useGravity; collisions = body.detectCollisions; constraints = body.constraints; detection = body.collisionDetectionMode; }
            internal void Restore(Rigidbody body)
            { body.position = Position; body.rotation = Rotation; body.velocity = Velocity; body.angularVelocity = Angular;
                body.useGravity = gravity; body.detectCollisions = collisions; body.constraints = constraints; body.collisionDetectionMode = detection; }
        }
    }
    internal sealed class AnxietyContactProbe : MonoBehaviour
    {
        internal bool Hit;
        private void OnCollisionEnter(Collision collision) { if (collision.collider.name == "MonMPAnxietyContactWall") Hit = true; }
    }
    [HarmonyPatch(typeof(BasicNeeds_Manager), nameof(BasicNeeds_Manager.Tick))]
    internal static class PairedAnxietyMinuteProbe
    {
        [HarmonyPrefix, HarmonyPriority(Priority.Last)]
        private static void Prefix(BasicNeeds_Manager __instance, bool _isSleeping, bool _isSaving)
            => PairedAnxietyGameplayTest.ObserveMinute(__instance, _isSleeping, _isSaving);
    }
}
