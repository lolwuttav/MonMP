using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    internal sealed class SharedProgress : IDisposable
    {
        private readonly ToDoList_Manager manager;
        private readonly ToDoList_Line[] lines;
        private readonly List<ToDoList_Object> groups;
        private readonly bool host;
        private readonly ProgressState original;
        private readonly WorldRecord descriptor;
        private ProgressState current;
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool disposed, initialized;
        private float nextCapture;
        public bool BlocksLocalChanges => !host && session != null && !disposed;
        // Construction Turbo's paved roads (#124) are bought by phone and recorded as ToDo lines, but
        // the native purchase raised the road only on the buyer's own copy: a guest got a free local
        // road the host never saw, and a host's road never rose for guests. Guests now ask the host,
        // which charges the shared wallet and completes the line; every copy raises the roads its
        // shared ToDo state says are bought.
        private static readonly ToDoList_Line[] PavedLines =
            { ToDoList_Line.Advanced_PavedToHome, ToDoList_Line.Advanced_PavedFrancis, ToDoList_Line.Advanced_PavedHomeParking, ToDoList_Line.Advanced_PavedRoadToSugarshack };
        private static readonly string[] PavedFields = { "PavedRoads_ToHome", "PavedRoads_FrancisSide", "PavedRoads_HomeParking", "PavedRoads_ToSugarshack" };
        private const byte PavedRequestMarker = 0x50, PavedReplyMarker = 0x51;
        private bool applyingPaved;
        private readonly float[] pavedRequested = new float[4];
        internal bool AllowPavedRaise => host || applyingPaved || !BlocksLocalChanges;
        private static int PavedPrice(int index) => index switch
        {
            0 => GameManager.PRICE_Paved_ToHome, 1 => GameManager.PRICE_Paved_FrancisSide,
            2 => GameManager.PRICE_Paved_HomeParking, _ => GameManager.PRICE_Paved_ToSugarshack
        };
        // World unlocks outside the ToDo list that guests never learned: Roger's parts store (a static the
        // game sets when he is talked to after 5000$ at his van) and the racetrack's build day. A guest's
        // store stayed abandoned and its racetrack a building site (#172). The hydro line's build day
        // too: its poles go up in the host's daily store opening, which guests never run.
        public const ushort WorldFlagsAdapter = 101;
        public static readonly ulong WorldFlagsEntity = StaticId.Hash("shared-world-flags/v1");
        private static readonly System.Reflection.FieldInfo SpeedwayDayField = AccessTools.Field(typeof(Speedway), "DaySinceBuildStarted");
        private static readonly System.Reflection.MethodInfo SpeedwayUpdateMethod = AccessTools.Method(typeof(Speedway), "UpdateSpeedwayConstruction");
        private readonly byte[] originalFlags;
        private byte[] currentFlags;
        private readonly GarageFirewoodNotice garageFirewoodNotice = new GarageFirewoodNotice();
        private readonly bool originalRoger;
        private readonly int originalFirewood;
        private readonly Store_Roger? rogerStore = Object.FindObjectsOfType<Store_Roger>(true).FirstOrDefault(store => store.gameObject.scene.IsValid());

        private void RefreshRogerRewards()
        {
            if (rogerStore == null) return;
            // These only update the reviewed merchandise parents. OpenStore has other
            // lifecycle effects and listing activeSelf/sold-out state belongs to SharedStores.
            rogerStore.EnableRallyShocks();
            rogerStore.EnableDragTires();
        }

#if MONMP_DEVELOPMENT
        internal bool TestRogerOpen => GameManager.Get_RogerStoreIsOpen;
#endif

        private readonly Speedway? speedway = Object.FindObjectOfType<Speedway>();
        private static readonly System.Reflection.FieldInfo HydroDayField = AccessTools.Field(typeof(Store_HydroSaintClin), "DaySinceBuildStarted");
        private static readonly System.Reflection.FieldInfo HydroStagesField = AccessTools.Field(typeof(Store_HydroSaintClin), "ConstructionStages");
        private static readonly System.Reflection.FieldInfo HydroBuiltField = AccessTools.Field(typeof(Store_HydroSaintClin), "BuildCompleted");
        private static readonly System.Reflection.FieldInfo HydroBillField = AccessTools.Field(typeof(Store_HydroSaintClin), "BillToPay");
        private static readonly System.Reflection.FieldInfo HydroUsedField = AccessTools.Field(typeof(Store_HydroSaintClin), "currentComsuption");
        private readonly Store_HydroSaintClin? hydroStore = Object.FindObjectsOfType<Store_HydroSaintClin>(true).FirstOrDefault(s => s.gameObject.scene.IsValid());
        private byte[] CaptureFlags()
        {
            int day = speedway != null ? (int)SpeedwayDayField.GetValue(speedway) : -1;
            day = Mathf.Clamp(day, -1, short.MaxValue);
            int hydro = hydroStore != null ? Mathf.Clamp((int)HydroDayField.GetValue(hydroStore), -1, short.MaxValue) : -1;
            int wood = Mathf.Clamp(Singleton<HomeGarage>.i?.Get_FirewoodAmountAtBrotherHouse ?? 0, 0, ushort.MaxValue);
            // The hydro bill, in cents: the bill letter prints the local store's amount, and a guest's
            // never accrued, so it showed its own stale figure (#204).
            int bill = hydroStore != null ? Mathf.Clamp(Mathf.RoundToInt(hydroStore.Get_BillToPay * 100f), 0, int.MaxValue) : 0;
            // This period's consumption, in tenths: the hydro phone line reads the local store's, which
            // a guest never accrued, so it reported 0 (#186).
            int used = hydroStore != null ? Mathf.Clamp(Mathf.RoundToInt(hydroStore.Get_CurrentCompsuption * 10f), 0, int.MaxValue) : 0;
            return new[] { (byte)(GameManager.Get_RogerStoreIsOpen ? 1 : 0), (byte)(day & 0xFF), (byte)((day >> 8) & 0xFF),
                (byte)(hydro & 0xFF), (byte)((hydro >> 8) & 0xFF), (byte)(wood & 0xFF), (byte)((wood >> 8) & 0xFF),
                (byte)(bill & 0xFF), (byte)((bill >> 8) & 0xFF), (byte)((bill >> 16) & 0xFF), (byte)((bill >> 24) & 0xFF),
                (byte)(used & 0xFF), (byte)((used >> 8) & 0xFF), (byte)((used >> 16) & 0xFF), (byte)((used >> 24) & 0xFF) };
        }
        // The construction stages the native UpdateConstructionStage enables, without its completion side
        // effects (to-do line, generator power): those are the host's and arrive through their own records.
        private void ApplyHydroDay(int day)
        {
            if (hydroStore == null || day < 0 || (int)HydroDayField.GetValue(hydroStore) >= day) return;
            HydroDayField.SetValue(hydroStore, day);
            // Mark a finished build as done, or the next native stage update (a hydro load) completes it
            // again on the guest: the to-do popup and the generator's power-on.
            if (day >= 5) HydroBuiltField.SetValue(hydroStore, true);
            if (!(HydroStagesField.GetValue(hydroStore) is Store_HydroSaintClin.Hydro_ConstructionStage[] stages)) return;
            for (int i = 0; i < stages.Length && i + 1 <= day; i++)
                foreach (var go in stages[i]?.ObjectToEnableAtThisStage ?? Array.Empty<GameObject>())
                    if (go != null && !go.activeSelf) go.SetActive(true);
        }
        private void ApplyFlags(byte[] payload, bool snapshot = false)
        {
            if ((payload.Length != 7 && payload.Length != 11 && payload.Length != 15) || payload[0] > 1) throw new InvalidDataException("Invalid world flags payload.");
            currentFlags = payload;
            bool roger = payload[0] == 1;
            if (GameManager.Get_RogerStoreIsOpen != roger)
            {
                GameManager.Get_RogerStoreIsOpen = roger;
                // The native load refresh: abandoned or open storefront, by the new flag.
                rogerStore?.SaveSystemJustLoaded();
            }
            RefreshRogerRewards();
            int day = (short)(payload[1] | payload[2] << 8);
            if (speedway != null && day >= 0 && (int)SpeedwayDayField.GetValue(speedway) < day)
            {
                SpeedwayDayField.SetValue(speedway, day);
                SpeedwayUpdateMethod.Invoke(speedway, null);
            }
            ApplyHydroDay((short)(payload[3] | payload[4] << 8));
            // Firewood delivered to Francis for the garage: only the host's sales count it, and the
            // reception sign read the guest's stale local count.
            var garage = Singleton<HomeGarage>.i;
            if (garage != null)
            {
                int wood = payload[5] | payload[6] << 8;
                garage.Get_FirewoodAmountAtBrotherHouse = wood;
                if (!host && garageFirewoodNotice.Observe(wood, garage.Get_FirewoodMaxAmountToBuild, snapshot))
                    Singleton<UI_EventMessage>.i?.Show(TEXT.Actors_Francis_WellDoneBrotherGarageStarted,
                        "Francis", 6f, UI_EventMessage.EventIconType.Talk);
            }
            if (payload.Length >= 11 && hydroStore != null && !host)
                HydroBillField.SetValue(hydroStore, BitConverter.ToInt32(payload, 7) / 100f);
            if (payload.Length >= 15 && hydroStore != null && !host)
                HydroUsedField.SetValue(hydroStore, BitConverter.ToInt32(payload, 11) / 10f);
        }
        // The farm kiosk's biggest-fish paper reads a static record per fish type that only the catcher's
        // copy kept: a guest's paper never showed the host's catches, nor the host's the guest's (#235).
        // The host holds the shared record; a guest's bigger catch is reported to it.
        public const ushort FishRecordsAdapter = 131;
        public static readonly ulong FishRecordsEntity = StaticId.Hash("shared-fish-records/v1");
        private const byte FishRecordMarker = 0x54;
        private readonly float[] originalFishScale = (float[])(GameManager.Get_Fishing_FishScale?.Clone() ?? Array.Empty<float>());
        private float[] sharedFishScale = Array.Empty<float>();
        private float nextFishReport;
        private static byte[] EncodeFish(float[] scales)
        {
            var payload = new byte[1 + scales.Length * 4];
            payload[0] = (byte)scales.Length;
            for (int i = 0; i < scales.Length; i++) BitConverter.GetBytes(scales[i]).CopyTo(payload, 1 + i * 4);
            return payload;
        }
        private static float[] DecodeFish(byte[] payload)
        {
            if (payload.Length == 0 || payload.Length != 1 + payload[0] * 4) throw new InvalidDataException("Invalid fish records payload.");
            var scales = new float[payload[0]];
            for (int i = 0; i < scales.Length; i++)
            {
                scales[i] = BitConverter.ToSingle(payload, 1 + i * 4);
                if (float.IsNaN(scales[i]) || float.IsInfinity(scales[i]) || scales[i] < 0f || scales[i] > 100f) throw new InvalidDataException("Invalid fish record.");
            }
            return scales;
        }
        private void ApplyFish(byte[] payload)
        {
            var scales = DecodeFish(payload);
            var local = GameManager.Get_Fishing_FishScale;
            if (local == null || local.Length != scales.Length) return;
            ReportFish(local, scales);
            sharedFishScale = scales;
            if (local.SequenceEqual(scales)) return;
            GameManager.Get_Fishing_FishScale = (float[])scales.Clone();
            try { Object.FindObjectOfType<Store_FarmKiosque_FishRecord>()?.UpdateVisual(); }
            catch (Exception ex) { Debug.LogWarning("MonMP fish record refresh failed: " + ex.Message); }
        }
        // A guest's own catch beat the shared record: the host takes the bigger one.
        private void ReportFish(float[] local, float[] shared)
        {
            if (channel == null || local.Length != shared.Length) return;
            for (int i = 0; i < local.Length && i < byte.MaxValue; i++)
            {
                if (!(local[i] > shared[i] + 1e-4f) || local[i] > 100f) continue;
                var payload = new byte[6]; payload[0] = FishRecordMarker; payload[1] = (byte)i;
                BitConverter.GetBytes(local[i]).CopyTo(payload, 2);
                channel.Request(ProgressState.Entity, payload);
            }
        }
        [MonMP.Core.World.WorldCommandAdapters(ProgressState.Adapter)]
        private void FishCommand(uint peer, ulong entity, byte[] payload)
        {
            if (!host || disposed || entity != ProgressState.Entity || payload.Length != 6 || payload[0] != FishRecordMarker) return;
            var scales = GameManager.Get_Fishing_FishScale;
            float scale = BitConverter.ToSingle(payload, 2);
            if (scales == null || payload[1] >= scales.Length || float.IsNaN(scale) || scale <= scales[payload[1]] || scale > 100f) return;
            scales[payload[1]] = scale;
            try { Object.FindObjectOfType<Store_FarmKiosque_FishRecord>()?.UpdateVisual(); }
            catch (Exception ex) { Debug.LogWarning("MonMP fish record refresh failed: " + ex.Message); }
        }

        // Native Catch 1/50/250 Fish read the catcher's own GameManager.Fishing_Amount total, which
        // only the host's save keeps; guests restarted at zero every session, so a group never reached
        // 250. The host holds one shared count (saved with its world); guests report their catches and
        // everyone present earns each fish achievement once the shared total reaches it.
        public static readonly ulong FishCountsEntity = StaticId.Hash("shared-fish-counts/v1");
        private readonly int[] originalFishCounts = (int[])(GameManager.Get_Fishing_Amount?.Clone() ?? Array.Empty<int>());
        private int[] sharedFishCounts = Array.Empty<int>();
        private readonly SharedFishCounts.HostLedger fishLedger = new SharedFishCounts.HostLedger();
        private readonly SharedFishCounts.GuestTracker fishTracker = new SharedFishCounts.GuestTracker();
        private readonly HashSet<AchievementType> fishAwards = new HashSet<AchievementType>();
        private float nextFishCountReport;
        private static readonly AchievementType[] FishAchievements = { AchievementType.Fish_1, AchievementType.Fish_50, AchievementType.Fish_250 };
        private void ApplyFishCounts(byte[] payload)
        {
            var shared = SharedFishCounts.Decode(payload);
            var local = GameManager.Get_Fishing_Amount;
            if (local == null || local.Length != shared.Length) return;
            fishTracker.Receive(shared, local);
            sharedFishCounts = shared;
        }
        [MonMP.Core.World.WorldCommandAdapters(ProgressState.Adapter)]
        private void FishCountCommand(uint peer, ulong entity, byte[] payload)
        {
            if (!host || disposed || peer == 0 || entity != ProgressState.Entity || !SharedFishCounts.IsReport(payload)) return;
            var counts = GameManager.Get_Fishing_Amount;
            int[] caught;
            try { caught = SharedFishCounts.DecodeReport(payload); }
            catch (InvalidDataException) { return; }
            if (counts != null) fishLedger.Apply(peer, caught, counts);
        }
        private void FishPeerLeft(uint peer) { if (host) fishLedger.Remove(peer); }
        private void UpdateFishCounts()
        {
            var local = GameManager.Get_Fishing_Amount;
            if (local == null || local.Length == 0 || local.Length > SharedFishCounts.MaximumTypes) return;
            if (host)
            {
                if (local.Length == sharedFishCounts.Length && !local.SequenceEqual(sharedFishCounts))
                {
                    try { channel!.Publish(FishCountsEntity, SharedFishCounts.Encode(local)); sharedFishCounts = (int[])local.Clone(); }
                    catch (InvalidDataException ex) { Debug.LogWarning("MonMP fish count publish skipped: " + ex.Message); }
                }
            }
            else if (fishTracker.Observe(local) && channel != null && Time.unscaledTime >= nextFishCountReport)
            {
                nextFishCountReport = Time.unscaledTime + 1f;
                try { channel.Request(ProgressState.Entity, SharedFishCounts.EncodeReport(fishTracker.Caught)); fishTracker.Sent(); }
                catch (InvalidDataException ex) { Debug.LogWarning("MonMP fish catch report skipped: " + ex.Message); }
            }
            if ((!host && !fishTracker.Received) || GameManager.SETTING_Cheats) return;
            long total = SharedFishCounts.Total(local);
            for (int i = 0; i < FishAchievements.Length; i++)
                if (total >= SharedFishCounts.AwardThresholds[i] && fishAwards.Add(FishAchievements[i]))
                    GuestAchievements.Award(FishAchievements[i]);
        }

#if MONMP_DEVELOPMENT
        internal ProgressState TestState => current;


        internal bool TestRestored => Capture().Encode().SequenceEqual(original.Encode());
#endif

        internal bool Contains(ToDoList_LineDetails line) => manager.Get_Lines.Values.Contains(line);
        public SharedProgress(bool host, string diagnostics)
        {
            this.host = host; manager = UnityEngine.Object.FindObjectOfType<ToDoList_Manager>();
            if (manager == null || manager.Get_Lines.Count == 0) throw new InvalidOperationException("Wait until the ToDo list initializes.");
            lines = manager.Get_Lines.Keys.OrderBy(k => (int)k).ToArray();
            groups = (List<ToDoList_Object>)AccessTools.Field(typeof(ToDoList_Manager), "ToDoListsPerType").GetValue(manager);
            if (groups.Any(g => g == null) || lines.Any(k => !Enum.IsDefined(typeof(ToDoList_Line), k) || (int)manager.Get_Lines[k].toDoList_Type < 0 || (int)manager.Get_Lines[k].toDoList_Type >= groups.Count))
                throw new InvalidOperationException("Unsupported native ToDo layout.");
            string layout = string.Join("/", lines.Select(k => (int)k + ":" + k + ":" + (int)manager.Get_Lines[k].toDoList_Type + ":" + manager.Get_Lines[k].TextToShow)) + "/groups:" + groups.Count;
            descriptor = new WorldRecord(StaticId.Hash("todo-catalog/v1/" + layout), 1, 3, Array.Empty<byte>());
            original = current = Capture();
            originalFlags = currentFlags = CaptureFlags(); originalRoger = GameManager.Get_RogerStoreIsOpen;
            originalFirewood = Singleton<HomeGarage>.i?.Get_FirewoodAmountAtBrotherHouse ?? 0;

#if MONMP_DEVELOPMENT
            File.WriteAllText(Path.Combine(diagnostics, "shared-progress.txt"), "MONMP_PROGRESS_REGISTERED\nLines=" + lines.Length + "\nGroups=" + groups.Count + "\nCatalog=" + descriptor.Id.ToString("X16"));
#endif

        }
        private ProgressState Capture() => new ProgressState(lines.Select(k => manager.Get_Lines[k].value), groups.Select(g => g.gameObject.activeSelf));
        public IEnumerable<WorldRecord> InitialRecords()
        {
            yield return descriptor; yield return new WorldRecord(ProgressState.Entity, 1, ProgressState.Adapter, original.Encode());
            yield return new WorldRecord(WorldFlagsEntity, 1, WorldFlagsAdapter, originalFlags);
            if (originalFishScale.Length is > 0 and < 256)
            {
                sharedFishScale = (float[])originalFishScale.Clone();
                yield return new WorldRecord(FishRecordsEntity, 1, FishRecordsAdapter, EncodeFish(originalFishScale));
            }
            if (originalFishCounts.Length is > 0 and <= SharedFishCounts.MaximumTypes)
            {
                sharedFishCounts = (int[])originalFishCounts.Clone();
                yield return new WorldRecord(FishCountsEntity, 1, FishRecordsAdapter, SharedFishCounts.Encode(originalFishCounts));
            }
        }
        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel; channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
            channel.CommandRequested += PavedCommand; channel.CommandRejected += PavedCommand; channel.ReplyReceived += PavedReply;
            channel.CommandRequested += FishCommand; channel.CommandRequested += FishCountCommand; session.PeerLeft += FishPeerLeft;
            channel.CommandRequested += PhoneCommand; channel.CommandRejected += PhoneCommand; channel.ReplyReceived += PhoneReply;
        }

        // The phone's paid services check the caller's own wallet, which a guest never holds: the speedway
        // fuel delivery, hydro bill and police tickets all answered "not enough cash", and the hydro
        // start-construction letter shipped into the guest's private mailbox. Guests ask the host, which
        // runs the same checks against the shared wallet and world; the results replicate as usual.
        internal enum PhoneService : byte { SpeedwayFuel = 1, HydroBill = 2, PoliceTickets = 3, HydroStartLetter = 4 }
        private const byte PhoneRequestMarker = 0x52, PhoneReplyMarker = 0x53;
        private readonly float[] phoneRequested = new float[5];

