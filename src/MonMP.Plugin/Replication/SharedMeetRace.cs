using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Session;
using MonMP.Core.World;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // The night meet races one player at a time. The host holds a race lease: a bet (host or guest)
    // first claims it, and only the holder's native bet, countdown and race run. The holder's own
    // client drives its meet car natively and streams that car's pose, state and the start-line
    // traffic light; everyone else sees the car leave its spot, line up, race and park, and the
    // countdown lights. Bets are refused to others while the lease is held, and the host's meet
    // stays open while the holder is mid-race, as natively for its own race. A guest's money is the
    // host's: the host takes a guest's stake when it grants the race (and hands it back if that race
    // never starts), and pays a guest's win, since a guest's own cash calls are suppressed.
    internal sealed partial class SharedMeetRace : IDisposable
    {
        public const ushort Adapter = 99;
        internal static readonly ulong Entity = StaticId.Hash("meet-race/v1");
        private const byte Claim = 1, Release = 2, Sample = 3, Result = 4, Reward = 5;
        private const byte Refused = 0, Granted = 1, NoFunds = 2;
        private const float SampleSeconds = 0.05f;
        // The host is peer 0, so "no racer" is a value no peer has.
        private const uint Nobody = SharedSpeedwayEvents.Nobody;
        private static readonly FieldInfo? CarsField = AccessTools.Field(typeof(RaceManager), "RaceCars");
        private static readonly FieldInfo? LightField = AccessTools.Field(typeof(RaceManager), "Canvas_TrafficLight");
        private static readonly FieldInfo? LightObject = AccessTools.Field(typeof(Canvas_3D_TrafficLight), "TrafficLight");
        private static readonly FieldInfo? LightStatus = AccessTools.Field(typeof(Canvas_3D_TrafficLight), "CurrentStatus");
        private static readonly FieldInfo? VehicleField = AccessTools.Field(typeof(AI_MeetCar), "vc");
        private static readonly FieldInfo? SittingField = AccessTools.Field(typeof(AI_MeetCar), "Actor_Sitting");
        private static readonly FieldInfo? StandingField = AccessTools.Field(typeof(AI_MeetCar), "Actor_Standing");
        private static readonly FieldInfo? WindowField = AccessTools.Field(typeof(UI_GenericWindow_MeetsManager), "ui_GenericWindowManager");
        private static readonly MethodInfo? BetMethod = AccessTools.Method(typeof(UI_GenericWindow_MeetsManager), "Bet_Done", new[] { typeof(int) });
        private static readonly FieldInfo? TimerField = AccessTools.Field(typeof(RaceManager), "CurrentRace_Timer");

        private struct State
        {
            internal uint Racer, Lease;
            internal byte Car, CarState;
            internal sbyte Light;
            internal bool Racing, MeetOpen;
            internal Vector3 Position;
            internal Quaternion Rotation;
        }

        private bool host => session?.IsHost == true;
        private readonly Store_MeetManager? meet;
        private readonly RaceManager? race;
        private readonly AI_MeetCar[] cars;
        private readonly Canvas_3D_TrafficLight? trafficLight;
        private readonly HashSet<int> touched = new HashSet<int>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private State state;
        private bool disposed;
        // The holder's side of the lease.
        private bool holding, wentActive, allowNative;
        private uint holdingLease, nextOperation;
        private bool localResultSent;
        // The holder's side, while its granted bet runs natively: the stake is already the host's.
        private int grantedBet = -1;
        // The host's side of a guest's stake: (lease, amount, the race got going, the win was paid).
        private (uint Lease, float Stake, bool Active, bool Paid)? stake;
        private float heldSince, nextSample, lastSampleAt;
        private bool publishedMeetOpen;
        private float nextMeetOpenCapture;
        private (UI_GenericWindow_MeetsManager Window, int Bet, uint Operation)? pendingBet;
        private readonly Dictionary<uint, (uint Operation, byte[] Reply)> admissionReplies = new Dictionary<uint, (uint, byte[])>();
        private float pendingSince;
        // The observers' side: the holder's car, eased between samples.
        private uint presentedLease;
        private sbyte presentedLight = -1;
        // Observers play the racer's samples back a little behind, at an even pace.
        private readonly PoseTrail trail = new PoseTrail(0.15f, 0.03f);
        private bool hasPose;


        internal uint Racer => state.Racer;


        internal bool Holding => holding;


        // Where the race is, for watchers of its relayed sounds: the racing car.
        internal Vector3? Focus => state.Racer != Nobody && state.Car < cars.Length ? Vehicle(cars[state.Car])?.transform.position : null;
        // Paired test: hold a granted lease without running the native bet (no bet window is open).

#if MONMP_DEVELOPMENT
        internal bool TestSkipNativeBet { get; set; }


        internal int TestSamples { get; private set; }


        internal int TestRefused { get; private set; }


        internal AI_MeetCar? TestCar(int index) => index >= 0 && index < cars.Length ? cars[index] : null;


        internal Vector3? TestCarPosition(int index) => Vehicle(TestCar(index))?.transform.position;


        internal RaceManager? TestRaceManager => race;
#endif


        internal SharedMeetRace()
        {
            meet = UnityEngine.Object.FindObjectOfType<Store_MeetManager>(true);
            race = meet != null ? meet.Get_RaceManager : null;
            cars = race != null ? CarsField?.GetValue(race) as AI_MeetCar[] ?? Array.Empty<AI_MeetCar>() : Array.Empty<AI_MeetCar>();
            trafficLight = race != null ? LightField?.GetValue(race) as Canvas_3D_TrafficLight : null;
            if (trafficLight == null) trafficLight = UnityEngine.Object.FindObjectOfType<Canvas_3D_TrafficLight>(true);
            state.Racer = Nobody; state.Light = -1;
        }

        internal static IEnumerable<WorldRecord> InitialRecords() => new[]
        {
            new WorldRecord(Entity, 2, Adapter, Encode(new State { Racer = Nobody, Light = -1, Rotation = Quaternion.identity })),
            new WorldRecord(MeetProgressState.Entity, 1, Adapter, CaptureProgress().Encode())
        };

        internal void Attach(NetworkSession connected, WorldChannel world)
        {
            session = connected; channel = world;
            connected.PeerLeft += OnLeft;
            world.SnapshotApplied += Snapshot; world.Changed += Changed; world.CommandRequested += Command; world.ReplyReceived += Reply;
        }

        private static byte[] Encode(State s)
        {
            using var stream = new MemoryStream();
            using var w = new BinaryWriter(stream);
            w.Write((byte)2); w.Write(s.Racer); w.Write(s.Lease); w.Write(s.Car); w.Write(s.CarState); w.Write(s.Light);
            w.Write((byte)((s.Racing ? 1 : 0) | (s.MeetOpen ? 2 : 0)));
            w.Write(s.Position.x); w.Write(s.Position.y); w.Write(s.Position.z);
            w.Write(s.Rotation.x); w.Write(s.Rotation.y); w.Write(s.Rotation.z); w.Write(s.Rotation.w);
            return stream.ToArray();
        }

        private static State Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 41) throw new InvalidDataException("Invalid meet race sample size.");
            using var r = new BinaryReader(new MemoryStream(payload, false));
            if (r.ReadByte() != 2) throw new InvalidDataException("Unknown meet race layout.");
            var s = new State { Racer = r.ReadUInt32(), Lease = r.ReadUInt32(), Car = r.ReadByte(), CarState = r.ReadByte(), Light = r.ReadSByte() };
            byte flags = r.ReadByte();
            if ((flags & ~3) != 0) throw new InvalidDataException("Invalid meet race flags.");
            s.Racing = (flags & 1) != 0; s.MeetOpen = (flags & 2) != 0;
            s.Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            s.Rotation = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (!IsFinite(s.Position) || !IsFinite(s.Rotation) || s.Light < -1 || s.Light > 3)
                throw new InvalidDataException("Invalid meet race sample.");
            return s;
        }

        private static bool IsFinite(Vector3 v) => !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        private static bool IsFinite(Quaternion q) => !float.IsNaN(q.x) && !float.IsInfinity(q.x) &&
            !float.IsNaN(q.y) && !float.IsInfinity(q.y) && !float.IsNaN(q.z) && !float.IsInfinity(q.z) &&
            !float.IsNaN(q.w) && !float.IsInfinity(q.w);

        private static VehicleController? Vehicle(AI_MeetCar? car) => car != null ? VehicleField?.GetValue(car) as VehicleController : null;

        private uint Self => session?.LocalPeerId ?? 0;
        private bool Attached => !disposed && channel?.Ready == true && session?.IsRunning == true;

        // Someone else holds the race: every bet is refused, as natively while a race is on.
        internal bool RaceElsewhere => Attached && state.Racer != Nobody && state.Racer != Self;
        // The host's meet may close only when no one is mid-race (the native check covers its own race).
        internal bool BlocksClose => Attached && state.Racer != Nobody && state.Racer != Self && state.Racing;

        // Anthony's dialogue is deliberately host-owned; guests get only the local race window.
        internal bool OpenGuestAnthonyMenu(Actor actor, bool boundActor)
        {
            if (host) return false;
            bool inRange = false;
            try
            {
                var player = Singleton<Gameplay>.i?.Player;
                inRange = player != null && actor != null &&
                    (player.transform.position - actor.transform.position).sqrMagnitude <= 16f;
            }
            catch (Exception) { inRange = false; }
            bool ready = Attached && channel?.FullySynchronized == true &&
                MonMPPlugin.Instance?.World?.SaveGate.Active != true;
            if (!boundActor || actor is not Actor_Anthony || actor == null || !actor.isActiveAndEnabled ||
                !actor.gameObject.scene.IsValid() || !inRange || !ready || !state.MeetOpen || meet == null ||
                race == null || cars.Length == 0)
            {
                if (!ready) MonMPPlugin.Instance?.ReportItemUseUnavailable("Wait for the shared meet before opening its race menu.");
                else if (!state.MeetOpen) MonMPPlugin.Instance?.ReportItemUseUnavailable("The night meet is closed.");
                return false;
            }
            var manager = Singleton<UI_GenericWindowManager>.i;
            if (manager == null) return false;
            manager.OpenMeetsManager();
            return true;
        }

        // Prefix of the native bet (UI_GenericWindow_MeetsManager.Bet_Done): claims the race lease first.
        internal bool AllowBet(UI_GenericWindow_MeetsManager window, int bet)
        {
            if (!Attached || bet < 0 || bet >= cars.Length) return true;
            if (allowNative) { allowNative = false; return true; }
            if (RaceElsewhere || holding || state.Racer != Nobody || state.Racing || race?.Get_IsRacing == true || race?.isRaceCarWaitingAtStartLine == true)
            {
                Refuse(window);
                return false;
            }
            if (host)
            {
                Take(Self, (byte)bet);
                return true;
            }
            if (pendingBet != null && Time.unscaledTime - pendingSince < 3f) return false;
            uint operation = ++nextOperation;
            if (operation == 0) operation = ++nextOperation;
            pendingBet = (window, bet, operation); pendingSince = Time.unscaledTime;
            Debug.Log("MonMP meet race claims bet " + bet);
            var request = new byte[6]; request[0] = Claim; request[1] = (byte)bet;
            BitConverter.GetBytes(operation).CopyTo(request, 2);
            channel!.Request(Entity, request, useLease: true);
            return false;
        }

        private void Refuse(UI_GenericWindow_MeetsManager? window)
        {

#if MONMP_DEVELOPMENT
            TestRefused++;
#endif

            if (window != null && WindowField?.GetValue(window) is UI_GenericWindowManager manager) manager.Button_CloseGenericWindow();
            Singleton<UI_ErrorMessage>.i.Show(TEXT.GetText("RACEMANAGER_ARaceIsAlreadyGoingOn"));
        }

        // Host: grants the lease.
        private void Take(uint racer, byte car, float? paidStake = null)
        {
            if (state.Racer != racer || state.Car != car) state.Lease++;
            state.Racer = racer; state.Car = car; state.Racing = false;
            if (paidStake.HasValue)
            {
                stake = (state.Lease, paidStake.Value, false, false);
                Singleton<Cash_Manager>.i.Event_Spent(paidStake.Value);
            }
            if (racer == Self) { holding = true; holdingLease = state.Lease; wentActive = false; localResultSent = false; heldSince = Time.unscaledTime; }
            lastSampleAt = Time.unscaledTime;
            var vehicle = Vehicle(cars[car]);
            if (vehicle != null) { state.Position = vehicle.transform.position; state.Rotation = vehicle.transform.rotation; }
            state.CarState = (byte)cars[car].Get_Car_State;
            Debug.Log("MonMP meet race lease " + state.Lease + " to peer " + racer + " on car " + car);
            channel!.Publish(Entity, Encode(state));
        }

        private void Free(string reason)
        {
            if (state.Racer == Nobody) return;
            // A stake whose race never left the parking spot goes back.
            if (stake is var held && held.HasValue && held.Value.Lease == state.Lease && !held.Value.Active)
            {
                Debug.Log("MonMP meet race lease " + state.Lease + " refunds its stake of " + held.Value.Stake);
                Singleton<Cash_Manager>.i.Event_Received(held.Value.Stake);
            }
            if (stake is var lost && lost.HasValue && lost.Value.Lease == state.Lease && lost.Value.Active && !lost.Value.Paid)
                SetProgress(CaptureProgress().Lose());
            if (state.Racer == Self && wentActive && !localResultSent) SetProgress(CaptureProgress().Lose());
            stake = null;
            Debug.Log("MonMP meet race lease " + state.Lease + " of peer " + state.Racer + " ended: " + reason);
            state.Racer = Nobody; state.Racing = false; state.Light = -1;
            channel!.Publish(Entity, Encode(state));
        }

        [MonMP.Core.World.WorldCommandAdapters(SharedMeetRace.Adapter)]
        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (!host || entity != Entity || payload.Length == 0) return;
            if (peer == 0) peer = Self;
            if (payload[0] == Claim && payload.Length == 6)
            {
                uint operation = BitConverter.ToUInt32(payload, 2);
                if (operation == 0) return;
                if (admissionReplies.TryGetValue(peer, out var previous) && operation <= previous.Operation)
                {
                    if (operation == previous.Operation)
                    {
                        var reply = previous.Reply;
                        if (reply[1] == Granted && (state.Racer != peer || state.Lease != BitConverter.ToUInt32(reply, 7)))
                        { reply = (byte[])reply.Clone(); reply[1] = Refused; }
                        channel!.Reply(peer, Entity, reply);
                    }
                    return;
                }
                // Retain an admitted stake until settlement/release. Even the same car cannot
                // replace it; exact retries replay the original admission above without a debit.
                bool granted = payload[1] < cars.Length && payload[1] < GameManager.BetPrice_Race.Length && payload[1] < 3 && state.Racer == Nobody && stake == null &&
                    !holding && !state.Racing && meet?.isOpen == true && race != null && !race.Get_IsRacing && !race.isRaceCarWaitingAtStartLine;
                // A guest's stake comes out of the shared (host's) money.
                float price = payload[1] < GameManager.BetPrice_Race.Length ? GameManager.BetPrice_Race[payload[1]] : 0;
                bool funds = peer == Self || Singleton<Cash_Manager>.i.isCanAfford(price);
                if (granted && funds)
                {
                    Take(peer, payload[1], peer != Self ? price : (float?)null);
                }
                if (peer != Self)
                {
                    Debug.Log("MonMP meet race claim from peer " + peer + " bet " + payload[1] + ": granted=" + granted + " funds=" + funds);
                    var reply = new byte[11]; reply[0] = Claim; reply[1] = !granted ? Refused : funds ? Granted : NoFunds; reply[2] = payload[1];
                    BitConverter.GetBytes(operation).CopyTo(reply, 3); BitConverter.GetBytes(state.Lease).CopyTo(reply, 7);
                    admissionReplies[peer] = (operation, reply);
                    channel!.Reply(peer, Entity, reply);
                }
                return;
            }
            if (payload[0] == Reward && payload.Length == 6) { ClaimReward(peer, payload); return; }
            if (payload[0] == Result && payload.Length == 10)
            {
                // A guest's win, once per lease, only after its race was seen running.
                uint lease = BitConverter.ToUInt32(payload, 1);
                bool victory = payload[5] == 1;
                float seconds = BitConverter.ToSingle(payload, 6);
                if (payload[5] > 1 || victory && (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds <= 0f || seconds > 121f)) return;
                if (state.Racer == peer && peer != Self && lease == state.Lease && stake is var won && won.HasValue && won.Value.Lease == state.Lease && won.Value.Active && !won.Value.Paid)
                {
                    // Mark settled before callbacks or publications can replay this result.
                    stake = (won.Value.Lease, won.Value.Stake, true, true);
                    SetProgress(victory ? CaptureProgress().Win(state.Car, seconds) : CaptureProgress().Lose());
                    if (victory)
                    {
                        if (state.Car == 0)
                        {
                            try { Singleton<ToDoList_Manager>.i.Racing_WinAgaisntMichaelAtTheNightMeet(); }
                            catch (Exception ex) { Debug.LogWarning("MonMP meet Michael ToDo failed: " + ex.Message); }
                        }
                        Debug.Log("MonMP meet race lease " + state.Lease + " pays peer " + peer + " " + won.Value.Stake * 2f);
                        Singleton<Cash_Manager>.i.Event_Received(won.Value.Stake * 2f);
                    }
                }
                return;
            }
            if (state.Racer != peer || peer == Self) return;
            if (payload[0] == Release && payload.Length == 5 && BitConverter.ToUInt32(payload, 1) == state.Lease) { Free("released by the racer"); return; }
            if (payload[0] == Sample && payload.Length == 5 + 32 && BitConverter.ToUInt32(payload, 1) == state.Lease)
            {
                try
                {
                    var bytes = new byte[41]; bytes[0] = 2;
                    BitConverter.GetBytes(state.Racer).CopyTo(bytes, 1); BitConverter.GetBytes(state.Lease).CopyTo(bytes, 5);
                    Array.Copy(payload, 5, bytes, 9, 32);
                    var sample = Decode(bytes);
                    if (sample.Car != state.Car) return;
                    state.CarState = sample.CarState; state.Light = sample.Light; state.Racing = sample.Racing;
                    if (sample.Racing && stake is var running && running.HasValue && running.Value.Lease == state.Lease && !running.Value.Active)
                        stake = (running.Value.Lease, running.Value.Stake, true, running.Value.Paid);
                    state.Position = sample.Position; state.Rotation = sample.Rotation;
                    lastSampleAt = Time.unscaledTime;
                    channel!.Publish(Entity, Encode(state));
                }
                catch (InvalidDataException) { }
            }
        }

        private void Reply(ulong entity, byte[] payload)
        {
            if (entity != Entity || payload.Length == 0) return;
            if (payload[0] == Reward) { RewardReply(payload); return; }
            if (host) return;
            if (payload.Length != 11 || payload[0] != Claim || payload[1] > NoFunds) return;
            if (pendingBet == null || pendingBet.Value.Operation != BitConverter.ToUInt32(payload, 3) || pendingBet.Value.Bet != payload[2]) return;
            var pending = pendingBet; pendingBet = null;
            Debug.Log("MonMP meet race claim reply " + payload[1] + " bet " + payload[2] + " pending=" + (pending?.Bet ?? -1));
            if (payload[1] == NoFunds)
            {

#if MONMP_DEVELOPMENT
                TestRefused++;
#endif

                if (pending?.Window != null && WindowField?.GetValue(pending.Value.Window) is UI_GenericWindowManager manager) manager.Button_CloseGenericWindow();
                Singleton<UI_ErrorMessage>.i.Show(TEXT.GetText("RACEMANAGER_NoMoney"));
                return;
            }
            if (payload[1] == Refused) { Refuse(pending?.Window); return; }
            // An exact retry may replay a grant; an already-consumed reply never starts a
            // second native bet. The operation and lease are both carried explicitly.
            if (holding) return;
            holding = true; holdingLease = BitConverter.ToUInt32(payload, 7); wentActive = false; localResultSent = false; heldSince = Time.unscaledTime;
            state.Racer = Self; state.Lease = holdingLease; state.Car = payload[2];
            #if MONMP_DEVELOPMENT
            if (TestSkipNativeBet) return;
#endif
            if (pending == null || pending.Value.Window == null || BetMethod == null) { ReleaseHeld(); return; }
            // The host's grant arrives after the native bet was held back: run it now.
            allowNative = true; grantedBet = pending.Value.Bet;
            try
            {
                bool alreadyBet = meet?.Get_BetIsDoneToday[pending.Value.Bet] == true;
                BetMethod.Invoke(pending.Value.Window, new object[] { pending.Value.Bet });
                // Native admission can return normally for a closed meet, spent daily bet,
                // blocked road, or missing lift. None of those consumes the host's stake.
                if (alreadyBet || meet?.Get_BetIsDoneToday[pending.Value.Bet] != true) ReleaseHeld();
            }
            catch (TargetInvocationException ex)
            {
                // The native bet failed (its window went away): hand the race back rather than hold it.
                Debug.LogWarning("MonMP meet bet failed after the host's grant: " + ex.InnerException?.Message);
                ReleaseHeld();
            }
            finally { allowNative = false; grantedBet = -1; }
        }

        // The native bet's affordability check on a guest whose stake the host already took.
        internal bool AllowNativeAffordabilityQuote(float price) =>
            grantedBet >= 0 && grantedBet < GameManager.BetPrice_Race.Length && price == GameManager.BetPrice_Race[grantedBet];

        // Prefix of RaceManager.PlayerCompletedTheRace: a guest's win is paid by the host (the native
        // payout is one of the guest's suppressed cash calls).
        internal void PlayerWon()
        {
            CompleteLocalRace(true);
        }

        internal void PlayerLost() => CompleteLocalRace(false);

        private void CompleteLocalRace(bool won)
        {
            if (!Attached || !holding || race?.Get_IsRacing != true || localResultSent) return;
            localResultSent = true;
            if (host)
            {
                if (!won) SetProgress(CaptureProgress().Lose());
                return;
            }
            // Ensure even a finish between regular samples has an observed running race.
            nextSample = 0f; Hold();
            float seconds = won && TimerField?.GetValue(race) is float time ? time : 0f;
            var result = new byte[10]; result[0] = Result;
            BitConverter.GetBytes(holdingLease).CopyTo(result, 1); result[5] = (byte)(won ? 1 : 0);
            BitConverter.GetBytes(seconds).CopyTo(result, 6);
            channel!.Request(Entity, result, useLease: true);
        }

        internal void NativeResultFinished()
        {
            if (Attached && host) PublishProgress();
        }

        private void ReleaseHeld()
        {
            holding = false;
            var release = new byte[5]; release[0] = Release; BitConverter.GetBytes(holdingLease).CopyTo(release, 1);
            channel!.Request(Entity, release, useLease: true);
        }

        private void OnLeft(uint peer)
        {
            if (host && state.Racer == peer) Free("racer left");
            admissionReplies.Remove(peer); rewardReplies.Remove(peer);
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (!host && records.TryGetValue(MeetProgressState.Entity, out var progress)) ReceiveProgress(progress.Payload);
            if (!host && records.TryGetValue(Entity, out var record)) Receive(record.Payload, true);
        }

        private void Changed(WorldRecord record)
        {
            if (record.Id == MeetProgressState.Entity) { if (!host) ReceiveProgress(record.Payload); return; }
            if (record.Id != Entity) return;
            // The host sees its own publishes here, including a guest racer's relayed samples.
            if (host) Observe(false); else Receive(record.Payload, false);
        }

        private void Receive(byte[] payload, bool snap)
        {
            State next;
            try { next = Decode(payload); } catch (Exception ex) when (ex is InvalidDataException || ex is IOException) { return; }
            state = next;
            if (holding && next.Racer != Self && Time.unscaledTime - heldSince > 2f) holding = false;
            Observe(snap);
        }

        // Observers: the holder's car and the start-line light.
        private void Observe(bool snap)
        {
            if (state.Racer == Nobody || state.Racer == Self || state.Car >= cars.Length)
            {
                if (presentedLight != -1 && trafficLight != null && !holding && race?.isRaceCarWaitingAtStartLine != true && race?.Get_IsRacing != true)
                    using (SpatialSounds.Mute()) trafficLight.ChangeLight(-1);
                presentedLight = -1;
                return;
            }
            var car = cars[state.Car];
            if (presentedLease != state.Lease)
            {
                presentedLease = state.Lease; hasPose = false;
                touched.Add(state.Car);
                // The driver climbs in, as for a native bet.
                (SittingField?.GetValue(car) as GameObject)?.SetActive(true);
                (StandingField?.GetValue(car) as GameObject)?.SetActive(false);
            }
            var vehicle = Vehicle(car);
            if (vehicle != null)
            {
                if (vehicle.vehicleRigidbody != null && !vehicle.vehicleRigidbody.isKinematic) vehicle.vehicleRigidbody.isKinematic = true;
                if (snap || !hasPose) trail.Clear();
                trail.Add(state.Position, state.Rotation);
                hasPose = true;
            }
            if (state.Light != presentedLight && trafficLight != null)
            {
                presentedLight = state.Light;
                // The racer relays its countdown beeps to those watching (SharedSpeedwayEvents).
                using (SpatialSounds.Mute()) trafficLight.ChangeLight(state.Light);
            }
        }

        internal void Update()
        {
            if (!Attached || race == null) return;
            UpdateProgress();
            if (host && Time.unscaledTime >= nextMeetOpenCapture)
            {
                nextMeetOpenCapture = Time.unscaledTime + 0.25f;
                bool open = meet?.isOpen == true;
                if (open != publishedMeetOpen)
                {
                    publishedMeetOpen = open; state.MeetOpen = open;
                    channel!.Publish(Entity, Encode(state));
                }
            }
            if (pendingBet != null && Time.unscaledTime - pendingSince > 5f) pendingBet = null;
            if (holding) Hold();
            if (host && state.Racer != Nobody && state.Racer != Self && Time.unscaledTime - lastSampleAt > 10f) Free("racer silent");
            if (hasPose && state.Racer != Nobody && state.Racer != Self && state.Car < cars.Length)
            {
                var vehicle = Vehicle(cars[state.Car]);
                if (vehicle != null)
                {
                    var played = trail.Playback();
                    var position = played.Position;
                    var rotation = played.Rotation;
                    vehicle.transform.SetPositionAndRotation(position, rotation);
                    if (vehicle.vehicleRigidbody != null) { vehicle.vehicleRigidbody.position = position; vehicle.vehicleRigidbody.rotation = rotation; }
                }
            }
        }

        // The holder: streams its native race until its meet car is parked again.
        private void Hold()
        {
            if (state.Car >= cars.Length) { holding = false; return; }
            var car = cars[state.Car];
            var carState = car.Get_Car_State;
            bool racing = race!.Get_IsRacing;
            if (carState != AI_MeetCar.MeetCar_State.Sleeping) wentActive = true;
            bool done = wentActive ? carState == AI_MeetCar.MeetCar_State.Sleeping && !racing : Time.unscaledTime - heldSince > 6f;
            if (done)
            {
                holding = false;
                if (host) Free("race over");
                else ReleaseHeld();
                return;
            }
            if (Time.unscaledTime < nextSample) return;
            nextSample = Time.unscaledTime + SampleSeconds;
            var vehicle = Vehicle(car);
            if (vehicle == null) return;
            sbyte light = -1;
            if (trafficLight != null && LightObject?.GetValue(trafficLight) is GameObject lamp && lamp.activeSelf && LightStatus?.GetValue(trafficLight) is Enum status)
                light = (sbyte)Mathf.Clamp(Convert.ToInt32(status), -1, 3);
            var sample = new State
            {
                Racer = state.Racer, Lease = state.Lease, Car = state.Car, CarState = (byte)carState, Light = light, Racing = racing,
                MeetOpen = state.MeetOpen,
                Position = vehicle.transform.position, Rotation = vehicle.transform.rotation
            };

#if MONMP_DEVELOPMENT
            TestSamples++;
#endif

            if (host)
            {
                if (state.Racer != Self) return;
                state = sample; lastSampleAt = Time.unscaledTime;
                channel!.Publish(Entity, Encode(state));
                return;
            }
            // Guest: the sample without the racer/lease header, which the host fills in.
            var full = Encode(sample);
            var command = new byte[5 + 32];
            command[0] = Sample;
            BitConverter.GetBytes(holdingLease).CopyTo(command, 1);
            Array.Copy(full, 9, command, 5, 32);
            channel!.Request(Entity, command, useLease: true);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (session != null) session.PeerLeft -= OnLeft;
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; channel.ReplyReceived -= Reply; }
            // Cars moved for someone else's race go back to their spots.
            foreach (int index in touched)
            {
                var car = index < cars.Length ? cars[index] : null;
                if (car == null || car.Get_Car_State != AI_MeetCar.MeetCar_State.Sleeping) continue;
                car.Close_Store_Completed();
                (SittingField?.GetValue(car) as GameObject)?.SetActive(false);
                (StandingField?.GetValue(car) as GameObject)?.SetActive(true);
            }
            if (presentedLight != -1 && trafficLight != null) trafficLight.ChangeLight(-1);
            if (!host) ApplyProgress(originalProgress);
            session = null; channel = null;
        }

#if MONMP_DEVELOPMENT
        internal bool TestHolding => Holding;
        internal uint TestRacer => Racer;
#endif
    }
}
