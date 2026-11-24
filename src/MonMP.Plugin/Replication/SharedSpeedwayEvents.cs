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
using MonMP.Plugin.Patches;
using NWH;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.Effects;
using NWH.WheelController3D;
using UnityEngine;
using UnityEngine.UI;

namespace MonMP.Plugin.Replication
{
    internal enum SpeedwayVenue : byte { Trial = 0, Championship = 1, Derby = 2, Meet = 3 }

    // The speedway's events run natively on the machine of the player who takes part, one at a time
    // per venue, and everyone else watches them.
    //
    // The host arbitrates three leases:
    //  - a time trial (drag, oval or track), claimed as the racer enters a start line. Natively only
    //    one runs at a time, and none while a championship runs.
    //  - the championship registration: one registered player, who runs the championship natively.
    //  - the derby registration: one registered player, who runs the derby natively in Le Missile.
    //
    // The holder streams what its native event shows: tree and traffic lights, start lines and
    // checkpoints, the AI cars (pose, engine, wheels, lamps, health, steam), the derby stands and
    // gates, and the HUD values. It also relays its race sounds, event messages, dents and repairs.
    //
    // Watchers apply the same visuals (native triggers stay off for them). They hear the sounds and
    // see the messages and HUD only within watching range, so a player elsewhere is not disturbed.
    //
    // A guest's rewards (cash, trophy, unlocks) and best times are the host's world state. The host
    // grants them once per lease, after it saw that event run.
    internal sealed class SharedSpeedwayEvents : IDisposable
    {
        public const ushort Adapter = 135;
        internal const uint Nobody = uint.MaxValue;
        internal static readonly ulong TrialEntity = StaticId.Hash("speedway-trial/v1");
        internal static readonly ulong ChampionshipEntity = StaticId.Hash("speedway-championship/v1");
        internal static readonly ulong DerbyEntity = StaticId.Hash("speedway-derby/v1");
        internal static readonly ulong MessageEntity = StaticId.Hash("speedway-message/v1");
        internal static readonly ulong RecordsEntity = StaticId.Hash("speedway-records/v1");
        private const byte Claim = 1, Release = 2, Sample = 3, MessagePart = 4, Reward = 5, Best = 6;
        private const float SampleSeconds = 0.05f, KeepAliveSeconds = 1f, SilentSeconds = 10f, WatchRange = 150f;
        // Beyond this no one sees the event's cars move: its holder streams at the keepalive rate (#290).
        private const float AudienceRange = 300f;
        // A championship's cars leave after it ends, a derby's cars lower after it: the lease is kept that long (#283, #284).
        private const float CleanupSeconds = 30f;
        // The longest message body a guest relays: five header bytes plus the body fit three 120-byte parts (#288).
        private const int MaxMessageBytes = 355;
        internal const byte TrialDrag = 1, TrialOval = 2, TrialTrack = 3;
        internal enum Cue : byte { Countdown = 1, Start, Checkpoint, Finish, Whistle, CarDead, Champion }
        private static readonly string[] TitleKeys = { "", "Speedway_DragRaceName", "Speedway_OvalRaceName", "Speedway_TrackRaceName", "SPEEDWAY_Championship_Name", "DerbyDemolition_Name" };

        private static FieldInfo? F(Type type, string name) => AccessTools.Field(type, name);
        // Speedway (time trials, boards).
        private static readonly FieldInfo? SwDragStart = F(typeof(Speedway), "Drag_StartLine"), SwDragFinish = F(typeof(Speedway), "Drag_FinishLine"),
            SwOvalStart = F(typeof(Speedway), "Oval_StartLine"), SwTrackStart = F(typeof(Speedway), "Track_StartLine"),
            SwTrackCps = F(typeof(Speedway), "Track_Checkpoints"), SwOvalCps = F(typeof(Speedway), "Oval_Checkpoints"),
            SwDragTree = F(typeof(Speedway), "dragTree"), SwTrackTree = F(typeof(Speedway), "trackTree"), SwOvalTree = F(typeof(Speedway), "ovalTree"),
            SwRacing = F(typeof(Speedway), "IsRacing"), SwInDrag = F(typeof(Speedway), "PlayerInDragStart"),
            SwInOval = F(typeof(Speedway), "PlayerInOvalStart"), SwInTrack = F(typeof(Speedway), "PlayerInTrackStart"),
            SwDerbyWon = F(typeof(Speedway), "DerbyWon"), SwChampWon = F(typeof(Speedway), "ChampionshipWon"),
            SwBoard = F(typeof(Speedway), "speedway_BestTimes"), SwChampBoard = F(typeof(Speedway), "Championship_BestTimes"),
            SwRacecarToDo = F(typeof(Speedway), "speedway_RacecarToDo");
        private static readonly MethodInfo? DeTick = AccessTools.Method(typeof(Event_DerbyDemolition), "Tick", new[] { typeof(int) });
        private static readonly MethodInfo? DeLowerCars = AccessTools.Method(typeof(Event_DerbyDemolition), "LowerCars");
        private static readonly FieldInfo? TreeStage = F(typeof(DragTree), "cacheStage");
        private static readonly FieldInfo? LightObject = F(typeof(Canvas_3D_TrafficLight), "TrafficLight");
        private static readonly FieldInfo? LightStatus = F(typeof(Canvas_3D_TrafficLight), "CurrentStatus");
        private static readonly FieldInfo? DetectorVariables = F(typeof(PlayerDetector), "Variables");
        // Championship.
        private static readonly FieldInfo? ChRunning = F(typeof(Event_Championship), "EventRunning"), ChForm = F(typeof(Event_Championship), "InscriptionBooth_Form"),
            ChDragTree = F(typeof(Event_Championship), "dragTree"), ChTrackTree = F(typeof(Event_Championship), "trackTree"),
            ChLight = F(typeof(Event_Championship), "Canvas_TrafficLight"), ChUi = F(typeof(Event_Championship), "UI_Event_Championship"),
            ChRacing = F(typeof(Event_Championship), "isRacing"), ChLap = F(typeof(Event_Championship), "CurrentRace_Lap"),
            ChLapTime = F(typeof(Event_Championship), "CurrentRace_Timer_Lap"), ChLastLap = F(typeof(Event_Championship), "CurrentRace_Timer_LastLap"),
            ChStage = F(typeof(Event_Championship), "Championship_Stage"), ChStarts = F(typeof(Event_Championship), "PlayerDetector_StartingLine"),
            ChTrophyAt = F(typeof(Event_Championship), "Championship_TrophySpawnLocation");
        private static readonly string[] ChCheckpointSets = { "Checkpoints_Rally", "Checkpoints_Drag", "Checkpoints_Race" };
        private static readonly string[] ChCars = { "AI_Championship_Rally1", "AI_Championship_Rally2", "AI_Championship_Drag1", "AI_Championship_Drag2", "AI_Championship_Race1", "AI_Championship_Race2" };
        private static readonly FieldInfo? RallyVc = F(typeof(AI_RallyCar), "vc");
        // Derby.
        private static readonly FieldInfo? DeRunning = F(typeof(Event_DerbyDemolition), "EventRunning"), DeAi = F(typeof(Event_DerbyDemolition), "AI_Vehicles"),
            DeBlock = F(typeof(Event_DerbyDemolition), "EntranceBlock"), DeDetector = F(typeof(Event_DerbyDemolition), "PlayerDetector"),
            DeSpectators = F(typeof(Event_DerbyDemolition), "Actors_Spectator"), DeForm = F(typeof(Event_DerbyDemolition), "InscriptionBooth_Form"),
            DePlayerCar = F(typeof(Event_DerbyDemolition), "playerCar"), DeUi = F(typeof(Event_DerbyDemolition), "ui_Event_DerbyDemolition"),
            DeTrophyAt = F(typeof(Event_DerbyDemolition), "Derby_TrophySpawnLocation"), DeRound = F(typeof(Event_DerbyDemolition), "Round_Current"),
            DeRounds = F(typeof(Event_DerbyDemolition), "Round_Total"), DeRegistered = F(typeof(Event_DerbyDemolition), "PlayerIsRegistered"),
            DeCars = F(typeof(Event_DerbyDemolition), "DerbyCars"), DeOpenAt = F(typeof(Event_DerbyDemolition), "PositionWhileOpen"),
            DeClosedAt = F(typeof(Event_DerbyDemolition), "PositionWhileClose"), DeRaising = F(typeof(Event_DerbyDemolition), "Coroutine_RaisingCars"),
            DeLowering = F(typeof(Event_DerbyDemolition), "Coroutine_LoweringCars");
        private static readonly FieldInfo? DvSlider = F(typeof(Event_DerbyDemolition_Vehicle), "Slider_HealthBar"), DvImage = F(typeof(Event_DerbyDemolition_Vehicle), "Image_HealthBar"),
            DvSteam = F(typeof(Event_DerbyDemolition_Vehicle), "VFX_Steam"), DvHealth = F(typeof(Event_DerbyDemolition_Vehicle), "Health"),
            DvMax = F(typeof(Event_DerbyDemolition_Vehicle), "MaxHealth");
        private static readonly FieldInfo? DerbyUiRoot = F(typeof(UI_Event_DerbyDemolition), "DerbyDemolitionUI");
        private static readonly FieldInfo? DamageMeshes = F(typeof(DamageHandler), "_deformableMeshFilters");
        private const int MissileSlot = 5;

        private sealed class VenueState
        {
            internal readonly SpeedwayVenue Venue; internal readonly ulong Entity;
            internal uint Holder = Nobody, Lease; internal byte Kind;
            internal byte[] Sample = Array.Empty<byte>();
            // Host: the holder's liveness and what it was seen doing this lease.
            internal float LastSampleAt; internal bool SawRunning, Paid;
            // Host: the lease's evidence of a real event (#285) and its sample budget (#289).
            internal float RunningSince = -1f, SampleBudget = 4f, BudgetAt; internal int MaxProgress;
            // Holder: sampling and idle release.
            internal float NextSample, IdleSince = -1f;
            // Guest: a pending claim and a recent grant.
            internal float PendingSince = -100f, GrantedAt = -100f;
            // Watchers: presenting someone else's event.
            internal bool Presenting;
            internal VenueState(SpeedwayVenue venue, ulong entity) { Venue = venue; Entity = entity; }
        }

        // An AI car on a watcher's machine, posed from the holder's samples.
        private sealed class Puppet
        {
            internal readonly GameObject Root; internal readonly VehicleController? Vehicle;
            internal readonly PoseTrail Trail = new PoseTrail(0.15f, 0.03f);
            internal bool Presenting, OriginalAuto;
            internal Vehicle.MultiplayerInstanceType OriginalMode;
            internal float Rpm, Spin, Throttle, EngineAt, EngineSpeed; internal Vector3 EnginePosition;
            internal bool EngineOn; internal ushort Lamps; internal bool LampsKnown;
            internal Puppet(GameObject root, VehicleController? vehicle) { Root = root; Vehicle = vehicle; }
        }

        private struct CarSample
        {
            public byte Index, Flags, Throttle; public Vector3 Position; public Quaternion Rotation; public ushort Rpm, Lamps; public short Spin;
        }

        private bool host => session?.IsHost == true;
        private readonly Speedway? speedway;
        private readonly Event_Championship? championship;
        private readonly Event_DerbyDemolition? derby;
        private readonly Gameplay? gameplay;
        private readonly GameObject[] trialObjects;
        private readonly DragTree? dragTree, ovalTree, trackTree, champDragTree, champTrackTree;
        private readonly Canvas_3D_TrafficLight? champLight;
        private readonly PlayerDetector[] champStarts, champCheckpoints;
        private readonly AI_RallyCar[] champCars;
        private readonly Puppet[] champPuppets;
        private readonly Event_DerbyDemolition_Vehicle[] derbyCars;
        private readonly Puppet[] derbyPuppets;
        private readonly Dictionary<WheelController, Puppet> byWheel = new Dictionary<WheelController, Puppet>();
        private readonly Dictionary<VehicleController, Puppet> byVehicle = new Dictionary<VehicleController, Puppet>();
        private readonly VenueState trial = new VenueState(SpeedwayVenue.Trial, TrialEntity);
        private readonly VenueState champ = new VenueState(SpeedwayVenue.Championship, ChampionshipEntity);
        private readonly VenueState derbyVenue = new VenueState(SpeedwayVenue.Derby, DerbyEntity);
        private readonly VenueState[] venues;
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool disposed, applying;
        // The holder's own trial kind while its claim is out, and the native registration a grant replays.
        private byte pendingTrialKind;
        private bool allowNativeChampionship, allowNativeDerby;
        // Watchers' HUD and messages.
        private bool champHud, derbyHud, shownMessage;
        // A local entry notice replaces the holder's screen message but is intentionally not
        // relayed. Its following native Hide must not close an earlier shared result on watchers.
        private bool localEntryNoticeActive;
        private (uint Holder, SpeedwayVenue Venue)? suppressedRemoteEntryNotice;
        private uint messageSeq, presentedMessageSeq;
        private float champLapTime, champLastLap, champBestLap; private int champLap, champStage; private bool champRacing;
        private byte derbyRound, derbyRounds, derbyHealth; private bool derbyUi;
        // Holder: the messages it relays.
        private (string Text, byte Title, bool Hide)? lastSentMessage;
        private byte[]? pendingMessage; private float lastMessageAt; private ushort sentMessageSeq;
        private readonly Dictionary<uint, (ushort Seq, byte[]?[] Parts)> messageParts = new Dictionary<uint, (ushort, byte[]?[])>();
        // Best times: the host's copy, what this guest last applied, and recent trial holders.
        private byte[] records = Array.Empty<byte>();
        private float nextRecords;
        private float[][]? appliedBoards;
        // Host: which peer recently raced for which board (0-2: drag, oval, track; 3-4: championship).
        private readonly Dictionary<(uint Peer, int Board), float> recentRacers = new Dictionary<(uint, int), float>();
        // Holder: when its championship last ran and its derby was last registered; the derby lease its noon went to.
        private float champLastRunningAt = -1000f, derbyLastRegisteredAt = -1000f;
        private uint derbyNoonLease; private bool derbyNoonDone;
        // Where the other players are, for the holder's stream rate (#290).
        private readonly Dictionary<uint, (Vector3 At, float Time)> peerPositions = new Dictionary<uint, (Vector3, float)>();