#if MONMP_DEVELOPMENT
        internal int TestPhoneReplies { get; private set; }
#endif

        internal bool RequestPhoneService(PhoneService service)
        {
            int index = (int)service;
            if (!BlocksLocalChanges || channel == null || index < 1 || index >= phoneRequested.Length) return false;
            if (phoneRequested[index] > 0 && Time.unscaledTime - phoneRequested[index] < 3f) return true;
            phoneRequested[index] = Time.unscaledTime;
            channel.Request(ProgressState.Entity, new[] { PhoneRequestMarker, (byte)service });
            return true;
        }

        [MonMP.Core.World.WorldCommandAdapters(ProgressState.Adapter)]
        private void PhoneCommand(uint peer, ulong entity, byte[] payload)
        {
            if (!host || disposed || entity != ProgressState.Entity || payload.Length != 2 || payload[0] != PhoneRequestMarker) return;
            var service = (PhoneService)payload[1];
            byte status;
            try { status = RunPhoneService(service); }
            catch (Exception ex) { Debug.LogWarning("MonMP phone service " + service + " failed: " + ex.Message); status = 4; }
            Debug.Log("MonMP phone service " + service + " from peer " + peer + ": status " + status);
            if (peer != 0) channel!.Reply(peer, ProgressState.Entity, new[] { PhoneReplyMarker, (byte)service, status });
        }

        // 1 done, 2 nothing to do, 3 not enough cash, 4 unavailable.
        private byte RunPhoneService(PhoneService service)
        {
            var cash = Singleton<Cash_Manager>.i;
            if (cash == null) return 4;
            switch (service)
            {
                case PhoneService.SpeedwayFuel:
                {
                    var tank = Object.FindObjectOfType<Speedway>()?.Get_StorageFuelTank;
                    if (tank == null || !manager.isDone(ToDoList_Line.Speedway_Completed)) return 4;
                    float missing = tank.Get_FuelCapacity - tank.Get_FuelLevel;
                    if (missing <= 0f) return 2;
                    int price = (int)Mathf.Ceil(missing * 1.12f);
                    if (!cash.isCanAfford(price)) return 3;
                    cash.Event_Spent(price);
                    tank.Get_FuelLevel = tank.Get_FuelCapacity;
                    return 1;
                }
                case PhoneService.HydroBill:
                {
                    var hydro = Object.FindObjectOfType<Store_HydroSaintClin>();
                    var post = Object.FindObjectOfType<Store_PostOffice>();
                    if (hydro == null || post == null) return 4;
                    if (!hydro.Get_HydroCut || hydro.Get_BillToPay <= 0f) return 2;
                    if (!cash.isCanAfford(hydro.Get_BillToPay)) return 3;
                    cash.Event_Spent(hydro.Get_BillToPay);
                    post.DestroyAllLeterOfThisType(Envelope_Type.Hydro_Facture);
                    hydro.Pay_Bill();
                    return 1;
                }
                case PhoneService.PoliceTickets:
                {
                    var police = Object.FindObjectOfType<Store_PoliceInterceptor>();
                    if (police == null) return 4;
                    if (police.Get_BillToPay <= 0f) return 2;
                    if (!cash.isCanAfford(police.Get_BillToPay)) return 3;
                    cash.Event_Spent(police.Get_BillToPay);
                    police.Pay_Bill();
                    return 1;
                }
                case PhoneService.HydroStartLetter:
                {
                    var hydro = Object.FindObjectOfType<Store_HydroSaintClin>();
                    var post = Object.FindObjectOfType<Store_PostOffice>();
                    if (hydro == null || post == null) return 4;
                    if (hydro.Get_DaySinceBuildStarted != -1 || post.isThisLetterAlreadyWaiting(Envelope_Type.Hydro_StartConstruction)) return 2;
                    post.ShipThisLetterToPlayer(Envelope_Type.Hydro_StartConstruction);
                    return 1;
                }
                default: return 4;
            }
        }

        private void PhoneReply(ulong entity, byte[] payload)
        {
            if (host || entity != ProgressState.Entity || payload.Length != 3 || payload[0] != PhoneReplyMarker) return;

#if MONMP_DEVELOPMENT
            TestPhoneReplies++;
#endif

            var service = (PhoneService)payload[1]; byte status = payload[2];
            try
            {
                var message = Singleton<UI_EventMessage>.i;
                const UI_EventMessage.EventIconType Phone = UI_EventMessage.EventIconType.Phone;
                if (status == 3) { Singleton<UI_ErrorMessage>.i?.Show(TEXT.GetText("ERROR_NotEnoughCash")); return; }
                if (message == null || status == 4) return;
                switch (service)
                {
                    case PhoneService.SpeedwayFuel:
                        message.Show(TEXT.GetText(status == 1 ? "PHONE_Speedway_StorageFuelTank_ThankYouTheTankIsNowFilled" : "PHONE_Speedway_StorageFuelTank_AlreadyFull"),
                            TEXT.GetText("FuelDeliveryService_Name"), 6f, Phone);
                        break;
                    case PhoneService.HydroBill:
                        if (status == 1) message.Show(TEXT.GetText("PHONE_GRID_BillPayOverPhone"), TEXT.GetText("HydroStClin_Name"), 5f, Phone);
                        break;
                    case PhoneService.PoliceTickets:
                        message.Show(TEXT.GetText(status == 1 ? "PHONE_Police_TicketsPayOverPhone" : "PHONE_Police_NoTicketsToPay"),
                            TEXT.GetText("Police_Ticket_SenderName"), 5f, Phone);
                        break;
                    case PhoneService.HydroStartLetter:
                        if (status == 2) message.Show(TEXT.GetText("PHONE_GRID_AlreadySent"), TEXT.GetText("HydroStClin_Name"), 10f, Phone);
                        break;
                }
            }
            catch (Exception ex) { Debug.LogWarning("MonMP phone service reply failed: " + ex.Message); }
        }

        // Guest: the phone's purchase goes to the host instead of completing the line locally.
        internal bool RequestPaved(ToDoList_Line line)
        {
            int index = Array.IndexOf(PavedLines, line);
            if (!BlocksLocalChanges || index < 0 || channel == null) return false;
            // Both the ToDo call and the road raise route here (the one-line ToDo call may be inlined
            // past its patch), so one order is sent once.
            if (pavedRequested[index] > 0 && Time.unscaledTime - pavedRequested[index] < 3f) return true;
            pavedRequested[index] = Time.unscaledTime;
            channel.Request(ProgressState.Entity, new[] { PavedRequestMarker, (byte)index });
            return true;
        }

        // Host: validated against the shared ToDo state and wallet, so a stale or repeated request is harmless.
        [MonMP.Core.World.WorldCommandAdapters(ProgressState.Adapter)]
        private void PavedCommand(uint peer, ulong entity, byte[] payload)
        {
            if (!host || disposed || entity != ProgressState.Entity || payload.Length != 2 || payload[0] != PavedRequestMarker || payload[1] >= PavedLines.Length) return;
            int index = payload[1]; var line = PavedLines[index];
            var cash = Singleton<Cash_Manager>.i;
            byte status;
            if (manager.isDone(line)) status = 2;
            else if (cash == null || !cash.isCanAfford(PavedPrice(index))) status = 3;
            else
            {
                cash.Event_Spent(PavedPrice(index));
                manager.PavedRoadsBought(line);
                RaisePaved(index);
                current = Capture(); channel!.Publish(ProgressState.Entity, current.Encode());
                status = 1;
            }
            if (peer != 0) channel!.Reply(peer, ProgressState.Entity, new[] { PavedReplyMarker, (byte)index, status });
        }

        private void PavedReply(ulong entity, byte[] payload)
        {
            if (host || entity != ProgressState.Entity || payload.Length != 3 || payload[0] != PavedReplyMarker) return;
            if (payload[2] == 1)
            {
                // The phone's own confirmation only shows once the host has bought it.
                try { Singleton<UI_EventMessage>.i?.Show(TEXT.GetText("ConstructionTurbo_ServiceBought"), TEXT.GetText("ConstructionTurbo_Title"), 10f, UI_EventMessage.EventIconType.Phone); }
                catch (Exception ex) { Debug.LogWarning("MonMP paved road reply failed: " + ex.Message); }
                return;
            }
            string error = payload[2] == 2 ? "ERROR_PavedRoads_AlreadyBought" : "ERROR_NotEnoughCash";
            try { Singleton<UI_EventMessage>.i?.Hide(); Singleton<UI_ErrorMessage>.i?.Show(TEXT.GetText(error)); }
            catch (Exception ex) { Debug.LogWarning("MonMP paved road reply failed: " + ex.Message); }
        }

        private static GameObject[]? PavedRoadsOf(PavedRoads roads, int index) =>
            AccessTools.Field(typeof(PavedRoads), PavedFields[index])?.GetValue(roads) as GameObject[];

        private void RaisePaved(int index)
        {
            var roads = Singleton<Gameplay>.i?.home?.pavedRoads ?? Object.FindObjectOfType<PavedRoads>();
            if (roads == null) return;
            applyingPaved = true;
            try { roads.PavedRoadBought(PavedLines[index]); }
            finally { applyingPaved = false; }
        }

        // Guest: show exactly the roads the host's ToDo state has bought.
        private void SyncPaved(ProgressState state)
        {
            var roads = Singleton<Gameplay>.i?.home?.pavedRoads ?? Object.FindObjectOfType<PavedRoads>();
            if (roads == null) return;
            for (int i = 0; i < PavedLines.Length; i++)
            {
                int at = Array.IndexOf(lines, PavedLines[i]);
                var parts = PavedRoadsOf(roads, i);
                if (at < 0 || parts == null || parts.Length == 0 || parts[0] == null) continue;
                bool bought = state.Completed[at], shown = parts[0].activeSelf;
                if (bought && !shown) RaisePaved(i);
                else if (!bought && shown) foreach (var part in parts) if (part != null) part.SetActive(false);
            }
        }
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            if (records.TryGetValue(ProgressState.Entity, out var record)) Apply(record);
            if (records.TryGetValue(WorldFlagsEntity, out var flags) && flags.Adapter == WorldFlagsAdapter) ApplyFlags(flags.Payload, true);
            if (records.TryGetValue(FishRecordsEntity, out var fish) && fish.Adapter == FishRecordsAdapter) ApplyFish(fish.Payload);
            if (records.TryGetValue(FishCountsEntity, out var counts) && counts.Adapter == FishRecordsAdapter) ApplyFishCounts(counts.Payload);
        }
        private void Changed(WorldRecord record)
        {
            if (host) return;
            if (record.Adapter == ProgressState.Adapter) Apply(record);
            else if (record.Adapter == WorldFlagsAdapter && record.Id == WorldFlagsEntity) ApplyFlags(record.Payload);
            else if (record.Adapter == FishRecordsAdapter && record.Id == FishRecordsEntity) ApplyFish(record.Payload);
            else if (record.Adapter == FishRecordsAdapter && record.Id == FishCountsEntity) ApplyFishCounts(record.Payload);
        }
        private void Apply(WorldRecord record)
        {
            if (record.Id != ProgressState.Entity || record.Adapter != ProgressState.Adapter) throw new InvalidDataException("Unknown progression record.");
            var state = ProgressState.Decode(record.Payload);
            if (state.Completed.Count != lines.Length || state.VisibleGroups.Count != groups.Count) throw new InvalidDataException("Progression catalog mismatch.");
            // A line the host just completed plays the native checklist popup and sound on guests too.
            var completed = initialized ? lines.Where((line, i) => state.Completed[i] && !current.Completed[i]).ToList() : new List<ToDoList_Line>();
            bool newlyChecked = completed.Count > 0;
            // The host's native completion grants its milestone achievement; guests get it here.
            foreach (var line in completed) GuestAchievements.AwardToDo(line);
            current = state; ApplyNative(state); initialized = true;
            try { SyncPaved(state); } catch (Exception ex) { Debug.LogWarning("MonMP paved roads sync failed: " + ex.Message); }
            if (newlyChecked) Checked();
        }
        private void Checked()
        {
            try
            {
                Object.FindObjectOfType<UI_FrontLeftPopup>()?.ToDoChecked();
                using (SpatialSounds.Local()) Singleton<SOUND_Manager>.i?.PlaySound_ToDoListDone();
            }
            catch (Exception ex) { Debug.LogWarning("MonMP ToDo popup failed: " + ex.Message); }
        }
        private void ApplyNative(ProgressState state)
        {
            for (int i = 0; i < lines.Length; i++)
            { var line = manager.Get_Lines[lines[i]]; line.value = state.Completed[i]; line.UpdateStrike(); }
            for (int i = 0; i < groups.Count; i++) groups[i].gameObject.SetActive(state.VisibleGroups[i]);
            RefreshRogerRewards();
        }
        public void Update()
        {
            if (disposed || session?.IsRunning != true) return;
            if (Time.unscaledTime < nextCapture) return; nextCapture = Time.unscaledTime + 0.2f;
            try { UpdateFishCounts(); } catch (Exception ex) { Debug.LogWarning("MonMP fish count sync failed: " + ex.Message); }
            if (host)
            {
                current = Capture(); channel!.Publish(ProgressState.Entity, current.Encode());
                var flags = CaptureFlags();
                if (!flags.SequenceEqual(currentFlags)) { currentFlags = flags; channel.Publish(WorldFlagsEntity, flags); }
                var fish = GameManager.Get_Fishing_FishScale;
                if (fish != null && fish.Length == sharedFishScale.Length && !fish.SequenceEqual(sharedFishScale))
                { sharedFishScale = (float[])fish.Clone(); channel.Publish(FishRecordsEntity, EncodeFish(sharedFishScale)); }
            }
            else if (sharedFishScale.Length != 0 && Time.unscaledTime >= nextFishReport && GameManager.Get_Fishing_FishScale is { } local)
            {
                nextFishReport = Time.unscaledTime + 2f;
                ReportFish(local, sharedFishScale);
            }
            else if (initialized && (lines.Where((line, i) => manager.Get_Lines[line].value != current.Completed[i]).Any() || groups.Where((g, i) => g.gameObject.activeSelf != current.VisibleGroups[i]).Any())) ApplyNative(current);
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (channel != null)
            {
                channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed;
                channel.CommandRequested -= PavedCommand; channel.CommandRejected -= PavedCommand; channel.ReplyReceived -= PavedReply;
                channel.CommandRequested -= FishCommand; channel.CommandRequested -= FishCountCommand;
                channel.CommandRequested -= PhoneCommand; channel.CommandRejected -= PhoneCommand; channel.ReplyReceived -= PhoneReply;
            }
            if (!host && manager != null) ApplyNative(original);
            if (!host && GameManager.Get_RogerStoreIsOpen != originalRoger)
            {
                GameManager.Get_RogerStoreIsOpen = originalRoger;
                try { Object.FindObjectOfType<Store_Roger>()?.SaveSystemJustLoaded(); } catch (Exception ex) { Debug.LogWarning("MonMP Roger store restore failed: " + ex.Message); }
            }
            if (!host && Singleton<HomeGarage>.i is { } garage) garage.Get_FirewoodAmountAtBrotherHouse = originalFirewood;
            if (!host && originalFishScale.Length != 0) GameManager.Get_Fishing_FishScale = (float[])originalFishScale.Clone();
            if (!host && originalFishCounts.Length != 0) GameManager.Get_Fishing_Amount = (int[])originalFishCounts.Clone();
            if (session != null) session.PeerLeft -= FishPeerLeft;
            session = null; channel = null;
        }
    }
}
