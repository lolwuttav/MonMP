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
using UnityEngine.InputSystem;
using UnityEngine.Localization.Settings;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // In a multiplayer session the host owns the one portable console, its location,
    // battery, and partial charge. Guests may ask to move it between hand and charger;
    // the stock save remains the durable source when the session ends.
    internal sealed partial class SharedPortableConsoleCharger : IDisposable
    {
        private const float InteractionRange = 4.5f;
        private const float PoseMaxAge = 2f;
        private const float CaptureInterval = 0.25f;
        private const float BudgetWindow = 2f;
        private const int RequestsPerWindow = 6;
        private const int MaxReplayResults = 2048;

        private static readonly FieldInfo ConsoleObjectField = AccessTools.Field(typeof(Furniture_PortableGameConsole_Charger), "GameConsole");
        private static readonly FieldInfo ChargerInputActionField = AccessTools.Field(typeof(Furniture_PortableGameConsole_Charger), "inputAction");
        private static readonly MethodInfo PlaceMethod = AccessTools.Method(typeof(Furniture_PortableGameConsole_Charger), "PlaceConsoleOnCharger", Type.EmptyTypes);
        private static readonly MethodInfo RetrieveMethod = AccessTools.Method(typeof(Furniture_PortableGameConsole_Charger), "RetrieveConsole", Type.EmptyTypes);
        private static readonly MethodInfo UpdateIndicatorsMethod = AccessTools.Method(typeof(Furniture_PortableGameConsole_Charger), "Update_BatteryIndicators", Type.EmptyTypes);

        private readonly Gameplay gameplay;
        private readonly bool host;
        private readonly SharedItems items;
        private readonly PortableGameConsole console;
        private readonly PlayerVisualHands hands;
        private readonly Furniture_PortableGameConsole_Charger[] chargers;
        private readonly Dictionary<ulong, Furniture_PortableGameConsole_Charger> chargersById = new Dictionary<ulong, Furniture_PortableGameConsole_Charger>();
        private readonly Dictionary<Furniture_PortableGameConsole_Charger, ulong> idsByCharger = new Dictionary<Furniture_PortableGameConsole_Charger, ulong>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Dictionary<uint, (float Started, int Count)> requestBudgets = new Dictionary<uint, (float, int)>();
        private readonly Dictionary<Guid, (ulong Entity, float SentAt, PortableConsoleChargerAction Action, Furniture_PortableGameConsole_Charger Charger)> pending =
            new Dictionary<Guid, (ulong, float, PortableConsoleChargerAction, Furniture_PortableGameConsole_Charger)>();
        private readonly Dictionary<(uint Peer, Guid Operation), PortableConsoleChargerStatus> replayResults =
            new Dictionary<(uint, Guid), PortableConsoleChargerStatus>();
        private readonly Queue<(uint Peer, Guid Operation)> replayOrder = new Queue<(uint, Guid)>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private PortableConsoleChargerState state;
        private PortableConsoleChargerState? lastPublished;
        private readonly PortableConsoleChargerState original;
        private float nextCapture;
        private bool applying, snapshotReceived, disposed;

        internal static SharedPortableConsoleCharger? Active { get; private set; }
        internal bool NewActionsBlocked { get; set; }
        internal bool TransactionsSettled => pending.Count == 0 && !ConsoleSaveInProgress;

#if MONMP_DEVELOPMENT
        internal int TestAccepted { get; private set; }


        internal int TestRejected { get; private set; }


        internal PortableConsoleChargerState TestState => state;
#endif


        internal SharedPortableConsoleCharger(Gameplay gameplay, bool host, SharedItems items, NativeEntityRegistry identities)
        {
            this.gameplay = gameplay ?? throw new ArgumentNullException(nameof(gameplay));
            this.host = host;
            this.items = items ?? throw new ArgumentNullException(nameof(items));
            if (identities == null) throw new ArgumentNullException(nameof(identities));

            if (ConsoleObjectField?.FieldType != typeof(GameObject) || ChargerInputActionField?.FieldType != typeof(InputAction) ||
                PlaceMethod == null || RetrieveMethod == null || UpdateIndicatorsMethod == null)
                throw new InvalidOperationException("Unsupported native portable-console charger layout.");
            hands = gameplay.Player?.Get_VisualHands ?? throw new InvalidOperationException("The player portable-console hands are unavailable.");
            console = hands.GameConsole ?? throw new InvalidOperationException("The player portable console is unavailable.");
            chargers = Object.FindObjectsOfType<Furniture_PortableGameConsole_Charger>(true)
                .Where(value => value != null && value.gameObject.scene.IsValid()).ToArray();
            if (chargers.Length < 1 || chargers.Length > 2 || chargers.Any(value => !IsChargerLocation(value.ChargerLocation)) ||
                chargers.Select(value => value.ChargerLocation).Distinct().Count() != chargers.Length)
                throw new InvalidOperationException("Native portable-console chargers do not match the supported unique-location layout.");

            foreach (var charger in chargers)
            {
                if (ConsoleObjectField.GetValue(charger) is not GameObject consoleObject)
                    throw new InvalidOperationException("A portable-console charger is missing its presentation object.");
                string path = "portable-console-charger/v1/" + SharedBuildingDoors.StablePath(charger.transform);
                ulong id = StaticId.Hash(path);
                identities.Bind(id, PortableConsoleChargerState.Adapter, charger.gameObject, path, true, EntityPersistence.NativeSave);
                chargersById.Add(id, charger);
                idsByCharger.Add(charger, id);
                _ = consoleObject;
            }

            state = original = Capture();
            Active = this;
        }

        private static bool IsChargerLocation(ConsoleLocation location) =>
            location == ConsoleLocation.Charger_Home || location == ConsoleLocation.Charger_Speedway;

        private static PortableConsoleLocation ToPortableLocation(ConsoleLocation location) => location switch
        {
            ConsoleLocation.Hand => PortableConsoleLocation.Hand,
            ConsoleLocation.Charger_Home => PortableConsoleLocation.HomeCharger,
            ConsoleLocation.Charger_Speedway => PortableConsoleLocation.SpeedwayCharger,
            _ => throw new InvalidDataException("Unknown native portable-console location.")
        };

        private static ConsoleLocation ToNativeLocation(PortableConsoleLocation location) => location switch
        {
            PortableConsoleLocation.Hand => ConsoleLocation.Hand,
            PortableConsoleLocation.HomeCharger => ConsoleLocation.Charger_Home,
            PortableConsoleLocation.SpeedwayCharger => ConsoleLocation.Charger_Speedway,
            _ => throw new InvalidDataException("Unknown replicated portable-console location.")
        };

        private static PortableConsoleLocation ToPortableLocation(Furniture_PortableGameConsole_Charger charger) => ToPortableLocation(charger.ChargerLocation);

        private PortableConsoleChargerState Capture() => new PortableConsoleChargerState(
            ToPortableLocation(hands.Get_ConsoleLocation), console.Get_BatteryLife, console.Battery_Charging);

        internal IEnumerable<WorldRecord> InitialRecords()
        {
            byte[] payload = state.Encode();
            foreach (var id in chargersById.Keys.OrderBy(value => value))
                yield return new WorldRecord(id, 1, PortableConsoleChargerState.Adapter, payload);
        }

        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
            session.PoseReceived += PoseReceived;
            session.PeerLeft += PeerLeft;
            channel.SnapshotApplied += Snapshot;
            channel.Changed += Changed;
            channel.CommandRequested += Command;
            channel.CommandRejected += Rejected;
            channel.ReplyReceived += Reply;
        }

        private bool Running => !disposed && session?.IsRunning == true;
        private bool Ready => Running && channel?.FullySynchronized == true;
        private bool IsGuest => Running && !host;
        internal bool AllowNativeBatteryWrite() => !IsGuest || applying;
        internal bool AllowNativeLocationWrite() => !IsGuest || applying;
        internal bool AllowNativeChargeTick(Furniture_PortableGameConsole_Charger candidate) => !IsGuest || applying || !Running;

        // Returns whether vanilla MouseDown may run. Offline behavior is left to the game.
        internal bool Intercept(Furniture_PortableGameConsole_Charger candidate)
        {
            if (!Running || applying) return true;
            if (candidate == null || !idsByCharger.ContainsKey(candidate) || !candidate.isActiveAndEnabled)
            {
                Report("This portable-console charger is not registered in the shared world.");
                return false;
            }
            if (host)
            {
                if (consoleSaves.Active) { Report("Wait for the handheld save before moving the console."); return false; }
                return true;
            }
            if (!Ready || NewActionsBlocked || consoleSaves.Active || MonMP.Plugin.MonMPPlugin.Instance?.World?.SaveGate.Active == true)
            {
                Report("Wait for the shared world and save to finish before moving the portable console.");
                return false;
            }
            if (pending.Count >= 16)
            {
                Report("Wait for the pending portable-console request to finish.");
                return false;
            }

            var action = state.Location == ToPortableLocation(candidate) && candidate.isConsoleCharging
                ? PortableConsoleChargerAction.Retrieve
                : PortableConsoleChargerAction.Place;
            var request = new PortableConsoleChargerRequest(Guid.NewGuid(), action);
            pending.Add(request.Operation, (idsByCharger[candidate], Time.unscaledTime, action, candidate));
            try { channel!.Request(idsByCharger[candidate], request.Encode()); }
            catch
            {
                pending.Remove(request.Operation);
                throw;
            }
            return false;
        }

        internal void NativeMouseDownCompleted(Furniture_PortableGameConsole_Charger candidate)
        {
            if (!Running || !host || applying || candidate == null || !idsByCharger.ContainsKey(candidate)) return;
            CaptureAndPublish();
        }

        private void PoseReceived(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);

        private void PeerLeft(uint peer)
        {
            consoleSaves.End(peer, consoleSaves.Operation);
            presence.Remove(peer);
            requestBudgets.Remove(peer);
            foreach (var key in replayResults.Keys.Where(key => key.Peer == peer).ToArray()) replayResults.Remove(key);
        }

        private bool TryPosition(uint peer, out Vector3 position, out bool canInteract)
        {
            if (peer == 0)
            {
                position = gameplay.PlayerWalking.transform.position;
                canInteract = ItemInteractionPosePolicy.AllowsState((byte)gameplay.Get_PlayerState) &&
                    !gameplay.Get_IsDead && !gameplay.Get_IsSleeping && MonMPPlugin.Instance?.Spectating != true;
                return true;
            }
            if (!presence.TryGetValue(peer, out var observed))
            {
                position = default;
                canInteract = false;
                return false;
            }
            float age = Time.unscaledTime - observed.Time;
            position = new Vector3(observed.Pose.X, observed.Pose.Y, observed.Pose.Z);
            canInteract = age >= 0 && age <= PoseMaxAge && ItemInteractionPosePolicy.Allows(observed.Pose);
            return age >= 0 && age <= PoseMaxAge;
        }

        private bool TakeBudget(uint peer)
        {
            float now = Time.unscaledTime;
            if (!requestBudgets.TryGetValue(peer, out var budget) || now < budget.Started || now - budget.Started >= BudgetWindow)
                budget = (now, 0);
            if (budget.Count >= RequestsPerWindow) { requestBudgets[peer] = budget; return false; }
            requestBudgets[peer] = (budget.Started, budget.Count + 1);
            return true;
        }

        [MonMP.Core.World.WorldCommandAdapters(PortableConsoleChargerState.Adapter)]
        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (PortableConsoleSaveRequest.Is(payload)) { ConsoleSaveCommand(peer, entity, payload); return; }
            if (!host || session?.IsHost != true || !chargersById.TryGetValue(entity, out var charger) || charger == null ||
                !PortableConsoleChargerRequest.Is(payload)) return;
            if (charger is null) return;
            PortableConsoleChargerRequest request;
            try { request = PortableConsoleChargerRequest.Decode(payload); }
            catch (InvalidDataException) { return; }

            var key = (peer, request.Operation);
            if (replayResults.TryGetValue(key, out var prior)) { SendReply(peer, entity, request.Operation, prior); return; }
            bool peerKnown = peer == 0 || session.Peers.ContainsKey(peer);
            bool poseKnown = TryPosition(peer, out Vector3 position, out bool canInteract);
            bool inRange = poseKnown && Vector3.Distance(position, charger.transform.position) <= InteractionRange;
            bool handsFree = peer == 0 ? !gameplay.Player.IsHoldingAnObject : peerKnown && !items.PeerHolding(peer);
            bool active = charger.gameObject.scene.IsValid() && charger.gameObject.scene.isLoaded &&
                charger.isActiveAndEnabled && charger.gameObject.activeInHierarchy;
            bool chargerMatchesState = ReferenceEquals(chargersById[entity], charger);
            try { CaptureAndPublish(); }
            catch (InvalidDataException ex) { Debug.LogWarning("MonMP could not validate portable-console state for a request: " + ex.Message); }
            var status = PortableConsoleChargerPolicy.ValidateHostRequest(
                Ready && peerKnown,
                NewActionsBlocked || consoleSaves.Active || MonMP.Plugin.MonMPPlugin.Instance?.World?.SaveGate.Active == true,
                peerKnown && TakeBudget(peer),
                chargerMatchesState,
                active,
                canInteract,
                inRange,
                handsFree,
                request.Action,
                state.Location,
                ToPortableLocation(charger));

            if (status == PortableConsoleChargerStatus.Accepted)
            {
                try
                {
                    applying = true;
                    MethodInfo method = request.Action == PortableConsoleChargerAction.Place ? PlaceMethod! : RetrieveMethod!;
                    method.Invoke(charger, null);
                    var after = Capture();
                    bool expected = request.Action == PortableConsoleChargerAction.Place
                        ? after.Location == ToPortableLocation(charger)
                        : after.Location == PortableConsoleLocation.Hand;
                    if (!expected) status = PortableConsoleChargerStatus.NativeRejected;
                    else
                    {
                        CaptureAndPublish();

#if MONMP_DEVELOPMENT
                        TestAccepted++;
#endif

                    }
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    status = PortableConsoleChargerStatus.NativeRejected;
                }
                finally { applying = false; }
            }
            if (status != PortableConsoleChargerStatus.Accepted)