        internal static bool Relaying { get; private set; }

#if MONMP_DEVELOPMENT
        internal int TestSamples { get; private set; }


        internal int TestCues { get; private set; }


        internal int TestDents { get; private set; }


        internal uint TestHolder(SpeedwayVenue venue) => Venue(venue)?.Holder ?? Nobody;
#endif


        internal SharedSpeedwayEvents()
        {
            speedway = UnityEngine.Object.FindObjectOfType<Speedway>(true);
            championship = UnityEngine.Object.FindObjectOfType<Event_Championship>(true);
            derby = UnityEngine.Object.FindObjectOfType<Event_DerbyDemolition>(true);
            gameplay = UnityEngine.Object.FindObjectOfType<Gameplay>();
            venues = new[] { trial, champ, derbyVenue };
            var objects = new List<GameObject?> { Get<GameObject>(SwDragStart, speedway), Get<GameObject>(SwOvalStart, speedway), Get<GameObject>(SwTrackStart, speedway), Get<GameObject>(SwDragFinish, speedway) };
            objects.AddRange(Get<GameObject[]>(SwOvalCps, speedway) ?? Array.Empty<GameObject>());
            objects.AddRange(Get<GameObject[]>(SwTrackCps, speedway) ?? Array.Empty<GameObject>());
            trialObjects = objects.Select(o => o!).ToArray();
            dragTree = Get<DragTree>(SwDragTree, speedway); ovalTree = Get<DragTree>(SwOvalTree, speedway); trackTree = Get<DragTree>(SwTrackTree, speedway);
            champDragTree = Get<DragTree>(ChDragTree, championship); champTrackTree = Get<DragTree>(ChTrackTree, championship);
            champLight = Get<Canvas_3D_TrafficLight>(ChLight, championship);
            if (champLight == null && championship != null) champLight = UnityEngine.Object.FindObjectOfType<Canvas_3D_TrafficLight>(true);
            champStarts = Get<PlayerDetector[]>(ChStarts, championship) ?? Array.Empty<PlayerDetector>();
            champCheckpoints = ChCheckpointSets.SelectMany(name => Get<PlayerDetector[]>(F(typeof(Event_Championship), name), championship) ?? Array.Empty<PlayerDetector>()).ToArray();
            champCars = ChCars.Select(name => Get<AI_RallyCar>(F(typeof(Event_Championship), name), championship)).Where(car => car != null).Select(car => car!).ToArray();
            champPuppets = champCars.Select(car => new Puppet(car.gameObject, Get<VehicleController>(RallyVc, car))).ToArray();
            derbyCars = (Get<Event_DerbyDemolition_Vehicle[]>(DeAi, derby) ?? Array.Empty<Event_DerbyDemolition_Vehicle>()).Where(car => car != null).Take(5).ToArray();
            derbyPuppets = derbyCars.Select(car => new Puppet(car.gameObject, car.GetComponent<VehicleController>())).ToArray();
        }

        internal static IEnumerable<WorldRecord> InitialRecords() => new[]
        {
            new WorldRecord(TrialEntity, 1, Adapter, EncodeVenue(Nobody, 0, 0, Array.Empty<byte>())),
            new WorldRecord(ChampionshipEntity, 1, Adapter, EncodeVenue(Nobody, 0, 0, Array.Empty<byte>())),
            new WorldRecord(DerbyEntity, 1, Adapter, EncodeVenue(Nobody, 0, 0, Array.Empty<byte>())),
            new WorldRecord(MessageEntity, 1, Adapter, EncodeMessage(0, Nobody, 0, 0, 0, 0, true, "")),
            new WorldRecord(RecordsEntity, 1, Adapter, new byte[] { 0 }),
        };

        internal void Attach(NetworkSession connected, WorldChannel world)
        {
            session = connected; channel = world;
            connected.PeerLeft += OnLeft; connected.PoseReceived += OnPose;
            world.SnapshotApplied += Snapshot; world.Changed += Changed; world.CommandRequested += Command; world.ReplyReceived += ReplyReceived;
        }

        private uint Self => session?.LocalPeerId ?? 0;
        private bool Attached => !disposed && channel?.Ready == true && session?.IsRunning == true;
        private VenueState? Venue(SpeedwayVenue venue) => venue switch { SpeedwayVenue.Trial => trial, SpeedwayVenue.Championship => champ, SpeedwayVenue.Derby => derbyVenue, _ => null };
        private static T? Get<T>(FieldInfo? field, object? owner) where T : class => field != null && owner != null ? field.GetValue(owner) as T : null;
        private static bool Flag(FieldInfo? field, object? owner) => field != null && owner != null && field.GetValue(owner) is bool value && value;
        private static int Int(FieldInfo? field, object? owner) => field != null && owner != null && field.GetValue(owner) is int value ? value : 0;
        private static float Float(FieldInfo? field, object? owner) => field != null && owner != null && field.GetValue(owner) is float value ? value : 0f;
        private static sbyte Stage(DragTree? tree) => tree != null ? (sbyte)Mathf.Clamp(Int(TreeStage, tree), -1, 4) : (sbyte)-1;
        private bool Other(VenueState v) => v.Holder != Nobody && v.Holder != Self;
        private bool Mine(VenueState v) => v.Holder != Nobody && v.Holder == Self;
        internal bool WearsRacingHelmet(uint peer) => Attached && peer != Nobody &&
            (champ.Holder == peer || derbyVenue.Holder == peer);
        private static bool SpeedwayCompleted => Singleton<ToDoList_Manager>.i?.isDone(ToDoList_Line.Speedway_Completed) == true;

        // ---------------------------------------------------------------- encoding

        private static byte[] EncodeVenue(uint holder, uint lease, byte kind, byte[] sample)
        {
            var bytes = new byte[10 + sample.Length];
            bytes[0] = 1; BitConverter.GetBytes(holder).CopyTo(bytes, 1); BitConverter.GetBytes(lease).CopyTo(bytes, 5); bytes[9] = kind;
            sample.CopyTo(bytes, 10);
            return bytes;
        }

        private static void DecodeVenue(byte[] payload, out uint holder, out uint lease, out byte kind, out byte[] sample)
        {
            if (payload.Length < 10 || payload[0] != 1) throw new InvalidDataException("Unknown speedway venue layout.");
            holder = BitConverter.ToUInt32(payload, 1); lease = BitConverter.ToUInt32(payload, 5); kind = payload[9];
            sample = new byte[payload.Length - 10];
            Array.Copy(payload, 10, sample, 0, sample.Length);
        }

        private static byte[] EncodeMessage(byte venue, uint holder, uint seq, byte icon, byte duration, byte title, bool hide, string text)
        {
            var body = Encoding.UTF8.GetBytes(text);
            var bytes = new byte[15 + body.Length];
            bytes[0] = 1; bytes[1] = venue; BitConverter.GetBytes(holder).CopyTo(bytes, 2); BitConverter.GetBytes(seq).CopyTo(bytes, 6);
            bytes[10] = icon; bytes[11] = duration; bytes[12] = title; bytes[13] = (byte)(hide ? 1 : 0); bytes[14] = 0;
            body.CopyTo(bytes, 15);
            return bytes;
        }

        private static void WriteVector(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        private static Vector3 ReadVector(BinaryReader r)
        {
            var v = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            if (!ItemPose.FiniteValue(v.x, 100000) || !ItemPose.FiniteValue(v.y, 100000) || !ItemPose.FiniteValue(v.z, 100000)) throw new InvalidDataException("Invalid speedway position.");
            return v;
        }
        private static ushort Angle(float degrees) => (ushort)Mathf.RoundToInt(Mathf.Repeat(degrees, 360f) / 360f * 65535f);
        private static float Degrees(ushort angle) => angle * 360f / 65535f;
        private static void WriteRotation(BinaryWriter w, Quaternion q) { var e = q.eulerAngles; w.Write(Angle(e.x)); w.Write(Angle(e.y)); w.Write(Angle(e.z)); }
        private static Quaternion ReadRotation(BinaryReader r) => Quaternion.Euler(Degrees(r.ReadUInt16()), Degrees(r.ReadUInt16()), Degrees(r.ReadUInt16()));
        private static short Centimetres(float metres) => (short)Mathf.Clamp(Mathf.RoundToInt(metres * 100f), short.MinValue, short.MaxValue);
        private static byte Unit(float value) => (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);

        private static void WriteCar(BinaryWriter w, CarSample c)
        {
            w.Write(c.Index); w.Write(c.Flags); WriteVector(w, c.Position); WriteRotation(w, c.Rotation);
            w.Write(c.Rpm); w.Write(c.Spin); w.Write(c.Throttle); w.Write(c.Lamps);
        }
        private static CarSample ReadCar(BinaryReader r) => new CarSample
        {
            Index = r.ReadByte(), Flags = r.ReadByte(), Position = ReadVector(r), Rotation = ReadRotation(r),
            Rpm = r.ReadUInt16(), Spin = r.ReadInt16(), Throttle = r.ReadByte(), Lamps = r.ReadUInt16()
        };

        // Trial: kind, the three trees, the start/finish/checkpoint objects and where the racer is.
        private struct TrialSample { public byte Kind; public sbyte Drag, Oval, Track; public ulong Objects; public Vector3 Focus; public bool Racing; }
        private static byte[] Write(TrialSample s)
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write(s.Kind); w.Write(s.Drag); w.Write(s.Oval); w.Write(s.Track); w.Write(s.Objects); WriteVector(w, s.Focus); w.Write(s.Racing);
            return stream.ToArray();
        }
        private static TrialSample ReadTrial(byte[] bytes)
        {
            using var r = new BinaryReader(new MemoryStream(bytes, false));
            var s = new TrialSample { Kind = r.ReadByte(), Drag = r.ReadSByte(), Oval = r.ReadSByte(), Track = r.ReadSByte(), Objects = r.ReadUInt64(), Focus = ReadVector(r), Racing = r.ReadBoolean() };
            if (s.Kind > TrialTrack || s.Drag < -1 || s.Drag > 4 || s.Oval < -1 || s.Oval > 4 || s.Track < -1 || s.Track > 4) throw new InvalidDataException("Invalid trial sample.");
            return s;
        }

        private struct ChampSample
        {
            public byte Flags, Stage, Cars, Starts, Variable, Lap; public ulong Checkpoints; public sbyte DragTree, TrackTree, Light;
            public float LapTime, LastLap, BestLap; public Vector3 Focus; public CarSample[] Moving;
            public bool Running => (Flags & 1) != 0; public bool Racing => (Flags & 2) != 0;
        }
        private static byte[] Write(ChampSample s)
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write(s.Flags); w.Write(s.Stage); w.Write(s.Cars); w.Write(s.Starts); w.Write(s.Checkpoints); w.Write(s.Variable);
            w.Write(s.DragTree); w.Write(s.TrackTree); w.Write(s.Light); w.Write(s.Lap); w.Write(s.LapTime); w.Write(s.LastLap); w.Write(s.BestLap);
            WriteVector(w, s.Focus);
            w.Write((byte)s.Moving.Length); foreach (var car in s.Moving) WriteCar(w, car);
            return stream.ToArray();
        }
        private static ChampSample ReadChampionship(byte[] bytes)
        {
            using var r = new BinaryReader(new MemoryStream(bytes, false));
            var s = new ChampSample
            {
                Flags = r.ReadByte(), Stage = r.ReadByte(), Cars = r.ReadByte(), Starts = r.ReadByte(), Checkpoints = r.ReadUInt64(), Variable = r.ReadByte(),
                DragTree = r.ReadSByte(), TrackTree = r.ReadSByte(), Light = r.ReadSByte(), Lap = r.ReadByte(),
                LapTime = r.ReadSingle(), LastLap = r.ReadSingle(), BestLap = r.ReadSingle(), Focus = ReadVector(r)
            };
            int count = r.ReadByte();
            if (count > 2 || s.Stage > 7 || s.Light < -1 || s.Light > 3 || s.DragTree < -1 || s.DragTree > 4 || s.TrackTree < -1 || s.TrackTree > 4 ||
                !ValidSeconds(s.LapTime) || !ValidSeconds(s.LastLap) || !ValidSeconds(s.BestLap) || (s.Cars & 0xC0) != 0) throw new InvalidDataException("Invalid championship sample.");
            s.Moving = new CarSample[count];
            for (int i = 0; i < count; i++)
            {
                s.Moving[i] = ReadCar(r);
                if (s.Moving[i].Index >= ChCars.Length || i > 0 && s.Moving[i].Index == s.Moving[0].Index) throw new InvalidDataException("Invalid championship car.");
            }
            return s;
        }

