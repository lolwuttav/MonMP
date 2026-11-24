using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // PN-1/PN-2: native admission, AI drive to the line and countdown run normally.
    // The fixture invokes native start/finish callbacks; physical driving and checkpoint
    // traversal are outside this suite, and no postcondition arrays/state are injected.
    internal sealed class PairedMeetTest : IDisposable
    {
        private const int StartingCash = 1000000; // Native cents: $10,000.
        private const int Stake = 100000; // Tom's native $1,000 stake.
        private const byte Control = 238;
        private static readonly FieldInfo OpenAt = Field(typeof(Store_MeetManager), "PositionWhileOpen");
        private static readonly FieldInfo ClosedAt = Field(typeof(Store_MeetManager), "PositionWhileClose");
        private static readonly FieldInfo ListBuilt = Field(typeof(UI_GenericWindow_MeetsManager), "listBuilt");
        private static readonly FieldInfo Operation = Field(typeof(SharedMeetRace), "nextOperation");
        private static readonly FieldInfo HoldingLease = Field(typeof(SharedMeetRace), "holdingLease");
        private static readonly FieldInfo Timer = Field(typeof(RaceManager), "CurrentRace_Timer");
        private static readonly FieldInfo Detector = Field(typeof(RaceManager), "PlayerDetector");
        private static readonly MethodInfo BetDone = Method(typeof(UI_GenericWindow_MeetsManager), "Bet_Done");
        private static readonly MethodInfo ClaimReward = Method(typeof(UI_GenericWindow_MeetsManager), "ClaimReward");
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly Store_MeetManager meet;
        private readonly UI_GenericWindow_MeetsManager window;
        private readonly UI_GenericWindowManager windows;
        private readonly Cash_Manager cash;
        private readonly WorldChannel channel;
        private readonly SharedMeetRace race;
        private readonly Actor_Anthony anthony;
        private readonly HashSet<ulong> originalRewards;
        private readonly ushort rewardPrefab;
        private readonly Dictionary<uint, byte> admissions = new Dictionary<uint, byte>();
        private readonly Dictionary<uint, (byte Status, ulong Item, int Count)> rewards = new Dictionary<uint, (byte, ulong, int)>();
        private readonly float spentBefore, receivedBefore;
        private readonly Vector3 originalVehiclePosition;
        private readonly Quaternion originalVehicleRotation;
        private int phase, guestStage, hostAck, driverEntryStep, driverEntryAttempts, driverDoorAttempts;
        private uint guestPeer, admissionOperation, rewardOperation, duplicateRewardOperation, lease;
        private ulong rewardItem;
        private float deadline, next, finishTime, driverEntryStarted;
        private bool disposed;

        internal string Evidence => "Meet: native host opening/closing presentation, guest $1,000 native bet and one shared debit, repeated same/different native bets and command admission, owned Konig entry and native countdown, one native finish callback with repeated result commands, canonical host/guest record and streak, one host-created native reward despite repeated claims, native spent-daily-bet refund, and a second native lease refunded on disconnect passed. The fixture positions the owned Konig at the start and invokes native start/finish callbacks; physical driving/checkpoint traversal, achievements and disk save/reload remain manual.";
        internal string Status => "meet phase=" + phase + "; guestStage=" + guestStage + "; ack=" + hostAck +
            "; open=" + meet.isOpen + "; raised=" + meet.Get_RaiseCarDone + "; cash=" + cash.GetCash +
            "; racer=" + race.TestRacer + "; holding=" + race.TestHolding + "; refused=" + race.TestRefused +
            "; car=" + race.TestCar(2)?.Get_Car_State + "; racing=" + meet.Get_RaceManager.Get_IsRacing +
            "; samples=" + race.TestSamples + "; nativeBet=" + meet.Get_BetIsDoneToday[2] +
            "; record=" + GameManager.Get_Meet_Race_BestTimes[2] + "; streak=" + GameManager.Get_RaceWonInARow[0] +
            "; entry=" + DriverEntryStatus();

        // Isolated baselines, before SharedWorld captures the initial native records.
        internal static void Prepare(bool host)
        {
            var sky = UniStorm.UniStormSystem.Instance;
            if (sky != null) sky.TimeFlow = UniStorm.UniStormSystem.EnableFeature.Disabled;
            UnityEngine.Object.FindObjectOfType<Cash_Manager>().LoadSaveSystem(host ? StartingCash : 420, new float[5]);
            // Native counters are floats: keep the isolated cent deltas exact even when
            // the selected throwaway save already has a very large lifetime income.
            GameManager.Get_PlayerStats[3] = 0; GameManager.Get_PlayerStats[4] = 0;
            GameManager.Get_Meet_Race_BestTimes = new[] { -1f, -1f, -1f };
            GameManager.Get_RaceWonInARow = new int[2];
            GameManager.Get_Meet_Rewards_Claimed = new bool[3];
            SetTime(720);
            if (host) UnityEngine.Object.FindObjectOfType<Store_MeetManager>(true)?.CloseStore();
        }

        internal PairedMeetTest(MonMPPlugin plugin)
        {
            this.plugin = plugin;
            gameplay = Singleton<Gameplay>.i;
            meet = UnityEngine.Object.FindObjectOfType<Store_MeetManager>(true) ?? throw new InvalidOperationException("No night meet in the scene.");
            window = UnityEngine.Object.FindObjectOfType<UI_GenericWindow_MeetsManager>(true) ?? throw new InvalidOperationException("No native meet window.");
            windows = Singleton<UI_GenericWindowManager>.i;
            cash = Singleton<Cash_Manager>.i;
            race = plugin.World!.MeetRace!;
            Require(GameManager.BetPrice_Race[2] == 1000f && GameManager.BetPrice_Race[0] == 50f, "native bet prices no longer match the cent ledger fixture");
            channel = plugin.World.Channel;
            anthony = UnityEngine.Object.FindObjectsOfType<Actor_Anthony>(true).First(actor => actor.gameObject.scene.IsValid());
            rewardPrefab = plugin.World.Items.TestPrefab(Singleton<GameAssets>.i.Part_LipSpoiler.GetComponent<InteractableItems>().PrefabName);
            originalRewards = new HashSet<ulong>(RewardIds());
            spentBefore = GameManager.Get_PlayerStats[4]; receivedBefore = GameManager.Get_PlayerStats[3];
            plugin.Vehicles!.TestType = Vehicle_Type.MainVehicle;
            plugin.Vehicles.TestEnableDriverEntryDiagnostics();
            originalVehiclePosition = plugin.Vehicles.TestVehicle.transform.position;
            originalVehicleRotation = plugin.Vehicles.TestVehicle.transform.rotation;
            channel.CommandRequested += OnCommand; channel.ReplyReceived += OnReply;
            windows.OpenMeetsManager(); // Native Start initializes the real UI and its listeners.
        }

        internal bool Tick()
        {
            if (deadline == 0) deadline = Time.unscaledTime + 240;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Meet test stalled: " + Status);
            if (Time.unscaledTime < next || !channel.FullySynchronized) return false;
            if (!plugin.TestOptions.IsHost && phase == 0 &&
                (!plugin.World!.PersonalProfileReady || plugin.World.ReconnectPositions?.Settled != true)) return false;
            plugin.Vehicles!.TestType = Vehicle_Type.MainVehicle;
            if (!plugin.TestOptions.IsHost && gameplay.Get_CurrentVehicleController == plugin.Vehicles.TestVehicle)
            {
                var vehicle = plugin.Vehicles.TestVehicle;
                vehicle.input.Throttle = 0; vehicle.input.Brakes = 1; vehicle.input.Handbrake = 1;
            }
            return plugin.TestOptions.IsHost ? Host() : Guest();
        }

        private bool Host()
        {
            if (phase == 0 && plugin.Session!.Peers.Count >= 2 && !meet.isOpen &&
                Mathf.Abs(meet.transform.position.y - ((Vector3)ClosedAt.GetValue(meet)).y) < 0.01f)
            {
                Require(cash.GetCash == StartingCash && EmptyProgress(), "isolated host wallet/progression baseline is missing");
                RequireDriverSeat();
                var vehicle = plugin.Vehicles!.TestVehicle;
                if (vehicle.isDriverDoorClosed && vehicle.isPassengerDoorClosed)
                {
                    var position = gameplay.PlayerWalking.transform.position;
                    MovePlayer(plugin.Vehicles.Doors.TestKonigPosition() + Vector3.right);
                    plugin.Vehicles.Doors.TestKonigClick();
                    MovePlayer(position);
                }
                SetTime(1350); meet.OpenStore(); phase = 1;
            }
            else if (phase == 1 && guestStage >= 1) { SetTime(720); meet.CloseStore(); phase = 2; }
            else if (phase == 2 && guestStage >= 2) { SetTime(1350); meet.OpenStore(); phase = 3; }
            else if (phase == 3 && guestStage >= 3)
            {
                Ledger(StartingCash - Stake, Stake, 0);
                Require(EmptyProgress() && race.TestRacer == guestPeer, "repeated guest admission replaced its lease or progression");
                int refused = race.TestRefused; Bet(0);
                Require(race.TestRefused == refused + 1 && !race.TestHolding, "host native bet was accepted during the guest lease");
                Ack(3); phase = 4;
            }
            else if (phase == 4 && guestStage >= 4)
            {
                Ledger(StartingCash - Stake, Stake, 0);
                Require(channel.Records.TryGetValue(SharedMeetRace.Entity, out var sample) && (sample.Payload[12] & 1) != 0,
                    "host never observed the guest's real native running-race sample");
                int refused = race.TestRefused; Bet(0);
                Require(race.TestRefused == refused + 1 && EmptyProgress(), "a running guest race admitted a host bet or result early");
                Ack(4); phase = 5;
            }
            else if (phase == 5 && guestStage >= 5)
            {
                Ledger(StartingCash + Stake, Stake, Stake * 2);
                Require(WinningProgress(false), "native guest result/repeated commands did not settle canonical records and streak once");
                Ack(5); phase = 6;
            }
            else if (phase == 6 && guestStage >= 6)
            {
                Ledger(StartingCash + Stake, Stake, Stake * 2);
                Require(WinningProgress(true) && NewRewardIds().Length == 1, "repeated reward claims did not create exactly one canonical reward");
                Ack(6); phase = 7;
            }
            else if (phase == 7 && guestStage >= 7)
            {
                Ledger(StartingCash + Stake, Stake * 2, Stake * 3);
                Require(race.TestRacer == SharedSpeedwayEvents.Nobody && WinningProgress(true), "native daily-bet refusal kept its lease/stake or altered the win");
                Ack(7); phase = 8;
            }
            else if (phase == 8 && guestStage >= 8)
            {
                Ledger(StartingCash + Stake - 5000, Stake * 2 + 5000, Stake * 3);
                Require(race.TestRacer == guestPeer, "second native guest bet did not hold a disconnect fixture lease");
                Ack(8); phase = 9;
            }
            else if (phase == 9 && plugin.Session!.Peers.Count == 1)
            {
                Ledger(StartingCash + Stake, Stake * 2 + 5000, Stake * 3 + 5000);
                Require(race.TestRacer == SharedSpeedwayEvents.Nobody && WinningProgress(true) && NewRewardIds().Length == 1,
                    "disconnect retained/refunded the wrong lease or changed settled progress/reward");
                SetTime(720); meet.CloseStore(); return true;
            }
            return false;
        }

        private bool Guest()
        {
            var shared = plugin.Vehicles!;
            if (phase == 0 && RaisedAt((Vector3)OpenAt.GetValue(meet))) { Signal(1); phase = 1; }
            else if (phase == 1 && !meet.isOpen && Mathf.Abs(meet.transform.position.y - ((Vector3)ClosedAt.GetValue(meet)).y) < 0.01f)
            { Signal(2); phase = 2; }
            else if (phase == 2 && RaisedAt((Vector3)OpenAt.GetValue(meet)))
            {
                Require(cash.GetCash == StartingCash && EmptyProgress(), "host wallet/progression baseline did not reach the guest");
                windows.OpenMeetsManager(); phase = 3; next = Time.unscaledTime + 0.2f;
            }
            else if (phase == 3)
            {
                Require(ListBuilt.GetValue(window) is true && !meet.Get_BetIsDoneToday[2], "real native meet UI did not initialize");
                Require(!meet.Get_RaceManager.CheckIfSomethingOnTheRoad(2), "isolated Tom race route is obstructed before the native bet; clear-road prerequisite is missing");
                Bet(2); admissionOperation = CurrentOperation;
                Require(!race.TestHolding && !meet.Get_BetIsDoneToday[2], "guest native race/debit ran before the host's grant");
                Request(Claim(2, admissionOperation)); // Exact retry of the native operation.
                phase = 4;
            }
            else if (phase == 4 && race.TestHolding && meet.Get_BetIsDoneToday[2] && race.TestCar(2)?.Get_Car_State != AI_MeetCar.MeetCar_State.Sleeping && cash.GetCash == StartingCash - Stake)
            {
                Require(admissions.TryGetValue(admissionOperation, out byte status) && status == 1, "native bet had no correlated grant");
                // Read the correlated native grant, even if its coalesced world delta has
                // not yet reached the guest. This never assigns adapter state.
                lease = (uint)HoldingLease.GetValue(race);
                RepeatNativeBets();
                // The two later native reward actions advance this same operation counter
                // past these diagnostic commands before the next native race admission.
                Request(Claim(2, admissionOperation + 1)); Request(Claim(0, admissionOperation + 2));
                phase = 5; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 5 && admissions.TryGetValue(admissionOperation + 2, out byte refusal))
            {
                Require(refusal == 0 && admissions[admissionOperation + 1] == 0 && cash.GetCash == StartingCash - Stake && EmptyProgress(),
                    "same/different fresh command admission debited/replaced an existing stake");
                Signal(3); phase = 6;
            }
            else if (phase == 6 && hostAck >= 3)
            {
                if (!plugin.World!.PersonalProfileReady || plugin.World.ReconnectPositions?.Settled != true) return false;
                if (windows.Get_isOpen) windows.Button_CloseGenericWindow();
                RequireDriverSeat(); driverEntryStarted = Time.unscaledTime; phase = 7;
            }
            else if (phase == 7)
            {
                Require(Time.unscaledTime - driverEntryStarted < 25, "native Konig driver admission stalled: " + DriverEntryStatus());
                if (shared.TestState.Driver.Owner == plugin.Session!.LocalPeerId) { phase = 8; return false; }
                Require(shared.TestState.Driver.Owner == ItemLease.Unowned, "the fixture Konig already has another driver: " + DriverEntryStatus());
                Require(gameplay.Get_PlayerState == PlayerState.Walking && !gameplay.Player.IsHoldingAnObject && !windows.Get_isOpen,
                    "native driver entry requires walking, empty hands and closed UI: " + DriverEntryStatus());
                var vehicle = shared.TestVehicle;
                if (vehicle.isDriverDoorClosed && vehicle.isPassengerDoorClosed)
                {
                    // Re-open through the real shared handle if physics closed the host's
                    // prerequisite door while the native meet was raising/driving its car.
                    if (driverEntryStep != 1 || Vector3.Distance(gameplay.PlayerWalking.transform.position, shared.Doors.TestKonigPosition()) > 3)
                    {
                        MovePlayer(shared.Doors.TestKonigPosition() + Vector3.right); driverEntryStep = 1;
                        next = Time.unscaledTime + 1; return false;
                    }
                    shared.Doors.TestKonigClick(); driverDoorAttempts++; driverEntryStep = 2;
                    next = Time.unscaledTime + .7f; return false;
                }
                if (driverEntryStep < 3 || Vector3.Distance(gameplay.PlayerWalking.transform.position, shared.TestSeat) > 3)
                {
                    MovePlayer(shared.TestSeat + Vector3.right); driverEntryStep = 3;
                    next = Time.unscaledTime + 1; return false;
                }
                RequireDriverSeat(); shared.TestEnter(); driverEntryAttempts++;
                phase = 8; next = Time.unscaledTime + 0.7f;
            }
            else if (phase == 8)
            {
                Require(Time.unscaledTime - driverEntryStarted < 25, "native Konig driver entry did not apply: " + DriverEntryStatus());
                if (shared.TestState.Driver.Owner != plugin.Session!.LocalPeerId) { phase = 7; return false; }
                if (gameplay.Get_CurrentVehicleController != shared.TestVehicle || gameplay.Get_PlayerState != PlayerState.InVehicle_Driver) return false;
                Debug.Log("MONMP_MEET_NATIVE_DRIVER: " + DriverEntryStatus());
                phase = 9;
            }
            else if (phase == 9 && race.TestCar(2)?.Get_Car_State == AI_MeetCar.MeetCar_State.WaitingAtStartLine)
            {
                RepeatNativeBets();
                // Position the actually leased/entered vehicle, then use the native callback.
                // Countdown still enforces vehicle type, speed and clear road.
                var detector = (GameObject)Detector.GetValue(meet.Get_RaceManager);
                var vehicle = shared.TestVehicle;
                vehicle.transform.position += detector.transform.position - gameplay.PlayerWalking.transform.position;
                vehicle.vehicleRigidbody.position = vehicle.transform.position;
                vehicle.vehicleRigidbody.velocity = vehicle.vehicleRigidbody.angularVelocity = Vector3.zero;
                Physics.SyncTransforms();
                meet.Get_RaceManager.StartDetector_PlayerIn(); phase = 10;
            }
            else if (phase == 10 && meet.Get_RaceManager.Get_IsRacing && (float)Timer.GetValue(meet.Get_RaceManager) > 0.5f)
            {
                RepeatNativeBets();
                Signal(4); phase = 11;
            }
            else if (phase == 11 && hostAck >= 4)
            {
                finishTime = (float)Timer.GetValue(meet.Get_RaceManager);
                meet.Get_RaceManager.PlayerCompletedTheRace(); meet.Get_RaceManager.PlayerCompletedTheRace();
                var result = new byte[10]; result[0] = 4; BitConverter.GetBytes(lease).CopyTo(result, 1);
                result[5] = 1; BitConverter.GetBytes(finishTime).CopyTo(result, 6);
                Request(result); Request(result); phase = 12; next = Time.unscaledTime + 1;
            }
            else if (phase == 12 && cash.GetCash == StartingCash + Stake && WinningProgress(false))
            {
                Require(Mathf.Abs(GameManager.Get_Meet_Race_BestTimes[2] - finishTime) < 0.001f, "host/guest best time differs from the actual native timer");
                Signal(5); phase = 13;
            }
            else if (phase == 13 && hostAck >= 5)
            {
                RepeatNativeBets(); // Settled result still retains its lease until the car sleeps.
                race.TestCar(2)!.StopRacing(); // Native fixture stop after the result; no Car_State writes.
                shared.TestExit(); phase = 14; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 14 && gameplay.Get_CurrentVehicleController == null && shared.TestState.Driver.Owner == ItemLease.Unowned)
            {
                MovePlayer(anthony.transform.position + Vector3.right);
                windows.OpenMeetsManager(); phase = 15; next = Time.unscaledTime + 1;
            }
            else if (phase == 15) { Reward(2); rewardOperation = CurrentOperation; phase = 16; }
            else if (phase == 16 && rewards.TryGetValue(rewardOperation, out var granted))
            {
                Require(granted.Status == 1 && granted.Item != 0, "eligible native reward was not granted by the host");
                rewardItem = granted.Item;
                if (plugin.World!.Items.TestItem(rewardItem) == null || !plugin.World.Items.TestLocalHeld(rewardItem)) return false;
                Require(WinningProgress(true) && NewRewardIds().SequenceEqual(new[] { rewardItem }) &&
                    plugin.World.Items.TestState(rewardItem).Lease.Owner == plugin.Session!.LocalPeerId,
                    "reward was not one real host-created, guest-leased item");
                plugin.World.Items.TestDrop(false); phase = 17; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 17 && !gameplay.Player.IsHoldingAnObject && plugin.World!.Items.TestState(rewardItem).Lease.Owner == ItemLease.Unowned)
            {
                Request(RewardRequest(2, rewardOperation)); Request(RewardRequest(2, rewardOperation)); phase = 18;
            }
            else if (phase == 18 && rewards[rewardOperation].Count >= 3)
            {
                Require(rewards[rewardOperation].Item == rewardItem && NewRewardIds().Length == 1, "exact reward retry created a second item");
                Reward(2); duplicateRewardOperation = CurrentOperation; phase = 19;
            }
            else if (phase == 19 && rewards.TryGetValue(duplicateRewardOperation, out var repeated))
            {
                Require(repeated.Status == 2 && repeated.Item == 0 && NewRewardIds().Length == 1 && WinningProgress(true) && cash.GetCash == StartingCash + Stake,
                    "fresh native repeat reward claim changed entitlement, item count or wallet");
                Signal(6); phase = 20;
            }
            else if (phase == 20 && hostAck >= 6 && !race.TestHolding && race.TestRacer == SharedSpeedwayEvents.Nobody)
            { windows.OpenMeetsManager(); phase = 21; next = Time.unscaledTime + 0.2f; }
            else if (phase == 21)
            {
                Require(meet.Get_BetIsDoneToday[2], "native spent-daily-bet prerequisite disappeared");
                Bet(2); admissionOperation = CurrentOperation; phase = 22; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 22 && admissions.TryGetValue(admissionOperation, out byte dailyGrant) && !race.TestHolding && race.TestRacer == SharedSpeedwayEvents.Nobody && cash.GetCash == StartingCash + Stake)
            {
                Require(dailyGrant == 1, "spent daily bet never reached actual native refusal after admission");
                Signal(7); phase = 23;
            }
            else if (phase == 23 && hostAck >= 7) { windows.OpenMeetsManager(); phase = 24; next = Time.unscaledTime + 0.2f; }
            else if (phase == 24) { Bet(0); phase = 25; }
            else if (phase == 25 && race.TestHolding && meet.Get_BetIsDoneToday[0] && race.TestCar(0)?.Get_Car_State != AI_MeetCar.MeetCar_State.Sleeping && cash.GetCash == StartingCash + Stake - 5000)
            { Signal(8); phase = 26; }
            else if (phase == 26 && hostAck >= 8) { plugin.EndPairedTestSession(); phase = 27; return true; }
            else if (phase == 27) return true;
            return false;
        }

        private void RepeatNativeBets()
        {
            int refused = race.TestRefused; Bet(2); Bet(0);
            Require(race.TestRefused == refused + 2 && race.TestHolding && meet.Get_BetIsDoneToday[2] && !meet.Get_BetIsDoneToday[0],
                "same/different repeated native bets were not refused without changing the admitted race");
        }
        private void RequireDriverSeat()
        {
            var seat = plugin.Vehicles!.TestVehicle.vehicle_PartsManager.Seat_Left;
            Require(seat != null && seat.isInstalled && seat.partInstalled is Part_Seat part && !part.Get_isFolded && seat.CheckIfSolidlyAttached(),
                "the isolated Konig requires an installed, unfolded, solid native driver seat: " + DriverEntryStatus());
        }
        private string DriverEntryStatus()
        {
            var shared = plugin.Vehicles!; var vehicle = shared.TestKonigVehicle;
            var seat = vehicle.vehicle_PartsManager.Seat_Left; var part = seat != null ? seat.partInstalled as Part_Seat : null;
            return "step=" + driverEntryStep + "; attempts=" + driverEntryAttempts + "/door=" + driverDoorAttempts +
                "; owner=" + shared.TestKonigState.Driver.Owner + "/" + shared.TestKonigState.Driver.Generation +
                "; current=" + (gameplay.Get_CurrentVehicleController == vehicle) + "; player=" + gameplay.Get_PlayerState +
                "; holdingItem=" + gameplay.Player.IsHoldingAnObject + "; ui=" + windows.Get_isOpen +
                "; doorsClosed=" + vehicle.isDriverDoorClosed + "/" + vehicle.isPassengerDoorClosed +
                "; seatInstalled=" + (seat != null && seat.isInstalled) + "; folded=" + (part != null && part.Get_isFolded) +
                "; solid=" + (seat != null && seat.CheckIfSolidlyAttached()) +
                "; range=" + (seat != null && seat.gameobject != null ? Vector3.Distance(gameplay.PlayerWalking.transform.position, seat.gameobject.transform.position).ToString("F2") : "missing") +
                "; settled=" + (plugin.World?.ReconnectPositions?.Settled == true) + "; " + shared.TestDriverEntryDiagnostic;
        }
        private bool RaisedAt(Vector3 location) => meet.isOpen && meet.Get_RaiseCarDone && Mathf.Abs(meet.transform.position.y - location.y) < 0.01f;
        private static bool EmptyProgress() => GameManager.Get_Meet_Race_BestTimes.All(time => time == -1f) &&
            GameManager.Get_RaceWonInARow.SequenceEqual(new[] { 0, 0 }) && GameManager.Get_Meet_Rewards_Claimed.All(flag => !flag);
        private static bool WinningProgress(bool claimed) => GameManager.Get_Meet_Race_BestTimes[0] == -1f && GameManager.Get_Meet_Race_BestTimes[1] == -1f &&
            GameManager.Get_Meet_Race_BestTimes[2] > 0f && GameManager.Get_RaceWonInARow.SequenceEqual(new[] { 1, 1 }) &&
            GameManager.Get_Meet_Rewards_Claimed.SequenceEqual(new[] { false, false, claimed });
        private void Ledger(int wallet, float spent, float received)
        {
            Require(cash.GetCash == wallet && GameManager.Get_PlayerStats[4] - spentBefore == spent && GameManager.Get_PlayerStats[3] - receivedBefore == received,
                "host native wallet/spending/income differ from the exactly-once ledger: wallet=" + cash.GetCash +
                "; spent=" + (GameManager.Get_PlayerStats[4] - spentBefore) + "; received=" + (GameManager.Get_PlayerStats[3] - receivedBefore));
        }
        private IEnumerable<ulong> RewardIds() => channel.Records.Values.Where(record => record.Adapter == ItemState.Adapter)
            .Where(record => { var item = ItemState.Decode(record.Payload); return item.Exists && item.Prefab == rewardPrefab; }).Select(record => record.Id);
        private ulong[] NewRewardIds() => RewardIds().Where(id => !originalRewards.Contains(id)).OrderBy(id => id).ToArray();
        private uint CurrentOperation => (uint)Operation.GetValue(race);
        private static byte[] Claim(byte car, uint operation) { var bytes = new byte[6]; bytes[0] = 1; bytes[1] = car; BitConverter.GetBytes(operation).CopyTo(bytes, 2); return bytes; }
        private static byte[] RewardRequest(byte reward, uint operation) { var bytes = Claim(reward, operation); bytes[0] = 5; return bytes; }
        private void Request(byte[] payload) => channel.Request(SharedMeetRace.Entity, payload, useLease: true);
        private void Signal(byte stage) => Request(new[] { Control, stage });
        private void Ack(byte stage) => channel.Reply(guestPeer, SharedMeetRace.Entity, new[] { Control, stage });
        private void OnCommand(uint peer, ulong entity, byte[] payload)
        {
            if (!plugin.TestOptions.IsHost || entity != SharedMeetRace.Entity || payload.Length != 2 || payload[0] != Control) return;
            guestPeer = peer; guestStage = Math.Max(guestStage, payload[1]);
        }
        private void OnReply(ulong entity, byte[] payload)
        {
            if (entity != SharedMeetRace.Entity || payload.Length < 2) return;
            if (payload[0] == Control && payload.Length == 2) hostAck = Math.Max(hostAck, payload[1]);
            else if (payload[0] == 1 && payload.Length == 11) admissions[BitConverter.ToUInt32(payload, 3)] = payload[1];
            else if (payload[0] == 5 && payload.Length == 15)
            {
                uint operation = BitConverter.ToUInt32(payload, 3); rewards.TryGetValue(operation, out var previous);
                rewards[operation] = (payload[2], BitConverter.ToUInt64(payload, 7), previous.Count + 1);
            }
        }
        private void Bet(int car) => Invoke(BetDone, car);
        private void Reward(int reward) => Invoke(ClaimReward, reward);
        private void Invoke(MethodInfo method, int value)
        {
            try { method.Invoke(window, new object[] { value }); }
            catch (TargetInvocationException ex) { throw new InvalidOperationException("Meet native " + method.Name + " failed: " + Status, ex.InnerException ?? ex); }
        }
        private void MovePlayer(Vector3 position)
        {
            gameplay.PlayerWalking.transform.position = position;
            if (gameplay.Get_Player_Rigidbody != null) { gameplay.Get_Player_Rigidbody.position = position; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero; }
        }
        private static void SetTime(int minute) { var sky = UniStorm.UniStormSystem.Instance; if (sky != null) sky.m_TimeFloat = minute / 1440f; }
        private static FieldInfo Field(Type type, string name) => AccessTools.Field(type, name) ?? throw new InvalidOperationException("Meet diagnostic field missing: " + type.Name + "." + name);
        private static MethodInfo Method(Type type, string name) => AccessTools.Method(type, name, new[] { typeof(int) }) ?? throw new InvalidOperationException("Meet diagnostic method missing: " + type.Name + "." + name);
        private static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException("Paired meet: " + reason); }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true; channel.CommandRequested -= OnCommand; channel.ReplyReceived -= OnReply;
            if (windows.Get_isOpen) windows.Button_CloseGenericWindow();
            // Host restores the isolated Konig pose after release. Native progress/rewards/
            // cash remain in the disposable game copy and are never written to disk.
            if (plugin.TestOptions.IsHost && plugin.Session?.Peers.Count == 1 && plugin.Vehicles != null)
            {
                plugin.Vehicles.TestType = Vehicle_Type.MainVehicle;
                var vehicle = plugin.Vehicles.TestVehicle;
                if (plugin.Vehicles.TestState.Driver.Owner == ItemLease.Unowned)
                {
                    vehicle.transform.SetPositionAndRotation(originalVehiclePosition, originalVehicleRotation);
                    vehicle.vehicleRigidbody.position = originalVehiclePosition; vehicle.vehicleRigidbody.rotation = originalVehicleRotation;
                    vehicle.vehicleRigidbody.velocity = vehicle.vehicleRigidbody.angularVelocity = Vector3.zero;
                }
            }
        }
    }
}