#if MONMP_DEVELOPMENT
TestRejected++;
#else
{}
#endif

            Remember(key, status);
            SendReply(peer, entity, request.Operation, status);
        }

        private void Remember((uint Peer, Guid Operation) key, PortableConsoleChargerStatus status)
        {
            replayResults[key] = status;
            replayOrder.Enqueue(key);
            while (replayOrder.Count > MaxReplayResults) replayResults.Remove(replayOrder.Dequeue());
        }

        private void SendReply(uint peer, ulong entity, Guid operation, PortableConsoleChargerStatus status)
        {
            if (channel?.Ready == true && (peer == 0 || session?.Peers.ContainsKey(peer) == true))
                channel.Reply(peer, entity, new PortableConsoleChargerReply(operation, status).Encode());
        }

        private void Rejected(uint peer, ulong entity, byte[] payload)
        {
            if (!host || !PortableConsoleChargerRequest.Is(payload)) return;
            try { SendReply(peer, entity, PortableConsoleChargerRequest.Decode(payload).Operation, PortableConsoleChargerStatus.NotReady); }
            catch (InvalidDataException) { }
        }

        private void Reply(ulong entity, byte[] payload)
        {
            if (ConsoleSaveReply(payload)) return;
            if (host || !PortableConsoleChargerReply.Is(payload)) return;
            PortableConsoleChargerReply reply;
            try { reply = PortableConsoleChargerReply.Decode(payload); }
            catch (InvalidDataException) { return; }
            if (!pending.TryGetValue(reply.Operation, out var request) || request.Entity != entity) return;
            pending.Remove(reply.Operation);
            if (reply.Status != PortableConsoleChargerStatus.Accepted)
                Report("The host could not move the portable console (" + reply.Status + "). Check your range, hands and charger state.");
            else if (request.Action == PortableConsoleChargerAction.Retrieve)
                ShowGuestRetrieveTip(request.Charger);
        }

        private void ShowGuestRetrieveTip(Furniture_PortableGameConsole_Charger charger)
        {
            var inputAction = ChargerInputActionField?.GetValue(charger) as InputAction ??
                throw new InvalidOperationException("The guest portable-console charger input action is unavailable.");
            string bindingDisplayString = inputAction.GetBindingDisplayString(1);
            if (gameplay.Get_InputDeviceType == InputDeviceType.Keyboard)
            {
                bindingDisplayString = inputAction.GetBindingDisplayString(0);
            }
            Singleton<UI_EventMessage>.i.Show("[<color=yellow>" + bindingDisplayString + "</color>] " + TEXT.GetText("PortableGameConsole_PickupFromChargerTip"),
                LocalizationSettings.StringDatabase.GetLocalizedString("UI_General", "LoadingScreen_TipsTitle", null, FallbackBehavior.UseProjectSettings), 4f);
        }

        private void ExpirePendingRequests()
        {
            foreach (var operation in pending.Where(pair => Time.unscaledTime - pair.Value.SentAt > 8f).Select(pair => pair.Key).ToArray())
            {
                pending.Remove(operation);
                Report("The portable-console request timed out; check the shared charger before trying again.");
            }
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            PortableConsoleChargerState? received = null;
            foreach (var id in chargersById.Keys)
            {
                if (!records.TryGetValue(id, out var record))
                {
                    if (snapshotReceived) return;
                    throw new InvalidDataException("Initial portable-console charger record is missing.");
                }
                if (record.Adapter != PortableConsoleChargerState.Adapter)
                    throw new InvalidDataException("Initial portable-console charger record has the wrong adapter.");
                var next = PortableConsoleChargerState.Decode(record.Payload);
                if (received.HasValue && !received.Value.Equals(next))
                    throw new InvalidDataException("Portable-console charger records disagree on host state.");
                received = next;
            }
            if (!received.HasValue) throw new InvalidDataException("No portable-console charger state was received.");
            snapshotReceived = true;
            Apply(received.Value);
        }

        private void Changed(WorldRecord record)
        {
            if (host || record.Adapter != PortableConsoleChargerState.Adapter) return;
            if (!chargersById.ContainsKey(record.Id)) throw new InvalidDataException("Unknown portable-console charger record.");
            Apply(PortableConsoleChargerState.Decode(record.Payload));
        }

        private void Apply(PortableConsoleChargerState next)
        {
            state = next;
            applying = true;
            try
            {
                hands.Get_ConsoleLocation = ToNativeLocation(next.Location);
                console.Get_BatteryLife = next.BatteryLife;
                console.Battery_Charging = next.ChargingTicks;
                foreach (var charger in chargers)
                {
                    if (charger == null) continue;
                    if (ConsoleObjectField.GetValue(charger) is GameObject presentation)
                        presentation.SetActive(charger.ChargerLocation == ToNativeLocation(next.Location));
                }
                if (next.Location != PortableConsoleLocation.Hand)
                {
                    var active = chargers.FirstOrDefault(value => value.ChargerLocation == ToNativeLocation(next.Location));
                    if (active != null && active.isActiveAndEnabled)
                    {
                        try { UpdateIndicatorsMethod!.Invoke(active, null); }
                        catch (TargetInvocationException ex) { Debug.LogWarning("MonMP could not refresh portable-console charger indicators: " + (ex.InnerException?.Message ?? ex.Message)); }
                    }
                }
            }
            finally { applying = false; }
        }

        private void CaptureAndPublish()
        {
            var next = Capture();
            state = next;
            if (!Ready || channel == null || lastPublished.HasValue && lastPublished.Value.Equals(next)) return;
            byte[] payload = next.Encode();
            foreach (var id in chargersById.Keys) channel.Publish(id, payload);
            lastPublished = next;
        }

        public void Update()
        {
            if (host) consoleSaves.Expire(Time.unscaledTime);
            if (!Running) return;
            if (!host)
            {
                ExpireConsoleSaveReply();
                ExpirePendingRequests();
                if (snapshotReceived)
                {
                    try
                    {
                        if (!Capture().Equals(state) || !PresentationMatches(state)) Apply(state);
                    }
                    catch (InvalidDataException ex) { Debug.LogWarning("MonMP could not verify the replicated portable-console state: " + ex.Message); }
                }
                return;
            }
            if (!Ready || Time.unscaledTime < nextCapture) return;
            nextCapture = Time.unscaledTime + CaptureInterval;
            try { CaptureAndPublish(); }
            catch (InvalidDataException ex) { Debug.LogWarning("MonMP portable-console state was not published: " + ex.Message); }
        }

        private static void Report(string message) => MonMP.Plugin.MonMPPlugin.Instance?.ReportItemUseUnavailable(message);

        private bool PresentationMatches(PortableConsoleChargerState value)
        {
            ConsoleLocation location = ToNativeLocation(value.Location);
            foreach (var charger in chargers)
            {
                if (charger == null || ConsoleObjectField.GetValue(charger) is not GameObject presentation) return false;
                if (presentation.activeSelf != (charger.ChargerLocation == location)) return false;
            }
            return true;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (session != null) { session.PoseReceived -= PoseReceived; session.PeerLeft -= PeerLeft; }
            if (channel != null)
            {
                channel.SnapshotApplied -= Snapshot;
                channel.Changed -= Changed;
                channel.CommandRequested -= Command;
                channel.CommandRejected -= Rejected;
                channel.ReplyReceived -= Reply;
            }
            if (!host && snapshotReceived)
            {
                try { Apply(original); }
                catch (Exception ex) { Debug.LogWarning("MonMP could not restore the local portable-console state: " + ex.Message); }
            }
            if (ReferenceEquals(Active, this)) Active = null;
            presence.Clear(); requestBudgets.Clear(); pending.Clear(); replayResults.Clear(); replayOrder.Clear();
            session = null; channel = null;
        }

        private static void ReportFailure(PortableConsoleChargerStatus status)
        {
            Report("The portable-console charger request was rejected (" + status + ").");
        }
    }
}