        private struct DerbyCar { public byte Flags, Health; public Vector3 Offset; public Quaternion Rotation; public ushort Rpm; public short Spin; }
        private struct DerbySample
        {
            public byte Flags, Raise, Health, Round, Rounds; public Vector3 DetectorAt; public ushort DetectorYaw; public DerbyCar[] Cars;
            public bool Running => (Flags & 1) != 0;
        }
        private static byte[] Write(DerbySample s)
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write(s.Flags); w.Write(s.Raise); w.Write(s.Health); w.Write(s.Round); w.Write(s.Rounds); WriteVector(w, s.DetectorAt); w.Write(s.DetectorYaw);
            w.Write((byte)s.Cars.Length);
            foreach (var car in s.Cars)
            {
                w.Write(car.Flags); w.Write(car.Health);
                w.Write(Centimetres(car.Offset.x)); w.Write(Centimetres(car.Offset.y)); w.Write(Centimetres(car.Offset.z));
                WriteRotation(w, car.Rotation); w.Write(car.Rpm); w.Write(car.Spin);
            }
            return stream.ToArray();
        }
        private static DerbySample ReadDerby(byte[] bytes)
        {
            using var r = new BinaryReader(new MemoryStream(bytes, false));
            var s = new DerbySample { Flags = r.ReadByte(), Raise = r.ReadByte(), Health = r.ReadByte(), Round = r.ReadByte(), Rounds = r.ReadByte(), DetectorAt = ReadVector(r), DetectorYaw = r.ReadUInt16() };
            int count = r.ReadByte();
            if (count > 5) throw new InvalidDataException("Invalid derby sample.");
            s.Cars = new DerbyCar[count];
            for (int i = 0; i < count; i++)
                s.Cars[i] = new DerbyCar
                {
                    Flags = r.ReadByte(), Health = r.ReadByte(), Offset = new Vector3(r.ReadInt16() / 100f, r.ReadInt16() / 100f, r.ReadInt16() / 100f),
                    Rotation = ReadRotation(r), Rpm = r.ReadUInt16(), Spin = r.ReadInt16()
                };
            return s;
        }

        private static bool ValidSeconds(float seconds) => !float.IsNaN(seconds) && !float.IsInfinity(seconds) && seconds >= 0f && seconds <= 36000f;

        // A guest holder's sample, as its native event could show it: at the speedway (#289).
        private void Validate(SpeedwayVenue venue, byte[] sample)
        {
            if (sample.Length == 0) return;
            if (venue == SpeedwayVenue.Trial) { Near(ReadTrial(sample).Focus); return; }
            if (venue == SpeedwayVenue.Championship)
            {
                var s = ReadChampionship(sample);
                foreach (var car in s.Moving) Near(car.Position);
                return;
            }
            var d = ReadDerby(sample);
            if (d.Round > 20 || d.Rounds > 20 || d.Round > d.Rounds + 1) throw new InvalidDataException("Invalid derby round.");
            if ((d.Flags & 8) != 0 && derby != null && (d.DetectorAt - DerbyAnchor()).sqrMagnitude > 1000f * 1000f) throw new InvalidDataException("Derby detector away from the arena.");
        }

        // Within a few kilometres of the speedway's drag start line (the championship's rally leaves the oval).
        private void Near(Vector3 at)
        {
            var line = trialObjects.Length > 0 ? trialObjects[0] : null;
            if (line != null && (at - line.transform.position).sqrMagnitude > 4000f * 4000f) throw new InvalidDataException("Speedway sample away from the speedway.");
        }

        // ---------------------------------------------------------------- leases (host)

        private void Publish(VenueState v) => channel!.Publish(v.Entity, EncodeVenue(v.Holder, v.Lease, v.Kind, v.Sample));

        private void Take(VenueState v, uint holder, byte kind)
        {
            if (v.Holder != holder) { v.Lease++; v.SawRunning = false; v.Paid = false; v.Sample = Array.Empty<byte>(); v.RunningSince = -1f; v.MaxProgress = 0; }
            v.Holder = holder; v.Kind = kind; v.LastSampleAt = Time.unscaledTime;
            Raced(v);
            Debug.Log("MonMP speedway " + v.Venue + " lease " + v.Lease + " to peer " + holder + " (kind " + kind + ")");
            Publish(v);
        }

        private void Free(VenueState v, string reason)
        {
            if (v.Holder == Nobody) return;
            Debug.Log("MonMP speedway " + v.Venue + " lease " + v.Lease + " of peer " + v.Holder + " ended: " + reason);
            Raced(v);
            v.Holder = Nobody; v.Kind = 0; v.Sample = Array.Empty<byte>();
            Publish(v);
            if (v == champ || v == derbyVenue) RestoreForms();
        }

        private bool ChampionshipRunningFor(uint peer) =>
            champ.Holder != Nobody && champ.Holder != peer && champ.Sample.Length > 0 && champ.Sample[0] % 2 == 1;

        // A championship registration is not refused over a running time trial: its races wait for it
        // at the start line instead (AllowChampionshipStart).
        private bool CanGrant(VenueState v, uint peer) => (v.Holder == Nobody || v.Holder == peer) && (v != trial || !ChampionshipRunningFor(peer));

        [MonMP.Core.World.WorldCommandAdapters(SharedSpeedwayEvents.Adapter)]
        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (!host || payload.Length < 2) return;
            if (peer == 0) peer = Self;
            if (entity == MessageEntity) { if (payload[0] == MessagePart) MessageCommand(peer, payload); return; }
            if (entity == RecordsEntity) { if (payload[0] == Best && payload.Length == 7) BestCommand(peer, payload[1], payload[2], BitConverter.ToSingle(payload, 3)); return; }
            var v = venues.FirstOrDefault(candidate => candidate.Entity == entity);
            if (v == null || payload[1] != (byte)v.Venue) return;
            switch (payload[0])
            {
                case Claim when payload.Length == 3:
                {
                    bool granted = CanGrant(v, peer);
                    if (granted) Take(v, peer, payload[2]);
                    if (peer != Self) channel!.Reply(peer, entity, new[] { Claim, payload[1], (byte)(granted ? 1 : 0), payload[2] });
                    return;
                }
                case Release when payload.Length == 2:
                    if (v.Holder == peer && peer != Self) Free(v, "released by the holder");
                    return;
                case Sample:
                {
                    if (v.Holder != peer || peer == Self) return;
                    // At most 40 samples a second per venue, in bursts of four (the holder sends 20).
                    float now = Time.unscaledTime;
                    v.SampleBudget = Mathf.Min(4f, v.SampleBudget + (now - v.BudgetAt) * 40f); v.BudgetAt = now;
                    if (v.SampleBudget < 1f) return;
                    v.SampleBudget -= 1f;
                    var sample = new byte[payload.Length - 2];
                    Array.Copy(payload, 2, sample, 0, sample.Length);
                    try { Validate(v.Venue, sample); } catch (Exception ex) when (ex is InvalidDataException || ex is IOException) { return; }
                    Store(v, sample);
                    return;
                }
                case Reward when payload.Length == 2:
                    if (v.Holder == peer && peer != Self && !v.Paid && Earned(v)) { v.Paid = true; HostReward(v.Venue); }
                    else Debug.Log("MonMP speedway " + v.Venue + " reward request of peer " + peer + " refused (no event seen this lease)");
                    return;
            }
        }

        // What the host saw of the holder's event before a win: the championship reached its final
        // races, the derby its second round, and the event ran long enough to be one. The holder's
        // own machine runs the event, so this bounds what a lease alone can claim rather than
        // proving the race (#285). Reward eligibility belongs to this lease; native events have
        // no payout cooldown that spans different holders or later registrations.
        private bool Earned(VenueState v)
        {
            float now = Time.unscaledTime;
            if (!v.SawRunning || v.RunningSince < 0f) return false;
            if (v == champ) return v.MaxProgress >= 5 && now - v.RunningSince >= 60f;
            if (v == derbyVenue) return v.MaxProgress >= 2 && now - v.RunningSince >= 30f;
            return false;
        }

        // The boards a lease lets its holder set: its trial kind's, or the championship's two.
        private void Raced(VenueState v)
        {
            if (v.Holder == Nobody) return;
            float now = Time.unscaledTime;
            if (v == trial && v.Kind >= TrialDrag && v.Kind <= TrialTrack) recentRacers[(v.Holder, v.Kind - 1)] = now;
            if (v == champ) { recentRacers[(v.Holder, 3)] = now; recentRacers[(v.Holder, 4)] = now; }
        }

        private void OnPose(uint peer, PlayerPose pose) => peerPositions[peer] = (new Vector3(pose.X, pose.Y, pose.Z), Time.unscaledTime);

        // Anyone else near enough to see the event's cars (#290).
        private bool Audience(Vector3 at)
        {
            float now = Time.unscaledTime, range = AudienceRange * AudienceRange;
            foreach (var entry in peerPositions)
                if (entry.Key != Self && now - entry.Value.Time < 5f && (entry.Value.At - at).sqrMagnitude < range) return true;
            return false;
        }

        private void Store(VenueState v, byte[] sample)
        {
            v.LastSampleAt = Time.unscaledTime;
            if (v == trial && sample.Length > 0 && sample[0] >= TrialDrag && sample[0] <= TrialTrack) v.Kind = sample[0];
            Raced(v);
            bool running = sample.Length > 0 && (v == trial || sample[0] % 2 == 1);
            if (running)
            {
                v.SawRunning = true;
                if (v.RunningSince < 0f) v.RunningSince = Time.unscaledTime;
                // The championship's stage, the derby's round.
                if (v == champ && sample.Length > 1) v.MaxProgress = Math.Max(v.MaxProgress, sample[1]);
                if (v == derbyVenue && sample.Length > 3) v.MaxProgress = Math.Max(v.MaxProgress, sample[3]);
            }
            v.Sample = sample;

#if MONMP_DEVELOPMENT
            TestSamples++;
#endif

            Publish(v);
        }

        private void OnLeft(uint peer)
        {
            if (!host) return;
            foreach (var v in venues) if (v.Holder == peer) Free(v, "holder left");
            messageParts.Remove(peer);
        }

        // A guest's win: the rewards its own machine cannot give (cash, trophy, unlocks, counters).
        private void HostReward(SpeedwayVenue venue)
        {
            if (speedway == null || gameplay == null) return;
            var todo = Singleton<ToDoList_Manager>.i;
            using (RemoteActionScope.Enter())
            using (SpatialSounds.Mute())
            {
                if (venue == SpeedwayVenue.Derby && derby != null)
                {
                    if (!todo.isDone(ToDoList_Line.Speedway_isDerbyWon))
                    {
                        todo.Speedway_DerbyEventWon();
                        gameplay.Store_PostOffice.ShipThisLetterToPlayer(Envelope_Type.Publicite_Roger_Rally);
                        UnityEngine.Object.FindObjectOfType<Store_TravelingVan>()?.EnableRallyShocks();
                        UnityEngine.Object.FindObjectOfType<Store_Roger>()?.EnableRallyShocks();
                    }
                    var at = Get<Transform>(DeTrophyAt, derby);
                    if (at != null) UnityEngine.Object.Instantiate(Singleton<GameAssets>.i.Trophy_DerbyEvent, at.position, at.rotation);
                    Singleton<Cash_Manager>.i.Event_Received(2500f);
                    speedway.DerbyEventWon();
                }
                else if (venue == SpeedwayVenue.Championship && championship != null)
                {
                    if (!todo.isDone(ToDoList_Line.Speedway_IsChampionshipWon))
                    {
                        todo.Speedway_ChampionshipWon();
                        UnityEngine.Object.FindObjectOfType<Store_Roger>()?.EnableDragTires();
                        UnityEngine.Object.FindObjectOfType<Store_TravelingVan>()?.EnableDragTires();
                    }
                    speedway.ChampionshipEventWon();
                    var at = Get<Transform>(ChTrophyAt, championship);
                    var trophy = speedway.Get_Player_ChampionshipWon == 5 ? Singleton<GameAssets>.i.Trophy_EventChampionship_GrandChampion : Singleton<GameAssets>.i.Trophy_EventChampionship;
                    if (at != null) UnityEngine.Object.Instantiate(trophy, at.position, at.rotation);
                    Singleton<Cash_Manager>.i.Event_Received(5000f);
                }
            }
            MonMPPlugin.Instance?.Items?.DiscoverSoon();
            Debug.Log("MonMP speedway " + venue + " reward granted to the guest holder");
            nextRecords = 0;
        }

        // ---------------------------------------------------------------- claims (every machine)

        private void RequestClaim(VenueState v, byte kind)
        {
            if (host) { if (CanGrant(v, Self)) Take(v, Self, kind); return; }
            if (Time.unscaledTime - v.PendingSince < 2f) return;
            v.PendingSince = Time.unscaledTime;
            channel!.Request(v.Entity, new[] { Claim, (byte)v.Venue, kind }, useLease: true);
        }

        private void RequestRelease(VenueState v)
        {
            if (host) Free(v, "released by the holder");
            else channel!.Request(v.Entity, new[] { Release, (byte)v.Venue }, useLease: true);
            v.IdleSince = -1f;
        }

        private static void Refused(string? key = null) =>
            Singleton<UI_ErrorMessage>.i.Show(TEXT.GetText(key ?? "RACEMANAGER_ARaceIsAlreadyGoingOn"));

        // Speedway.PlayerEntered_Drag/Oval/Track: one time trial at a time, none during someone's championship.
        internal bool AllowTrialStart(byte kind)
        {
            if (!Attached || speedway == null) return true;
            if (Other(trial) || ChampionshipRunningFor(Self))
            {
                Singleton<UI_EventMessage>.i.Show(TEXT.GetText("RACEMANAGER_ARaceIsAlreadyGoingOn"), TEXT.GetText(TitleKeys[kind]), 3f);
                return false;
            }
            if (Mine(trial)) { trial.Kind = kind; return true; }
            pendingTrialKind = kind;
            RequestClaim(trial, kind);
            // A guest starts its countdown at once; a refusal stops it (the countdown takes seconds).
            return host ? Mine(trial) : true;
        }

        // Event_Championship.StartDetector_PlayerIn: the registrant's races wait for a running time trial.
        internal bool AllowChampionshipStart()
        {
            if (!Attached) return true;
            if (Other(champ)) return false;
            if (Other(trial))
            {
                Singleton<UI_EventMessage>.i.Show(TEXT.GetText("RACEMANAGER_ARaceIsAlreadyGoingOn"), TEXT.GetText("SPEEDWAY_Championship_Name"), 3f);
                return false;
            }
            return true;
        }

        // Event_Championship / Event_DerbyDemolition.InscriptionFormClicked: one registered player each.
        internal bool AllowRegistration(SpeedwayVenue venue)
        {
            if (!Attached) return true;
            var v = Venue(venue)!;
            if (venue == SpeedwayVenue.Championship && allowNativeChampionship) { allowNativeChampionship = false; return true; }
            if (venue == SpeedwayVenue.Derby && allowNativeDerby) { allowNativeDerby = false; return true; }
            if (Other(v)) { Refused(); return false; }
            if (host) { RequestClaim(v, 0); return Mine(v); }
            if (Mine(v)) return true;
            RequestClaim(v, 0);
            return false;
        }

        private void ReplyReceived(ulong entity, byte[] payload)
        {
            if (host || payload.Length != 4 || payload[0] != Claim) return;
            var v = venues.FirstOrDefault(candidate => candidate.Entity == entity);
            if (v == null) return;
            v.PendingSince = -100f;
            bool granted = payload[2] == 1;
            Debug.Log("MonMP speedway " + v.Venue + " claim reply: granted=" + granted);
            if (!granted)
            {
                if (v == trial) AbortTrial(); else Refused();
                return;
            }
            v.GrantedAt = Time.unscaledTime;
            // The host's grant arrives after the native registration was held back: run it now.
            if (v == champ && championship != null) { allowNativeChampionship = true; try { championship.InscriptionFormClicked(); } finally { allowNativeChampionship = false; } }
            if (v == derbyVenue && derby != null) { allowNativeDerby = true; try { derby.InscriptionFormClicked(); } finally { allowNativeDerby = false; } }
        }

        // A refused guest's countdown (or, if it already went, its race) stops.
        private void AbortTrial()
        {
            if (speedway == null) return;
            speedway.PlayerExited_Drag(); speedway.PlayerExited_Oval(); speedway.PlayerExited_Track();
            if (Flag(SwRacing, speedway)) speedway.PlayerCross_ExitSpeedway();
            Singleton<UI_EventMessage>.i.Show(TEXT.GetText("RACEMANAGER_ARaceIsAlreadyGoingOn"), TEXT.GetText(TitleKeys[Math.Max(1, (int)pendingTrialKind)]), 3f);
        }

        // Watchers: the native checkpoints and start lines shown for someone else's event never fire for them.
        // A trial already running here finishes even if someone's championship opens meanwhile (#282).
        internal bool BlocksTrialCrossing => Attached && (trial.Presenting || champ.Presenting && !Mine(trial));
        internal bool BlocksChampionship => Attached && Other(champ);
        internal bool BlocksDerbyStart => Attached && Other(derbyVenue);
        // Event_DerbyDemolition.Tick at noon: with someone else registered, the derby stays open for them.
        internal bool KeepsDerbyOpen => Attached && Other(derbyVenue) && !Flag(DeRegistered, derby);

        // ---------------------------------------------------------------- snapshot / changes

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> snapshot)
        {
            if (host) return;
            foreach (var v in venues) if (snapshot.TryGetValue(v.Entity, out var record)) Receive(v, record.Payload);
            if (snapshot.TryGetValue(RecordsEntity, out var boards)) ApplyRecords(boards.Payload);
            if (snapshot.TryGetValue(MessageEntity, out var shown))
                try { presentedMessageSeq = BitConverter.ToUInt32(shown.Payload, 6); } catch (ArgumentException) { }
        }

        private void Changed(WorldRecord record)
        {
            if (host)
            {
                // The host sees its own publishes, including a guest holder's relayed samples.
                var own = venues.FirstOrDefault(v => v.Entity == record.Id);
                if (own != null) Present(own);
                else if (record.Id == MessageEntity) ReceiveMessage(record.Payload);
                return;
            }
            var venue = venues.FirstOrDefault(v => v.Entity == record.Id);
            if (venue != null) Receive(venue, record.Payload);
            else if (record.Id == MessageEntity) ReceiveMessage(record.Payload);
            else if (record.Id == RecordsEntity) ApplyRecords(record.Payload);
        }

        private void Receive(VenueState v, byte[] payload)
        {
            try { DecodeVenue(payload, out var holder, out var lease, out var kind, out var sample); v.Holder = holder; v.Lease = lease; v.Kind = kind; v.Sample = sample; }
            catch (Exception ex) when (ex is InvalidDataException || ex is ArgumentException) { return; }
            if (v != trial && v.Holder != Self) ClearLocalRegistration(v);
            if ((v == champ || v == derbyVenue) && v.Holder == Nobody) RestoreForms();
            Present(v);
        }

        // A guest's loaded world carries the host's registrations; only the lease holder is registered.
        private void ClearLocalRegistration(VenueState v)
        {
            if (Time.unscaledTime - v.GrantedAt < 3f) return;
            if (v == champ && championship != null && championship.Get_IsPlayerRegistered && !Flag(ChRunning, championship))
            {
                championship.Get_IsPlayerRegistered = false;
                Debug.Log("MonMP speedway: the championship registration is peer " + v.Holder + "'s, not this player's");
            }
            if (v == derbyVenue && derby != null && Flag(DeRegistered, derby) && !Flag(DeRunning, derby))
            {
                DeRegistered?.SetValue(derby, false);
                Debug.Log("MonMP speedway: the derby registration is peer " + v.Holder + "'s, not this player's");
            }
            HideForm(v);
        }

        private void HideForm(VenueState v)
        {
            if (v.Holder == Nobody || v.Holder == Self) return;
            var form = v == champ ? Get<Furniture_SwitchButton>(ChForm, championship) : Get<Furniture_SwitchButton>(DeForm, derby);
            if (form != null && form.gameObject.activeSelf) form.gameObject.SetActive(false);
        }

        // The registration forms come back as natively once no one holds the registration.
        private void RestoreForms()
        {
            if (!SpeedwayCompleted) return;
            if (champ.Holder == Nobody && championship != null)
            {
                var form = Get<Furniture_SwitchButton>(ChForm, championship);
                bool show = !championship.Get_IsPlayerRegistered && !Flag(ChRunning, championship);
                if (form != null && form.gameObject.activeSelf != show) form.gameObject.SetActive(show);
            }
            if (derbyVenue.Holder == Nobody && derby != null)
            {
                var form = Get<Furniture_SwitchButton>(DeForm, derby);
                int tick = UniStorm.UniStormSystem.Instance != null ? UniStorm.UniStormSystem.Instance.GetCurrentTick : 0;
                bool show = derby.isStoreOpen() && !Flag(DeRegistered, derby) && tick < 720;
                if (form != null && form.gameObject.activeSelf != show) form.gameObject.SetActive(show);
            }
        }

        // ---------------------------------------------------------------- holder: sampling

        internal void Update()
        {
            if (!Attached || speedway == null) return;
            try
            {
                Relaying = false;
                HoldRegistrations();
                if (Mine(trial)) HoldTrial();
                if (Mine(champ)) HoldChampionship();
                if (Mine(derbyVenue)) HoldDerby();
                if (host)
                {
                    foreach (var v in venues)
                        if (v.Holder != Nobody && v.Holder != Self && Time.unscaledTime - v.LastSampleAt > SilentSeconds) Free(v, "holder silent");
                    if (Time.unscaledTime >= nextRecords) PublishRecords();
                }
                else SendBoards();
                FlushMessage();
                Watch();
            }
            finally { Relaying = false; }
        }

        // Registration leases follow the native registration: a player registered here claims it; a
        // holder whose registration ended (event over, store closed) hands it back.
        private void HoldRegistrations()
        {
            float now = Time.unscaledTime;
            if (Flag(ChRunning, championship)) champLastRunningAt = now;
            if (Flag(DeRegistered, derby) || Flag(DeRunning, derby)) derbyLastRegisteredAt = now;
            // The championship's cars stop and lower for up to 25 s after it ends (#283); the derby's
            // cars lower for five after it or its store closes, not forever (#284).
            bool champCleanup = Mine(champ) && now - champLastRunningAt < CleanupSeconds && champCars.Any(car => car != null && car.gameObject.activeSelf);
            bool derbyCleanup = !derbyVenue.Presenting && now - derbyLastRegisteredAt < CleanupSeconds && DerbyRaised() > 0;
            bool champRegistered = championship != null && (championship.Get_IsPlayerRegistered || Flag(ChRunning, championship) || champCleanup);
            bool derbyRegistered = derby != null && (Flag(DeRegistered, derby) || Flag(DeRunning, derby) || derbyCleanup);
            if (!host && Mine(derbyVenue)) DeliverDerbyNoon();
            if (host)
            {
                if (champRegistered && champ.Holder == Nobody) Take(champ, Self, 0);
                if (derbyRegistered && derbyVenue.Holder == Nobody) Take(derbyVenue, Self, 0);
            }
            Idle(champ, !champRegistered && Mine(champ) && Time.unscaledTime - champ.GrantedAt > 3f, 3f);
            Idle(derbyVenue, !derbyRegistered && Mine(derbyVenue) && Time.unscaledTime - derbyVenue.GrantedAt > 3f, 3f);
            if (Other(champ)) HideForm(champ);
            if (Other(derbyVenue)) HideForm(derbyVenue);
        }

        // A guest's clock does not dispatch minute changes, so its native derby never hears noon: the
        // registered holder's derby gets its noon Tick once per lease, as soon as the shared clock is
        // at or past noon, whatever minutes were skipped (#294).
        private void DeliverDerbyNoon()
        {
            if (derby == null || DeTick == null) return;
            if (derbyNoonLease != derbyVenue.Lease) { derbyNoonLease = derbyVenue.Lease; derbyNoonDone = false; }
            if (derbyNoonDone || !Flag(DeRegistered, derby) || Flag(DeRunning, derby) || Flag(DeRaising, derby) || Flag(DeLowering, derby) || DerbyRaised() > 0) return;
            var clock = UniStorm.UniStormSystem.Instance;
            if (clock == null || clock.GetCurrentTick < 720) return;
            derbyNoonDone = true;
            Debug.Log("MonMP speedway: noon for this guest's derby registration (clock minute " + clock.GetCurrentTick + ")");
            try { DeTick.Invoke(derby, new object[] { 720 }); }
            catch (TargetInvocationException ex) { Debug.LogWarning("MonMP speedway: the derby's noon failed: " + ex.InnerException?.Message); }
        }

        // Event_DerbyDemolition.CloseStore of a registration that never started calls its LowerCars
        // iterator without running it, which leaves the cars raised: they lower here (#284).
        internal static bool DerbyCloseWouldStrandCars(Event_DerbyDemolition derby) =>
            Flag(DeRegistered, derby) && !Flag(DeRunning, derby) && Int(DeRound, derby) == 1 && !Flag(DeLowering, derby);

        internal void DerbyClosed(Event_DerbyDemolition closed, bool stranded)
        {
            if (!Attached || !stranded || closed != derby || DeLowering == null || Flag(DeLowering, derby) || !(DeLowerCars?.Invoke(derby, null) is System.Collections.IEnumerator lowering)) return;
            Debug.Log("MonMP speedway: the derby closed with its cars raised for an unstarted registration; lowering them");
            derby.StartCoroutine(lowering);
        }

        private void Idle(VenueState v, bool idle, float seconds)
        {
            if (!idle) { v.IdleSince = -1f; return; }
            if (v.IdleSince < 0) { v.IdleSince = Time.unscaledTime; return; }
            if (Time.unscaledTime - v.IdleSince > seconds) RequestRelease(v);
        }

        private void Send(VenueState v, byte[] sample, bool active)
        {
            float now = Time.unscaledTime;
            if (now < v.NextSample) return;
            v.NextSample = now + (active ? SampleSeconds : KeepAliveSeconds);
            if (host) { if (!v.Sample.SequenceEqual(sample) || now - v.LastSampleAt > KeepAliveSeconds) Store(v, sample); return; }
            var command = new byte[2 + sample.Length];
            command[0] = Sample; command[1] = (byte)v.Venue;
            sample.CopyTo(command, 2);
            channel!.Request(v.Entity, command, useLease: true);

#if MONMP_DEVELOPMENT
            TestSamples++;
#endif

        }

        private Vector3 LocalFocus()
        {
            var vehicle = gameplay != null ? gameplay.Get_CurrentVehicleController : null;
            if (vehicle != null) return vehicle.transform.position;
            var camera = Camera.main;
            return camera != null ? camera.transform.position : Vector3.zero;
        }

        private void HoldTrial()
        {
            bool starting = Flag(SwInDrag, speedway) || Flag(SwInOval, speedway) || Flag(SwInTrack, speedway);
            var s = new TrialSample { Kind = trial.Kind, Drag = Stage(dragTree), Oval = Stage(ovalTree), Track = Stage(trackTree), Focus = LocalFocus() };
            for (int i = 0; i < trialObjects.Length && i < 64; i++) if (trialObjects[i] != null && trialObjects[i].activeSelf) s.Objects |= 1UL << i;
            // A race is its lit tree or its finish line and checkpoints: a timed-out race clears
            // those but leaves the native IsRacing set until the racer leaves the speedway (#281).
            bool course = s.Drag >= 0 || s.Oval >= 0 || s.Track >= 0 || (s.Objects >> 3) != 0;
            s.Racing = Flag(SwRacing, speedway) && course;
            bool idle = !starting && !course;
            Send(trial, Write(s), !idle && Audience(s.Focus));
            Idle(trial, idle, 1.5f);
        }

        private static int TypeIndex(int stage) => stage <= 2 ? 0 : stage <= 4 ? 1 : 2;

        private void HoldChampionship()
        {
            if (championship == null) return;
            bool running = Flag(ChRunning, championship);
            int stage = Int(ChStage, championship);
            var s = new ChampSample
            {
                Flags = (byte)((running ? 1 : 0) | (Flag(ChRacing, championship) ? 2 : 0)), Stage = (byte)Mathf.Clamp(stage, 0, 7),
                Variable = 255, DragTree = Stage(champDragTree), TrackTree = Stage(champTrackTree), Light = TrafficLight(champLight),
                Lap = (byte)Mathf.Clamp(Int(ChLap, championship), 0, 255), LapTime = Float(ChLapTime, championship), LastLap = Float(ChLastLap, championship),
                Focus = LocalFocus()
            };
            var best = speedway!.Get_Player_Best_Championship_Lap;
            s.BestLap = best != null && best.Length == 3 ? best[TypeIndex(stage)] : 0f;
            for (int i = 0; i < champStarts.Length && i < 8; i++) if (champStarts[i] != null && champStarts[i].gameObject.activeSelf) s.Starts |= (byte)(1 << i);
            for (int i = 0; i < champCheckpoints.Length && i < 64; i++)
            {
                var detector = champCheckpoints[i];
                if (detector == null || !detector.gameObject.activeSelf) continue;
                s.Checkpoints |= 1UL << i;
                if (s.Variable == 255 && Get<GameObject[]>(DetectorVariables, detector) is GameObject[] variables)
                    for (int j = 0; j < variables.Length; j++) if (variables[j] != null && variables[j].activeSelf) { s.Variable = (byte)j; break; }
            }
            var moving = new List<CarSample>();
            for (int i = 0; i < champCars.Length; i++)
            {
                if (champCars[i] == null || !champCars[i].gameObject.activeSelf) continue;
                s.Cars |= (byte)(1 << i);
                if (moving.Count < 2 && champPuppets[i].Vehicle != null) moving.Add(CaptureCar((byte)i, champPuppets[i].Vehicle!));
            }
            s.Moving = moving.ToArray();
            bool seen = Audience(s.Focus);
            foreach (var car in s.Moving) seen |= Audience(car.Position);
            Send(champ, Write(s), (running || s.Cars != 0) && seen);
        }

        private static sbyte TrafficLight(Canvas_3D_TrafficLight? light)
        {
            if (light == null || !(LightObject?.GetValue(light) is GameObject lamp) || !lamp.activeSelf || !(LightStatus?.GetValue(light) is Enum status)) return -1;
            return (sbyte)Mathf.Clamp(Convert.ToInt32(status), -1, 3);
        }

        private static CarSample CaptureCar(byte index, VehicleController vehicle)
        {
            var c = new CarSample { Index = index, Position = vehicle.transform.position, Rotation = vehicle.transform.rotation };
            c.Rpm = (ushort)Mathf.Clamp(Mathf.Abs(vehicle.powertrain.engine.angularVelocity) * 9.549297f, 0f, 65000f);
            float spin = 0f; int wheels = 0;
            foreach (var wheel in vehicle.Wheels) { if (wheel?.wheelController?.wheel == null) continue; spin += wheel.wheelController.wheel.angularVelocity; wheels++; }
            c.Spin = (short)Mathf.Clamp(Mathf.RoundToInt((wheels > 0 ? spin / wheels : 0f) * 10f), short.MinValue, short.MaxValue);
            c.Throttle = Unit(vehicle.input.Throttle);
            c.Flags = (byte)(vehicle.powertrain.engine.IsRunning ? 1 : 0);
            try { c.Lamps = NativeVehicleLighting.Capture(vehicle).Lamps; } catch (Exception) { c.Lamps = 0; }
            return c;
        }

        private float DerbyRaised()
        {
            var cars = Get<GameObject>(DeCars, derby);
            if (cars == null || derby == null) return 0f;
            var open = (Vector3)(DeOpenAt?.GetValue(derby) ?? cars.transform.position);
            var closed = (Vector3)(DeClosedAt?.GetValue(derby) ?? cars.transform.position);
            return Mathf.Abs(open.y - closed.y) < 0.01f ? 0f : Mathf.Clamp01(Mathf.InverseLerp(closed.y, open.y, cars.transform.position.y));
        }

        private Vector3 DerbyAnchor() => derby != null && DeOpenAt?.GetValue(derby) is Vector3 open ? open : Vector3.zero;

        private void HoldDerby()
        {
            if (derby == null) return;
            bool running = Flag(DeRunning, derby);
            var missile = Get<Event_DerbyDemolition_Vehicle>(DePlayerCar, derby);
            var detector = Get<GameObject>(DeDetector, derby);
            var ui = Get<UI_Event_DerbyDemolition>(DeUi, derby);
            var steam = Get<ParticleSystem>(DvSteam, missile);
            var s = new DerbySample
            {
                Flags = (byte)((running ? 1 : 0) | (Get<GameObject>(DeSpectators, derby)?.activeSelf == true ? 2 : 0) | (Get<GameObject>(DeBlock, derby)?.activeSelf == true ? 4 : 0) |
                    (detector?.activeSelf == true ? 8 : 0) | ((Get<GameObject>(DerbyUiRoot, ui)?.activeSelf ?? false) ? 16 : 0) | (steam != null && steam.isPlaying ? 32 : 0)),
                Raise = Unit(DerbyRaised()), Health = Unit(Health(missile)), Round = (byte)Mathf.Clamp(Int(DeRound, derby), 0, 255), Rounds = (byte)Mathf.Clamp(Int(DeRounds, derby), 0, 255),
                DetectorAt = detector != null ? detector.transform.position : Vector3.zero, DetectorYaw = detector != null ? Angle(detector.transform.eulerAngles.y) : (ushort)0
            };
            var anchor = DerbyAnchor();
            s.Cars = new DerbyCar[derbyCars.Length];
            bool anyCar = false;
            for (int i = 0; i < derbyCars.Length; i++)
            {
                var car = derbyCars[i]; var vehicle = derbyPuppets[i].Vehicle;
                bool active = car != null && car.gameObject.activeSelf;
                anyCar |= active;
                var carSteam = Get<ParticleSystem>(DvSteam, car);
                var c = new DerbyCar
                {
                    Flags = (byte)((active ? 1 : 0) | (Get<Slider>(DvSlider, car)?.gameObject.activeSelf == true ? 2 : 0) | (carSteam != null && carSteam.isPlaying ? 4 : 0) |
                        (vehicle != null && vehicle.input.IsOn ? 8 : 0)),
                    Health = Unit(Health(car))
                };
                if (active && vehicle != null)
                {
                    var sample = CaptureCar((byte)i, vehicle);
                    c.Offset = vehicle.transform.position - anchor; c.Rotation = vehicle.transform.rotation; c.Rpm = sample.Rpm; c.Spin = sample.Spin;
                }
                else c.Rotation = Quaternion.identity;
                s.Cars[i] = c;
            }
            Send(derbyVenue, Write(s), (running || s.Raise > 0 || anyCar) && Audience(anchor));
        }

        private static float Health(Event_DerbyDemolition_Vehicle? car)
        {
            float max = Float(DvMax, car);
            return max > 0 ? Float(DvHealth, car) / max : 0f;
        }

        // ---------------------------------------------------------------- watchers: presentation

        private float Distance(Vector3 at)
        {
            var camera = Camera.main;
            return camera != null ? Vector3.Distance(camera.transform.position, at) : float.MaxValue;
        }

        private bool Watching(SpeedwayVenue venue)
        {
            switch (venue)
            {
                case SpeedwayVenue.Trial:
                    try { return trial.Sample.Length > 0 && Distance(ReadTrial(trial.Sample).Focus) < WatchRange; } catch (Exception) { return false; }
                case SpeedwayVenue.Championship:
                    try { return champ.Sample.Length > 0 && Distance(ReadChampionship(champ.Sample).Focus) < WatchRange; } catch (Exception) { return false; }
                case SpeedwayVenue.Derby:
                    return derby != null && Distance(DerbyAnchor()) < WatchRange;
                case SpeedwayVenue.Meet:
                    var focus = MonMPPlugin.Instance?.World?.MeetRace?.Focus;
                    return focus.HasValue && Distance(focus.Value) < WatchRange;
            }
            return false;
        }

        private void Present(VenueState v)
        {
            if (v.Holder == Self && v.Presenting) Restore(v);
            if (!Other(v) || v.Sample.Length == 0)
            {
                if (v.Presenting) Restore(v);
                return;
            }
            applying = true;
            try
            {
                using (SpatialSounds.Mute())
                {
                    if (v == trial) PresentTrial(ReadTrial(v.Sample));
                    else if (v == champ) PresentChampionship(ReadChampionship(v.Sample));
                    else PresentDerby(ReadDerby(v.Sample));
                }
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException) { }
            finally { applying = false; }
        }

        private void Restore(VenueState v)
        {
            applying = true;
            try
            {
                using (SpatialSounds.Mute())
                {
                    if (v == trial) RestoreTrial();
                    else if (v == champ) RestoreChampionship();
                    else RestoreDerby();
                }
            }
            finally { applying = false; }
        }

        private static void Active(GameObject? go, bool active) { if (go != null && go.activeSelf != active) go.SetActive(active); }
        private static void Tree(DragTree? tree, sbyte stage) { if (tree != null && Stage(tree) != stage) tree.Stage(stage); }

        private void PresentTrial(TrialSample s)
        {
            trial.Presenting = true;
            for (int i = 0; i < trialObjects.Length && i < 64; i++) Active(trialObjects[i], (s.Objects & (1UL << i)) != 0);
            Tree(dragTree, s.Drag); Tree(ovalTree, s.Oval); Tree(trackTree, s.Track);
        }

        private void RestoreTrial()
        {
            trial.Presenting = false;
            for (int i = 3; i < trialObjects.Length; i++) Active(trialObjects[i], false);
            Tree(dragTree, -1); Tree(ovalTree, -1); Tree(trackTree, -1);
            RestoreRaces();
        }

        // The time-trial start lines as this player's own speedway would have them.
        private void RestoreRaces()
        {
            if (speedway == null || !SpeedwayCompleted || trial.Presenting || champ.Presenting) return;
            if (Flag(SwRacing, speedway) || Mine(trial)) return;
            bool enabled = Int(ChStage, championship) == 0;
            for (int i = 0; i < 3 && i < trialObjects.Length; i++) Active(trialObjects[i], enabled);
        }

        private void PresentChampionship(ChampSample s)
        {
            if (!s.Running && s.Cars == 0) { if (champ.Presenting) RestoreChampionship(); return; }
            champ.Presenting = true;
            for (int i = 0; i < champStarts.Length && i < 8; i++) Active(champStarts[i]?.gameObject, (s.Starts & (1 << i)) != 0);
            for (int i = 0; i < champCheckpoints.Length && i < 64; i++)
            {
                var detector = champCheckpoints[i];
                bool active = (s.Checkpoints & (1UL << i)) != 0;
                Active(detector?.gameObject, active);
                if (active && detector != null && s.Variable != 255 && Get<GameObject[]>(DetectorVariables, detector)?.Length > s.Variable) detector.EnableVariable(s.Variable);
            }
            Tree(champDragTree, s.DragTree); Tree(champTrackTree, s.TrackTree);
            if (champLight != null && TrafficLight(champLight) != s.Light) champLight.ChangeLight(s.Light);
            // The holder's own time-trial start lines are off while its championship runs.
            if (s.Running && !Mine(trial)) for (int i = 0; i < 3 && i < trialObjects.Length; i++) Active(trialObjects[i], false);
            for (int i = 0; i < champPuppets.Length; i++) Show(champPuppets[i], (s.Cars & (1 << i)) != 0);
            foreach (var car in s.Moving)
                if (car.Index < champPuppets.Length) Feed(champPuppets[car.Index], car.Position, car.Rotation, car.Rpm, car.Spin / 10f, car.Throttle / 255f, (car.Flags & 1) != 0, car.Lamps);
            champRacing = s.Racing; champStage = s.Stage; champLap = s.Lap; champLapTime = s.LapTime; champLastLap = s.LastLap; champBestLap = s.BestLap;
            if (!s.Running) CloseChampionshipHud();
        }

        private void RestoreChampionship()
        {
            champ.Presenting = false;
            foreach (var start in champStarts) Active(start?.gameObject, false);
            foreach (var detector in champCheckpoints) Active(detector?.gameObject, false);
            // This player's own championship (if any) keeps its lights.
            if (!Flag(ChRunning, championship)) { Tree(champDragTree, -1); Tree(champTrackTree, -1); if (champLight != null && TrafficLight(champLight) != -1) champLight.ChangeLight(-1); }
            foreach (var puppet in champPuppets) Show(puppet, false);
            CloseChampionshipHud();
            RestoreRaces();
        }

        private void CloseChampionshipHud()
        {
            if (!champHud) return;
            champHud = false;
            if (!Flag(ChRunning, championship)) Get<UI_Event_Championship>(ChUi, championship)?.ForceClose();
        }

        private void PresentDerby(DerbySample s)
        {
            bool anyCar = s.Cars.Any(car => (car.Flags & 1) != 0);
            if (!s.Running && s.Raise == 0 && !anyCar && (s.Flags & 2) == 0) { if (derbyVenue.Presenting) RestoreDerby(); return; }
            if (derby == null) return;
            derbyVenue.Presenting = true;
            var cars = Get<GameObject>(DeCars, derby);
            if (cars != null && DeOpenAt?.GetValue(derby) is Vector3 open && DeClosedAt?.GetValue(derby) is Vector3 closed)
                cars.transform.position = Vector3.Lerp(closed, open, s.Raise / 255f);
            Active(Get<GameObject>(DeSpectators, derby), (s.Flags & 2) != 0);
            Active(Get<GameObject>(DeBlock, derby), (s.Flags & 4) != 0);
            var detector = Get<GameObject>(DeDetector, derby);
            Active(detector, (s.Flags & 8) != 0);
            if (detector != null && (s.Flags & 8) != 0) detector.transform.SetPositionAndRotation(s.DetectorAt, Quaternion.Euler(0f, Degrees(s.DetectorYaw), 0f));
            var anchor = DerbyAnchor();
            for (int i = 0; i < derbyPuppets.Length; i++)
            {
                var car = i < s.Cars.Length ? s.Cars[i] : default;
                bool active = (car.Flags & 1) != 0;
                Show(derbyPuppets[i], active);
                if (!active) continue;
                Feed(derbyPuppets[i], anchor + car.Offset, car.Rotation, car.Rpm, car.Spin / 10f, 0f, (car.Flags & 8) != 0, null);
                HealthBar(Get<Slider>(DvSlider, derbyCars[i]), Get<Image>(DvImage, derbyCars[i]), (car.Flags & 2) != 0, car.Health / 255f);
                Steam(Get<ParticleSystem>(DvSteam, derbyCars[i]), (car.Flags & 4) != 0, car.Health / 255f);
            }
            Steam(Get<ParticleSystem>(DvSteam, Get<Event_DerbyDemolition_Vehicle>(DePlayerCar, derby)), (s.Flags & 32) != 0, s.Health / 255f);
            derbyUi = (s.Flags & 16) != 0; derbyRound = s.Round; derbyRounds = s.Rounds; derbyHealth = s.Health;
            if (!derbyUi) CloseDerbyHud();
        }

        private void RestoreDerby()
        {
            derbyVenue.Presenting = false;
            if (derby == null) return;
            var cars = Get<GameObject>(DeCars, derby);
            if (cars != null && DeClosedAt?.GetValue(derby) is Vector3 closed && !Flag(DeRegistered, derby)) cars.transform.position = closed;
            Active(Get<GameObject>(DeSpectators, derby), false);
            Active(Get<GameObject>(DeBlock, derby), false);
            Active(Get<GameObject>(DeDetector, derby), false);
            foreach (var puppet in derbyPuppets)
            {
                if (puppet.Presenting) puppet.Vehicle?.damageHandler?.Repair();
                Show(puppet, false);
            }
            for (int i = 0; i < derbyCars.Length; i++) { Active(Get<Slider>(DvSlider, derbyCars[i])?.gameObject, false); Steam(Get<ParticleSystem>(DvSteam, derbyCars[i]), false, 1f); }
            Steam(Get<ParticleSystem>(DvSteam, Get<Event_DerbyDemolition_Vehicle>(DePlayerCar, derby)), false, 1f);
            derbyUi = false;
            CloseDerbyHud();
        }

        private void CloseDerbyHud()
        {
            if (!derbyHud) return;
            derbyHud = false;
            if (!Flag(DeRunning, derby)) Get<UI_Event_DerbyDemolition>(DeUi, derby)?.Derby_Disable();
        }

        private static Color Red(float value) { value = Mathf.Clamp01(value); return new Color(1f - value, value, 0f); }

        private static void HealthBar(Slider? slider, Image? image, bool active, float health)
        {
            if (slider == null) return;
            Active(slider.gameObject, active);
            if (!active) return;
            slider.value = health;
            if (image != null) image.color = Red(health);
        }

        // As Event_DerbyDemolition_Vehicle.TakeDamage: steam below 70% health, thicker as it drops.
        private static void Steam(ParticleSystem? steam, bool playing, float health)
        {
            if (steam == null) return;
            if (!playing) { if (steam.isPlaying) steam.Stop(); return; }
            if (!steam.isPlaying) steam.Play();
            float rate = Mathf.Max(0f, 0.7f - health) * 28.5f;
            var emission = steam.emission; emission.rateOverTime = rate; emission.rateOverDistance = rate;
        }

        // ---------------------------------------------------------------- puppets

        private void Show(Puppet puppet, bool active)
        {
            var vehicle = puppet.Vehicle;
            if (active && !puppet.Presenting)
            {
                puppet.Presenting = true; puppet.Trail.Clear(); puppet.LampsKnown = false;
                if (!puppet.Root.activeSelf) puppet.Root.SetActive(true);
                if (vehicle != null)
                {
                    puppet.OriginalMode = vehicle.VehicleMultiplayerInstanceType; puppet.OriginalAuto = vehicle.input.autoSetInput;
                    vehicle.SetMultiplayerInstanceType(Vehicle.MultiplayerInstanceType.Remote, true);
                    vehicle.input.autoSetInput = false;
                    vehicle.freezeWhileIdle = false; vehicle.freezeWhileAsleep = false;
                    if (!vehicle.enabled) vehicle.enabled = true;
                    byVehicle[vehicle] = puppet;
                    foreach (var wheel in vehicle.Wheels) if (wheel?.wheelController != null) byWheel[wheel.wheelController] = puppet;
                }
            }
            else if (!active && puppet.Presenting)
            {
                puppet.Presenting = false;
                if (vehicle != null)
                {
                    byVehicle.Remove(vehicle);
                    foreach (var wheel in vehicle.Wheels) if (wheel?.wheelController != null) byWheel.Remove(wheel.wheelController);
                    if (vehicle.VehicleMultiplayerInstanceType != puppet.OriginalMode) vehicle.SetMultiplayerInstanceType(puppet.OriginalMode, true);
                    vehicle.input.autoSetInput = puppet.OriginalAuto;
                    if (vehicle.vehicleRigidbody != null) vehicle.vehicleRigidbody.isKinematic = true;
                    vehicle.enabled = false;
                }
                if (puppet.Root.activeSelf) puppet.Root.SetActive(false);
            }
        }

        private static void Feed(Puppet puppet, Vector3 position, Quaternion rotation, float rpm, float spin, float throttle, bool engineOn, ushort? lamps)
        {
            if (!puppet.Presenting) return;
            puppet.Trail.Add(position, rotation);
            puppet.Rpm = rpm; puppet.Spin = spin; puppet.Throttle = throttle; puppet.EngineOn = engineOn;
            if (lamps.HasValue && puppet.Vehicle != null && (!puppet.LampsKnown || puppet.Lamps != lamps.Value))
            {
                puppet.Lamps = lamps.Value; puppet.LampsKnown = true;
                try { NativeVehicleLighting.Apply(puppet.Vehicle, new VehicleLightingState((ushort)(lamps.Value & 0x7FFF), 0, 0, 0, (byte)Mathf.Clamp(puppet.Vehicle.InteriorLightMode, 0, 2))); }
                catch (Exception) { }
            }
        }

        private static void Pose(Puppet puppet)
        {
            if (!puppet.Presenting || puppet.Trail.Count == 0 || puppet.Vehicle == null) return;
            var played = puppet.Trail.Playback();
            var vehicle = puppet.Vehicle;
            vehicle.transform.SetPositionAndRotation(played.Position, played.Rotation);
            var body = vehicle.vehicleRigidbody;
            if (body != null && body.isKinematic) { body.position = played.Position; body.rotation = played.Rotation; }
        }

        // WheelController visual update on a watcher: the holder's wheel spin.
        internal bool TryGetRemoteWheelSpin(WheelController wheel, out float angularVelocity)
        {
            angularVelocity = 0f;
            if (disposed || wheel == null || !wheel.visualOnlyUpdate || !byWheel.TryGetValue(wheel, out var puppet) || !puppet.Presenting) return false;
            angularVelocity = puppet.Spin;
            return true;
        }

        // NWH2_RES on a watcher: the holder's engine.
        internal bool RemoteEngine(VehicleController vehicle, out float rpm, out float speed, out bool gas)
        {
            rpm = 0f; speed = 0f; gas = false;
            if (disposed || vehicle == null || !byVehicle.TryGetValue(vehicle, out var puppet) || !puppet.Presenting) return false;
            float now = Time.time, dt = now - puppet.EngineAt;
            var position = vehicle.transform.position;
            if (puppet.EngineAt <= 0f || dt > 1f) puppet.EngineSpeed = 0f;
            else if (dt > 0.0001f) puppet.EngineSpeed = Mathf.Lerp(puppet.EngineSpeed, Mathf.Min((position - puppet.EnginePosition).magnitude / dt * 3.6f, 300f), Mathf.Clamp01(dt * 4f));
            puppet.EnginePosition = position; puppet.EngineAt = now;
            if (!puppet.EngineOn) return true;
            rpm = Mathf.Max(puppet.Rpm, 700f); speed = puppet.EngineSpeed; gas = puppet.Throttle > 0.05f || puppet.Rpm > 2500f;
            return true;
        }

        // ---------------------------------------------------------------- watchers: per frame

        private void Watch()
        {
            foreach (var puppet in champPuppets) Pose(puppet);
            foreach (var puppet in derbyPuppets) Pose(puppet);
            if (champ.Presenting && (champRacing || champStage > 0) && Watching(SpeedwayVenue.Championship) && championship != null && !Flag(ChRunning, championship))
            {
                var ui = Get<UI_Event_Championship>(ChUi, championship);
                if (ui != null)
                {
                    var best = new float[3]; best[TypeIndex(champStage)] = champBestLap;
                    ui.Update_Championship(champRacing, champStage, best, champLap, champLapTime, champLastLap);
                    champHud = true;
                }
            }
            else CloseChampionshipHud();
            if (derbyVenue.Presenting && derbyUi && Watching(SpeedwayVenue.Derby) && derby != null && !Flag(DeRunning, derby))
            {
                var ui = Get<UI_Event_DerbyDemolition>(DeUi, derby);
                if (ui != null)
                {
                    if (!derbyHud) ui.Derby_Enable();
                    derbyHud = true;
                    ui.Derby_Update(derbyRound, derbyRounds);
                    HealthBar(ui.Get_Slider_HealthBar, ui.Get_Image_HealthBar, true, derbyHealth / 255f);
                }
            }
            else CloseDerbyHud();
        }

        // ---------------------------------------------------------------- one-shot cues

        // Which of this player's events a native race sound belongs to (null: none running here).
        private SpeedwayVenue? ActiveVenue()
        {
            if (Mine(trial)) return SpeedwayVenue.Trial;
            if (Mine(derbyVenue) && (Flag(DeRunning, derby) || DerbyRaised() > 0)) return SpeedwayVenue.Derby;
            if (Mine(champ) && ChampionshipShowing) return SpeedwayVenue.Championship;
            if (MonMPPlugin.Instance?.World?.MeetRace?.Holding == true) return SpeedwayVenue.Meet;
            return null;
        }

        // The championship runs here, or just ended: its result message comes after it stops (#287).
        private bool ChampionshipShowing => Flag(ChRunning, championship) || Time.unscaledTime - champLastRunningAt < 10f;

        // SOUND_Manager race sounds this player's native event played: everyone watching hears them.
        internal void LocalCue(Cue cue)
        {
            if (!Attached || applying || Relaying || SharedPresentationEvents.Replaying) return;
            var venue = ActiveVenue();
            if (venue == null) return;
            var camera = Camera.main;
            MonMPPlugin.Instance?.World?.Events?.Post(PresentationKind.SpeedwayCue, (byte)cue, (ulong)venue.Value, camera != null ? camera.transform.position : LocalFocus());
        }

        private uint HolderOf(SpeedwayVenue venue) => venue == SpeedwayVenue.Meet
            ? MonMPPlugin.Instance?.World?.MeetRace?.Racer ?? Nobody
            : Venue(venue)?.Holder ?? Nobody;

        internal void PlayCue(PresentationEvent e)
        {
            if (e.Target > (ulong)SpeedwayVenue.Meet) return;
            var venue = (SpeedwayVenue)e.Target;
            if (HolderOf(venue) != e.Actor || !Watching(venue)) return;

#if MONMP_DEVELOPMENT
            TestCues++;
#endif

            Relaying = true;
            try
            {
                using (SpatialSounds.Local())
                {
                    var sounds = Singleton<SOUND_Manager>.i;
                    switch ((Cue)e.Variant)
                    {
                        case Cue.Countdown: sounds.PlaySound_RaceCountdown(); break;
                        case Cue.Start: sounds.PlaySound_RaceStart(); break;
                        case Cue.Checkpoint: sounds.PlaySound_Race_Checkpoint(); break;
                        case Cue.Finish: sounds.PlaySound_Race_Finish(); break;
                        case Cue.Whistle: sounds.PlaySound_Race_Whistle(); break;
                        case Cue.CarDead: sounds.PlaySound_Race_CarDead(); break;
                        case Cue.Champion: sounds.PlaySound_BuggyBuildCompleted(); break;
                    }
                }
            }
            finally { Relaying = false; }
        }

        // ---------------------------------------------------------------- derby dents and repairs

        private int DerbySlot(DamageHandler handler)
        {
            for (int i = 0; i < derbyPuppets.Length; i++) if (derbyPuppets[i].Vehicle?.damageHandler == handler) return i;
            var missile = Get<Event_DerbyDemolition_Vehicle>(DePlayerCar, derby);
            return missile != null && missile.GetComponent<VehicleController>()?.damageHandler == handler ? MissileSlot : -1;
        }

        private VehicleController? SlotVehicle(int slot)
        {
            if (slot >= 0 && slot < derbyPuppets.Length) return derbyPuppets[slot].Vehicle;
            return slot == MissileSlot ? Get<Event_DerbyDemolition_Vehicle>(DePlayerCar, derby)?.GetComponent<VehicleController>() : null;
        }

        // DamageHandler.Enqueue on the derby holder: the dent every watcher's copy takes too.
        internal void LocalDent(DamageHandler handler, Collision collision, float magnitude)
        {
            if (!Attached || !Mine(derbyVenue) || !Flag(DeRunning, derby) || collision == null) return;
            int slot = DerbySlot(handler);
            var vehicle = SlotVehicle(slot);
            if (slot < 0 || vehicle == null) return;
            int count = Math.Min(2, collision.contactCount);
            for (int i = 0; i < count; i++)
            {
                var contact = collision.GetContact(i);
                var local = vehicle.transform.InverseTransformPoint(contact.point);
                var normal = vehicle.transform.InverseTransformDirection(contact.normal).normalized;
                ulong packed = (ulong)(ushort)Centimetres(local.x) | (ulong)(ushort)Centimetres(local.y) << 16 | (ulong)(ushort)Centimetres(local.z) << 32 | (ulong)Octahedral(normal) << 48;
                MonMPPlugin.Instance?.World?.Events?.Post(PresentationKind.SpeedwayDent, (byte)(slot | (i == 0 ? 0x80 : 0)), packed, contact.point, magnitude);
            }
        }

        // DamageHandler.Repair on the derby holder.
        internal void LocalRepair(DamageHandler handler)
        {
            if (!Attached || applying || SharedPresentationEvents.Replaying || !Mine(derbyVenue)) return;
            int slot = DerbySlot(handler);
            var vehicle = SlotVehicle(slot);
            if (slot < 0 || vehicle == null) return;
            MonMPPlugin.Instance?.World?.Events?.Post(PresentationKind.SpeedwayRepair, (byte)slot, 0, vehicle.transform.position);
        }

        private static ushort Octahedral(Vector3 n)
        {
            float sum = Mathf.Abs(n.x) + Mathf.Abs(n.y) + Mathf.Abs(n.z);
            if (sum < 0.0001f) return 0x8080;
            float x = n.x / sum, y = n.y / sum;
            if (n.z < 0f) { float ox = (1f - Mathf.Abs(y)) * Mathf.Sign(x), oy = (1f - Mathf.Abs(x)) * Mathf.Sign(y); x = ox; y = oy; }
            return (ushort)(Mathf.Clamp(Mathf.RoundToInt((x * 0.5f + 0.5f) * 255f), 0, 255) | Mathf.Clamp(Mathf.RoundToInt((y * 0.5f + 0.5f) * 255f), 0, 255) << 8);
        }

        private static Vector3 FromOctahedral(ushort packed)
        {
            float x = (packed & 0xFF) / 255f * 2f - 1f, y = (packed >> 8) / 255f * 2f - 1f;
            float z = 1f - Mathf.Abs(x) - Mathf.Abs(y);
            if (z < 0f) { float ox = (1f - Mathf.Abs(y)) * Mathf.Sign(x), oy = (1f - Mathf.Abs(x)) * Mathf.Sign(y); x = ox; y = oy; }
            return new Vector3(x, y, z).normalized;
        }

        // A watcher's copy takes the holder's dent, as DamageHandler.MeshDeform shapes it, and an AI
        // car's crash is heard (Le Missile's own crash is relayed with the vehicle).
        internal void PlayDent(PresentationEvent e)
        {
            if (e.Actor != derbyVenue.Holder || !Other(derbyVenue)) return;
            int slot = e.Variant & 0x7F;
            var vehicle = SlotVehicle(slot);
            var handler = vehicle?.damageHandler;
            if (vehicle == null || handler == null || !(DamageMeshes?.GetValue(handler) is List<MeshFilter> meshes)) return;

#if MONMP_DEVELOPMENT
            TestDents++;
#endif

            var local = new Vector3((short)(e.Target & 0xFFFF) / 100f, (short)((e.Target >> 16) & 0xFFFF) / 100f, (short)((e.Target >> 32) & 0xFFFF) / 100f);
            var normalLocal = FromOctahedral((ushort)(e.Target >> 48));
            if (Vector3.Dot(normalLocal, local) > 0f) normalLocal = -normalLocal;
            var point = vehicle.transform.TransformPoint(local);
            var normal = vehicle.transform.TransformDirection(normalLocal);
            float radius = Mathf.Clamp(e.Value * handler.deformationStrength / 2000f, 0f, handler.deformationRadius);
            if (radius > 0f)
            {
                foreach (var filter in meshes)
                {
                    if (filter == null || handler.deformationIgnoreTags.Contains(filter.gameObject.tag)) continue;
                    var renderer = filter.GetComponent<Renderer>();
                    if (renderer != null) { var bounds = renderer.bounds; bounds.Expand(radius * 2f); if (!bounds.Contains(point)) continue; }
                    var mesh = filter.mesh;
                    var vertices = mesh.vertices;
                    bool moved = false;
                    for (int j = 0; j < vertices.Length; j++)
                    {
                        var world = filter.transform.TransformPoint(vertices[j]);
                        float distance = Vector3.Distance(point, world) * UnityEngine.Random.Range(1f - handler.deformationRandomness, 1f + handler.deformationRandomness);
                        if (distance >= radius) continue;
                        vertices[j] = filter.transform.InverseTransformPoint(world + normal * (radius - distance));
                        moved = true;
                    }
                    if (!moved) continue;
                    mesh.vertices = vertices; mesh.RecalculateNormals(); mesh.RecalculateTangents();
                }
            }
            if ((e.Variant & 0x80) != 0 && slot != MissileSlot && Watching(SpeedwayVenue.Derby))
                RemoteVehicleSounds.Crash(vehicle, point, e.Value / 100f);
        }

        internal void PlayRepair(PresentationEvent e)
        {
            if (e.Actor != derbyVenue.Holder || !Other(derbyVenue)) return;
            applying = true;
            try { SlotVehicle(e.Variant)?.damageHandler?.Repair(); }
            finally { applying = false; }
        }

        // ---------------------------------------------------------------- event messages

        private static byte TitleCode(string? title)
        {
            if (string.IsNullOrEmpty(title)) return 0;
            for (byte i = 1; i < TitleKeys.Length; i++) if (TEXT.GetText(TitleKeys[i]) == title) return i;
            return 255;
        }

        private static bool IsLocalOnlyEntryNotice(byte title, string? text) =>
            SpeedwayMessagePolicy.IsLocalOnlyEntryNotice(title, text,
                TEXT.GetText("SPEEDWAY_NotDrivingAVehicle"),
                TEXT.GetText("SPEEDWAY_DoNotMoveToStartCountdown"),
                TEXT.GetText("RACEMANAGER_DoNotMoveToStartTheRace"),
                TEXT.GetText("SPEEDWAY_DerbyDemolition_DoNotMoveToStartTheDerby"),
                TEXT.GetText("SPEEDWAY_DerbyDemolition_ThatAintLeMissile"),
                TEXT.GetText("SPEEDWAY_DerbyDemolition_FacingTheWrongWay"));

        // UI_EventMessage.Show / Hide on the holder: its event's messages, for watchers.
        internal void LocalMessage(string? text, string? title, float duration, UI_EventMessage.EventIconType icon, bool hide)
        {
            if (!Attached) return;
            if (shownMessage && !Relaying) shownMessage = false;
            if (Relaying || applying || RemoteActionScope.Active) return;
            byte code = hide ? (byte)0 : TitleCode(title);
            if (hide)
            {
                if (localEntryNoticeActive) { localEntryNoticeActive = false; return; }
            }
            else
            {
                localEntryNoticeActive = IsLocalOnlyEntryNotice(code, text);
                if (localEntryNoticeActive) return;
            }
            SpeedwayVenue venue;
            if (hide)
            {
                if (lastSentMessage == null || lastSentMessage.Value.Hide) return;
                var active = ActiveVenue();
                if (active == null || active == SpeedwayVenue.Meet) return;
                venue = active.Value;
            }
            else if (code >= 1 && code <= 3 && Mine(trial)) venue = SpeedwayVenue.Trial;
            else if ((code == 4 || code == 0) && Mine(champ) && (ChampionshipShowing || code == 4)) venue = SpeedwayVenue.Championship;
            else if (code == 5 && Mine(derbyVenue)) venue = SpeedwayVenue.Derby;
            else return;
            string body = hide ? "" : text ?? "";
            if (lastSentMessage is var last && last.HasValue && last.Value.Text == body && last.Value.Title == code && last.Value.Hide == hide) return;
            lastSentMessage = (body, code, hide);
            var bytes = Encoding.UTF8.GetBytes(body);
            if (bytes.Length > MaxMessageBytes)
            {
                // Cut before the character the limit falls in.
                int cut = MaxMessageBytes;
                while (cut > 0 && (bytes[cut] & 0xC0) == 0x80) cut--;
                bytes = bytes.Take(cut).ToArray();
            }
            var packed = new byte[5 + bytes.Length];
            packed[0] = (byte)venue; packed[1] = (byte)icon; packed[2] = (byte)Mathf.Clamp(Mathf.RoundToInt(duration * 4f), 0, 255); packed[3] = code; packed[4] = (byte)(hide ? 1 : 0);
            bytes.CopyTo(packed, 5);
            pendingMessage = packed;
            FlushMessage();
        }

        private void FlushMessage()
        {
            if (pendingMessage == null || Time.unscaledTime - lastMessageAt < 0.08f) return;
            var packed = pendingMessage; pendingMessage = null; lastMessageAt = Time.unscaledTime;
            if (host) { PublishMessage(Self, packed); return; }
            sentMessageSeq++;
            int parts = Math.Max(1, (packed.Length + 119) / 120);
            for (int part = 0; part < parts; part++)
            {
                int length = Math.Min(120, packed.Length - part * 120);
                var command = new byte[5 + length];
                command[0] = MessagePart; command[1] = (byte)(sentMessageSeq & 0xFF); command[2] = (byte)(sentMessageSeq >> 8); command[3] = (byte)part; command[4] = (byte)parts;
                Array.Copy(packed, part * 120, command, 5, length);
                channel!.Request(MessageEntity, command, useLease: true);
            }
        }

        private void MessageCommand(uint peer, byte[] payload)
        {
            if (payload.Length < 5) return;
            ushort seq = (ushort)(payload[1] | payload[2] << 8);
            int part = payload[3], parts = payload[4];
            if (parts < 1 || parts > 3 || part >= parts) return;
            if (!messageParts.TryGetValue(peer, out var pending) || pending.Seq != seq || pending.Parts.Length != parts) pending = (seq, new byte[]?[parts]);
            var chunk = new byte[payload.Length - 5];
            Array.Copy(payload, 5, chunk, 0, chunk.Length);
            pending.Parts[part] = chunk;
            messageParts[peer] = pending;
            if (pending.Parts.Any(p => p == null)) return;
            messageParts.Remove(peer);
            PublishMessage(peer, pending.Parts.SelectMany(p => p!).ToArray());
        }

        private void PublishMessage(uint peer, byte[] packed)
        {
            if (packed.Length < 5 || packed[0] > (byte)SpeedwayVenue.Derby) return;
            var v = Venue((SpeedwayVenue)packed[0]);
            if (v == null || v.Holder != peer) return;
            string text;
            try { text = Encoding.UTF8.GetString(packed, 5, packed.Length - 5); } catch (ArgumentException) { return; }
            if (IsLocalOnlyEntryNotice(packed[3], text)) return;
            channel!.Publish(MessageEntity, EncodeMessage(packed[0], peer, ++messageSeq, packed[1], packed[2], packed[3], packed[4] != 0, text));
        }

        private void ReceiveMessage(byte[] payload)
        {
            if (payload.Length < 15 || payload[0] != 1) return;
            uint seq = BitConverter.ToUInt32(payload, 6);
            if (seq == presentedMessageSeq) return;
            presentedMessageSeq = seq;
            uint holder = BitConverter.ToUInt32(payload, 2);
            if (payload[1] > (byte)SpeedwayVenue.Derby) return;
            var venue = (SpeedwayVenue)payload[1];
            if (Venue(venue)?.Holder != holder) return;
            bool hide = payload[13] != 0;
            string text = "";
            byte title = payload[12];
            if (!hide)
            {
                try { text = Encoding.UTF8.GetString(payload, 15, payload.Length - 15); } catch (ArgumentException) { return; }
                if (IsLocalOnlyEntryNotice(title, text))
                {
                    suppressedRemoteEntryNotice = (holder, venue);
                    return;
                }
                suppressedRemoteEntryNotice = null;
            }
            else
            {
                bool hidesPrivateNotice = suppressedRemoteEntryNotice is var prior && prior.HasValue &&
                    prior.Value.Holder == holder && prior.Value.Venue == venue;
                suppressedRemoteEntryNotice = null;
                if (holder == Self || hidesPrivateNotice) return;
                if (shownMessage) { shownMessage = false; Singleton<UI_EventMessage>.i.Hide(); }
                return;
            }
            if (holder == Self || !Watching(venue)) return;
            var ui = Singleton<UI_EventMessage>.i;
            Relaying = true;
            try
            {
                ui.Show(text, title > 0 && title < TitleKeys.Length ? TEXT.GetText(TitleKeys[title]) : "", Mathf.Max(0.5f, payload[11] / 4f),
                    Enum.IsDefined(typeof(UI_EventMessage.EventIconType), (int)payload[10]) ? (UI_EventMessage.EventIconType)payload[10] : UI_EventMessage.EventIconType.Info);
                shownMessage = true;
            }
            finally { Relaying = false; }
        }

        // ---------------------------------------------------------------- guest wins

        private static GameObject? discardTemplate;
        private (GameObject? Derby, GameObject? Championship, GameObject? Grand)? swappedTrophies;
        private int winsBefore;

        // A guest's native win would spawn a trophy only it has: the host spawns the shared one.
        internal void BeforeWin(SpeedwayVenue venue)
        {
            if (!Attached || host || speedway == null) return;
            var assets = Singleton<GameAssets>.i;
            if (discardTemplate == null)
            {
                discardTemplate = new GameObject("MonMP discarded trophy");
                UnityEngine.Object.DontDestroyOnLoad(discardTemplate);
                discardTemplate.AddComponent<DiscardedClone>();
                DiscardedClone.Template = discardTemplate;
            }
            swappedTrophies = (assets.Trophy_DerbyEvent, assets.Trophy_EventChampionship, assets.Trophy_EventChampionship_GrandChampion);
            assets.Trophy_DerbyEvent = discardTemplate; assets.Trophy_EventChampionship = discardTemplate; assets.Trophy_EventChampionship_GrandChampion = discardTemplate;
            winsBefore = venue == SpeedwayVenue.Derby ? Int(SwDerbyWon, speedway) : Int(SwChampWon, speedway);
        }

        internal void AfterWin(SpeedwayVenue venue)
        {
            if (swappedTrophies is var swapped && swapped.HasValue)
            {
                var assets = Singleton<GameAssets>.i;
                assets.Trophy_DerbyEvent = swapped.Value.Derby!; assets.Trophy_EventChampionship = swapped.Value.Championship!; assets.Trophy_EventChampionship_GrandChampion = swapped.Value.Grand!;
                swappedTrophies = null;
            }
            if (!Attached || host || speedway == null) return;
            int wins = venue == SpeedwayVenue.Derby ? Int(SwDerbyWon, speedway) : Int(SwChampWon, speedway);
            var v = Venue(venue);
            if (wins > winsBefore && v != null && Mine(v))
            {
                Debug.Log("MonMP speedway " + venue + " won by this guest: the host grants the rewards");
                channel!.Request(v.Entity, new[] { Reward, (byte)venue }, useLease: true);
            }
        }

        // ---------------------------------------------------------------- best times and counters

        private float[][] Boards() => new[]
        {
            speedway!.Get_Player_Best_Drag ?? Array.Empty<float>(), speedway.Get_Player_Best_Oval ?? Array.Empty<float>(), speedway.Get_Player_Best_Track ?? Array.Empty<float>(),
            speedway.Get_Player_Best_Championship ?? Array.Empty<float>(), speedway.Get_Player_Best_Championship_Lap ?? Array.Empty<float>()
        };

        private void PublishRecords()
        {
            nextRecords = Time.unscaledTime + 2f;
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write((byte)1);
            foreach (var board in Boards()) { w.Write((byte)Math.Min(64, board.Length)); for (int i = 0; i < board.Length && i < 64; i++) w.Write(board[i]); }
            w.Write(Int(SwDerbyWon, speedway)); w.Write(Int(SwChampWon, speedway));
            var bytes = stream.ToArray();
            if (bytes.SequenceEqual(records)) return;
            records = bytes;
            channel!.Publish(RecordsEntity, bytes);
        }

        private void ApplyRecords(byte[] payload)
        {
            if (speedway == null || payload.Length < 2 || payload[0] != 1) return;
            try
            {
                using var r = new BinaryReader(new MemoryStream(payload, false)); r.ReadByte();
                var boards = Boards();
                var applied = new float[boards.Length][];
                for (int b = 0; b < boards.Length; b++)
                {
                    int count = r.ReadByte();
                    applied[b] = new float[count];
                    for (int i = 0; i < count; i++)
                    {
                        float value = r.ReadSingle();
                        if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f) value = 0f;
                        applied[b][i] = value;
                        if (i >= boards[b].Length) continue;
                        // A best time set here since the last boards and not sent yet stays: SendBoards
                        // offers it to the host next (#286).
                        float local = boards[b][i];
                        float known = appliedBoards != null && b < appliedBoards.Length && i < appliedBoards[b].Length ? appliedBoards[b][i] : local;
                        bool unsent = local > 0f && local != known && (known <= 0f || local < known) && (value <= 0f || local < value);
                        if (!unsent) boards[b][i] = value;
                    }
                }
                SwDerbyWon?.SetValue(speedway, r.ReadInt32()); SwChampWon?.SetValue(speedway, r.ReadInt32());
                appliedBoards = applied;
            }
            catch (Exception ex) when (ex is IOException || ex is EndOfStreamException) { return; }
            (SwBoard?.GetValue(speedway) as Speedway_BestTimes)?.UpdateVisual();
            (SwChampBoard?.GetValue(speedway) as Speedway_Championship_BestTimes)?.UpdateVisual();
            (SwRacecarToDo?.GetValue(speedway) as Speedway_RacecarToDo)?.UpdateVisual();
        }

        private float nextBoards;
        // A guest's new best time goes to the host's boards.
        private void SendBoards()
        {
            if (appliedBoards == null || Time.unscaledTime < nextBoards || !(Mine(trial) || Mine(champ) || trial.Holder == Nobody)) return;
            nextBoards = Time.unscaledTime + 1f;
            var boards = Boards();
            for (int b = 0; b < boards.Length && b < appliedBoards.Length; b++)
                for (int i = 0; i < boards[b].Length && i < appliedBoards[b].Length; i++)
                {
                    float local = boards[b][i], known = appliedBoards[b][i];
                    if (local <= 0f || (known > 0f && local >= known)) continue;
                    appliedBoards[b][i] = local;
                    var command = new byte[7];
                    command[0] = Best; command[1] = (byte)b; command[2] = (byte)i; BitConverter.GetBytes(local).CopyTo(command, 3);
                    channel!.Request(RecordsEntity, command, useLease: true);
                }
        }

        private void BestCommand(uint peer, byte board, byte index, float time)
        {
            if (speedway == null || peer == Self || float.IsNaN(time) || time < 1f || time > 3600f) return;
            // Only the boards of the event the peer holds or just held: its trial kind's or the championship's (#285).
            bool racer = recentRacers.TryGetValue((peer, board), out float at) && Time.unscaledTime - at < 60f;
            if (!racer) { Debug.Log("MonMP speedway best time " + board + "/" + index + " from peer " + peer + " refused (not its event)"); return; }
            var boards = Boards();
            if (board >= boards.Length || index >= boards[board].Length) return;
            float current = boards[board][index];
            if (current > 0f && time >= current) return;
            boards[board][index] = time;
            Debug.Log("MonMP speedway best time " + board + "/" + index + " = " + time + " from peer " + peer);
            (SwBoard?.GetValue(speedway) as Speedway_BestTimes)?.UpdateVisual();
            (SwChampBoard?.GetValue(speedway) as Speedway_Championship_BestTimes)?.UpdateVisual();
            (SwRacecarToDo?.GetValue(speedway) as Speedway_RacecarToDo)?.UpdateVisual();
            nextRecords = 0;
        }

        public void Dispose()
        {
            if (disposed) return;
            foreach (var v in venues) if (v.Presenting) Restore(v);
            foreach (var puppet in champPuppets.Concat(derbyPuppets)) Show(puppet, false);
            if (shownMessage) Singleton<UI_EventMessage>.i?.Hide();
            disposed = true;
            if (session != null) { session.PeerLeft -= OnLeft; session.PoseReceived -= OnPose; }
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; channel.ReplyReceived -= ReplyReceived; }
            session = null; channel = null;
        }
    }

    // A clone of the placeholder a guest's native win instantiates in place of the trophy: gone at once.
    internal sealed class DiscardedClone : MonoBehaviour
    {
        internal static GameObject? Template;
        private void Awake() { if (Template != null && gameObject != Template) Destroy(gameObject); }
    }
}
