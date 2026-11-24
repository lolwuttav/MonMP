using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Persistence;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using MonMP.Plugin.Testing;
using UniStorm;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Diagnostics
{
    // Native regressions for PROD-01/02 and E-01. Fixture inputs and prerequisites are
    // prepared once; every debit and output after admission comes from the native action.
    internal sealed class PairedProductionSettlementTest
    {
        private const byte Marker = 0x75, Domain = 0x50;
        private const float ControlRetryInterval = .5f, ControlTimeout = 60f;
        private static string? preparedSaveName, preparedOriginalSave;
        private static int preparedOriginalTimestamp;
        private static PairedProductionSettlementTest? nativeObserver;
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly WorldChannel fixtureChannel;
        private readonly Vector3 originalPosition;
        private readonly Quaternion originalCamera;
        private readonly float[] originalStatistics;
        private readonly string originalSave;
        private readonly int originalTimestamp;
        private readonly List<Action> restore = new List<Action>();
        private readonly List<InteractableItems> fixtures = new List<InteractableItems>();
        private readonly List<InteractableItems> inputs = new List<InteractableItems>();
        private readonly HashSet<Guid> planterReplies = new HashSet<Guid>();
        private readonly Dictionary<ToDoList_Line, bool> originalProgress = new Dictionary<ToDoList_Line, bool>();
        private readonly float started = Time.unscaledTime;
        private Home_Bunker bunker = null!;
        private SugarShack shack = null!;
        private SugarShack_CrimpingMachine crimper = null!;
        private Collider crimperReception = null!;
        private ItemForSale_Dealer planterSale = null!;
        private Item_MapleSyrupCan_Empty emptyCan = null!;
        private HashSet<InteractableItems> outputsBefore = new HashSet<InteractableItems>();
        private HashSet<InteractableItems> cansBefore = new HashSet<InteractableItems>();
        private HashSet<InteractableItems> plantersBefore = new HashSet<InteractableItems>();
        private InteractableItems[] publishedOutputs = Array.Empty<InteractableItems>();
        private int phase, recipe, guestOrder, argument, ack, expectedPlanters, cashBefore, priceCents, canBaseline, emptyBaseline, planterBaseline, emptyCanPickupAttempts, planterClickAttempts, nativePlanterBaseline;
        private DealerMachineKind planterKind;
        private DealerMachinePurchaseReply? planterReply;
        private uint peer;
        private byte controlSequence, pendingOrder, pendingValue, receivedSequence, receivedOrder, receivedValue, pendingAckSequence, pendingAckOrder, pendingAckValue;
        private int controlRetries, ackRetries;
        private float controlStarted, nextControlRetry, ackStarted, nextAckRetry;
        private float nextAdmissionLog;
        private string lastAdmission = "";
        private float next, operationStarted;
        private float outputBindingStarted;
        private string lastOutputBindingWait = "";
        private bool cleaned, prerequisitesPrepared, recipePrepared, saveBaselinesCaptured, emptyCanApproached;
        private Harmony? nativeObservationPatch;
        private Exception? nativeObservationFailure;
        private bool nativeFriesObserved;
        private string nativeObservation = "not observed";
        private string savedDirectory = "";
        private static readonly Type[] Outputs = { typeof(Item_WeedPreRollBigPack), typeof(Item_WeedPreRollBigPack), typeof(Item_WeedPreRollHugePack), typeof(Item_FrenchFries) };
        private static readonly string[] Anchors = { "Packagers_SpawnBigPack", "Packagers_SpawnBigPackOnHuge", "Packagers_SpawnHugePack", "SpawnFrenchFriesLocation" };
        internal string Status => "production settlement phase=" + phase + "; recipe=" + recipe + "; order=" + guestOrder + "; ack=" + ack +
            "; control=" + controlSequence + "/" + pendingOrder + "/" + pendingValue + "/retries=" + controlRetries +
            "; received=" + receivedSequence + "/" + receivedOrder + "/" + receivedValue + "; pendingAck=" + pendingAckSequence + "/retries=" + ackRetries +
            "; bunker=" + bunker.gameObject.activeInHierarchy + "; power=" + (bunker.gameObject.activeInHierarchy && bunker.isBunkerPowerOn) +
            "; native=" + nativeObservation +
            "; admission=" + AdmissionStatus() + "; fixtureInputs=" + string.Join(" | ", inputs.Select(DescribeInput)) +
            "; crimperInput=" + CrimperInputStatus + "; planter=" + PlanterStatus + "; planterAmount=" + expectedPlanters + "; save=" + plugin.World?.SaveRounds?.Phase + "; directory=" + savedDirectory;
        private string PlanterStatus
        {
            get
            {
                if (plugin.World == null || planterKind < DealerMachineKind.OutdoorPlanter1 || planterKind > DealerMachineKind.OutdoorPlanter4) return "not selected";
                var dealer = plugin.World.DealerMachines;
                var sale = dealer.TestDisplay(planterKind);
                if (sale == null) return "missing display";
                var store = Field<Stores>(sale, "store");
                return planterKind + "/open=" + (store != null && store.isStoreOpen()) + "/active=" + sale.gameObject.activeInHierarchy +
                    "/stock=" + Field<GameObject>(sale, "AllPlanters").activeSelf + "/range=" + Vector3.Distance(gameplay.PlayerWalking.transform.position, sale.transform.position).ToString("F2") +
                    "/cash=" + Object.FindObjectOfType<Cash_Manager>().GetCash + "/price=" + Mathf.RoundToInt(Field<float>(sale, "price") * 100) +
                    "/settled=" + dealer.TransactionsSettled + "/native=" + dealer.TestNativePurchases + "/clicks=" + planterClickAttempts +
                    "/reply=" + (dealer.TestLastReply.HasValue ? dealer.TestLastReply.Value.Operation + ":" + dealer.TestLastReply.Value.Status : "none");
            }
        }
        private string CrimperInputStatus
        {
            get
            {
                if (emptyCan == null || plugin.World == null) return "not staged or consumed";
                ulong id = plugin.World.Items.TestBoundId(emptyCan);
                return "id=" + id.ToString("X") + "/attempts=" + emptyCanPickupAttempts + "/held=" + (gameplay.Player.Get_HoldingItem == emptyCan) +
                    "/trigger=" + (emptyCan.itemCollider != null && emptyCan.itemCollider.isTrigger) +
                    (id == 0 ? "/binding pending" : "/" + plugin.World.Items.TestPickupStatus(id));
            }
        }

        internal PairedProductionSettlementTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay;
            Require(LocalTestIsolation.Installed, "The fixture requires installed save isolation.");
            originalPosition = gameplay.PlayerWalking.transform.position; originalCamera = gameplay.Player.mainCamera.transform.rotation;
            originalStatistics = (float[])GameManager.Get_PlayerStats.Clone();
            originalSave = plugin.TestOptions.IsHost ? preparedOriginalSave ?? throw new InvalidOperationException("Production settlement fixture was not selected before hosting.") : LocalTestIsolation.GetString("SaveName");
            originalTimestamp = plugin.TestOptions.IsHost ? preparedOriginalTimestamp : LocalTestIsolation.GetInt("SaveTimestamp", -1);
            if (plugin.TestOptions.IsHost)
                Require(LocalTestIsolation.GetString("SaveName") == preparedSaveName && LocalTestIsolation.GetInt("SaveTimestamp", -1) == -1,
                    "Fixture save selection changed after the coordinator bound its history.");
            bunker = Object.FindObjectsOfType<Home_Bunker>(true).Single(value => value.gameObject.scene.IsValid());
            fixtureChannel = plugin.World!.Channel;
            if (plugin.TestOptions.IsHost) InstallNativeObservation();
            fixtureChannel.CommandRequested += OnCommand;
            fixtureChannel.ReplyReceived += OnReply;
        }
        private void InstallNativeObservation()
        {
            Require(nativeObserver == null && nativeObservationPatch == null, "Another native production observer is still installed.");
            nativeObserver = this;
            nativeObservationPatch = new Harmony("MonMP.LocalTest.ProductionSettlement");
            try
            {
                var button = AccessTools.Method(typeof(Home_Bunker), nameof(Home_Bunker.Button_FrenchFriesPress), Type.EmptyTypes);
                Require(button != null && button.ReturnType == typeof(void), "The native FrenchFries button signature changed.");
                nativeObservationPatch.Patch(button!, postfix: new HarmonyMethod(typeof(PairedProductionSettlementTest), nameof(FrenchFriesButtonReturned)));
                PatchHealth.VerifyManualPatchInstalled(nativeObservationPatch, button!);
            }
            catch
            {
                nativeObservationPatch.UnpatchSelf(); nativeObservationPatch = null; nativeObserver = null;
                throw;
            }
        }
        private static void FrenchFriesButtonReturned(Home_Bunker __instance)
        {
            var test = nativeObserver;
            if (test == null || test.cleaned || test.nativeFriesObserved || test.phase != 2 || test.recipe != 3 ||
                test.plugin.Session?.IsRunning != true || !ReferenceEquals(test.plugin.World?.Channel, test.fixtureChannel) ||
                !ReferenceEquals(test.bunker, __instance) || !SharedRemoteClicks.RunningForGuest || SharedRemoteClicks.ProductionPeer != test.peer) return;
            test.nativeFriesObserved = true;
            try
            {
                // Unity has advanced the real iterator to its first yield before this native
                // button returns. Observe the paid interval here rather than relying on a
                // later frame catching the short producer. Never create or retrack an iterator.
                bool pending = NativeSaveProduction.Pending(__instance, "BuildFrenchFriesPack");
                bool running = Field<bool>(__instance, "isFrenchFriesPressMachineRunning");
                int outputs = test.NewOutputs(Outputs[3], test.outputsBefore).Length;
                test.nativeObservation = "FrenchFries button return/frame=" + Time.frameCount + "/pending=" + pending +
                    "/running=" + running + "/settled=" + test.plugin.World!.RemoteClicks.TransactionsSettled + "/outputs=" + outputs;
                Debug.Log("MONMP_PRODUCTION_ADMITTED: " + test.nativeObservation + "; inputs=" + string.Join(" | ", test.inputs.Select(test.DescribeInput)));
                Require(running && pending, "Native FrenchFries action has no running retained iterator: " + test.nativeObservation);
                test.SettleAdmittedRecipe();
            }
            catch (Exception error)
            {
                // Do not let a diagnostic assertion change the production handler's flow.
                // Tick reports the original failure immediately after native dispatch returns.
                test.nativeObservationFailure = error;
                Debug.Log("MONMP_PRODUCTION_OBSERVATION_FAIL: " + error);
            }
        }
        internal static void PrepareHostSave()
        {
            if (preparedSaveName != null) return;
            Require(LocalTestIsolation.Installed, "Fixture save selection requires isolated storage.");
            preparedOriginalSave = LocalTestIsolation.GetString("SaveName"); preparedOriginalTimestamp = LocalTestIsolation.GetInt("SaveTimestamp", -1);
            preparedSaveName = "MonMPProd-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            LocalTestIsolation.SetString("SaveName", preparedSaveName); LocalTestIsolation.SetInt("SaveTimestamp", -1);
        }
        private void OnCommand(uint sender, ulong entity, byte[] payload)
        {
            if (!cleaned && plugin.TestOptions.IsHost && sender == peer && planterKind >= DealerMachineKind.OutdoorPlanter1 &&
                entity == DealerMachineState.EntityFor(planterKind) && DealerMachinePurchaseRequest.Is(payload))
            {
                var purchase = DealerMachinePurchaseRequest.Decode(payload);
                Debug.Log("MONMP_PRODUCTION_PLANTER_REQUEST: operation=" + purchase.Operation + "; quoted=" + purchase.PriceCents + "; " + PlanterStatus);
            }
            if (cleaned || !plugin.TestOptions.IsHost || sender == 0 || entity != ProfileRestoreChannel.Entity || payload.Length != 5 || payload[0] != Marker || payload[1] != Domain) return;
            if (peer != 0 && sender != peer) return;
            if (pendingOrder != 2 || payload[2] != controlSequence || payload[3] != pendingOrder || payload[4] != pendingValue) return;
            if (ack != pendingOrder) Debug.Log("MONMP_PRODUCTION_CONTROL: acknowledged=" + controlSequence + "; order=" + pendingOrder + "; value=" + pendingValue);
            peer = sender; ack = payload[3]; argument = payload[4];
        }
        private void OnReply(ulong entity, byte[] payload)
        {
            if (!cleaned && !plugin.TestOptions.IsHost && planterKind >= DealerMachineKind.OutdoorPlanter1 &&
                entity == DealerMachineState.EntityFor(planterKind) && DealerMachinePurchaseReply.Is(payload))
            {
                var purchase = DealerMachinePurchaseReply.Decode(payload);
                if (planterReplies.Add(purchase.Operation))
                {
                    planterReply = purchase;
                    Debug.Log("MONMP_PRODUCTION_PLANTER_REPLY: operation=" + purchase.Operation + "; status=" + purchase.Status + "; " + PlanterStatus);
                }
                return;
            }
            if (cleaned || plugin.TestOptions.IsHost || entity != ClockState.Entity || payload.Length != 5 || payload[0] != Marker || payload[1] != Domain) return;
            byte sequence = payload[2], order = payload[3], value = payload[4];
            if (sequence < receivedSequence) return;
            if (sequence == receivedSequence)
            {
                Require(order == receivedOrder && value == receivedValue, "A repeated fixture order changed its arguments.");
                return; // Preserve approach timers, output baselines and the already-issued native action.
            }
            Require(sequence == receivedSequence + 1 && order >= 1 && order <= 4, "Fixture control skipped an unobserved native stage.");
            pendingAckSequence = 0; ackRetries = 0;
            receivedSequence = sequence; receivedOrder = order; receivedValue = value;
            guestOrder = order; argument = value; phase = 0; next = 0;
            Debug.Log("MONMP_PRODUCTION_CONTROL: received=" + sequence + "; order=" + order + "; value=" + value);
        }
        private void Tell(byte order, byte value = 0)
        {
            Require(controlSequence < byte.MaxValue, "Fixture control sequence exhausted.");
            controlSequence++; pendingOrder = order; pendingValue = value; ack = 0;
            controlStarted = Time.unscaledTime; nextControlRetry = 0; controlRetries = 0;
            Debug.Log("MONMP_PRODUCTION_CONTROL: sending=" + controlSequence + "; order=" + order + "; value=" + value);
            RetryControl();
        }
        private void StopOrderRetry() { pendingOrder = 0; controlRetries = 0; }
        private void Signal(byte order, byte value = 0)
        {
            Require(receivedSequence != 0 && order == receivedOrder && value == receivedValue, "Fixture acknowledgement did not match its received order.");
            pendingAckSequence = receivedSequence; pendingAckOrder = order; pendingAckValue = value;
            ackStarted = Time.unscaledTime; nextAckRetry = 0; ackRetries = 0; RetryControl();
        }
        private void RetryControl()
        {
            if (plugin.TestOptions.IsHost)
            {
                if (pendingOrder == 0) return;
                Require(Time.unscaledTime - controlStarted < ControlTimeout, "Fixture order was not completed in time: " + Status);
                if (Time.unscaledTime < nextControlRetry || !fixtureChannel.TestPeerSynchronized(peer)) return;
                fixtureChannel.Reply(peer, ClockState.Entity, new[] { Marker, Domain, controlSequence, pendingOrder, pendingValue });
                controlRetries++; nextControlRetry = Time.unscaledTime + ControlRetryInterval;
            }
            else
            {
                if (pendingAckSequence == 0) return;
                Require(Time.unscaledTime - ackStarted < ControlTimeout, "Fixture output acknowledgement did not advance the host: " + Status);
                if (Time.unscaledTime < nextAckRetry) return;
                // This immutable command record avoids racing the live clock revision.
                // Replies stay on ClockState: the real profile reply decoder owns its entity.
                fixtureChannel.Request(ProfileRestoreChannel.Entity, new[] { Marker, Domain, pendingAckSequence, pendingAckOrder, pendingAckValue });
                ackRetries++; nextAckRetry = Time.unscaledTime + ControlRetryInterval;
            }
        }
        internal bool Tick()
        {
            if (nativeObservationFailure != null) throw new InvalidOperationException("Production settlement native observation failed: " + nativeObservation, nativeObservationFailure);
            Require(Time.unscaledTime - started < 270, "Timed out: " + Status);
            Require(plugin.Session?.IsRunning == true && ReferenceEquals(plugin.World?.Channel, fixtureChannel), "Fixture session ended or was replaced before completion.");
            if (plugin.TestOptions.IsHost && phase == 10 && plugin.Session!.Peers.Count == 1) { Cleanup(); return true; }
            if (plugin.World?.Channel.FullySynchronized != true || !plugin.World.PersonalProfileReady) return false;
            RetryControl();
            if (Time.unscaledTime < next) return false;
            return plugin.TestOptions.IsHost ? Host() : Guest();
        }
        private bool Host()
        {
            var world = plugin.World!;
            var items = world.Items;
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                peer = plugin.Session.Peers.Values.Single(value => value.Id != 0).Id;
                if (!world.Channel.TestPeerSynchronized(peer)) return false;
                PreparePrerequisites(); PrepareRecipe(); phase = 1; next = Time.unscaledTime + .5f;
            }
            else if (phase == 1)
            {
                if (inputs.Any(value => items.TestBoundId(value) == 0)) { items.DiscoverSoon(); return false; }
                Require(inputs.All(value => items.TestState(items.TestBoundId(value)).Lease.Owner == ItemLease.Unowned), "Recipe fixture was not unowned before admission.");
                var scan = Field<Transform>(bunker, "Packagers_SpawnBigPack").position;
                var nativeInputs = Physics.OverlapSphere(scan, 6f).Where(value => value.tag != "Player").Select(value => value.GetComponent<InteractableItems>()).ToArray();
                Require(inputs.All(value => nativeInputs.Contains(value)), "A frozen fixture input is outside the native producer's collider scan: " + Status);
                Debug.Log("MONMP_PRODUCTION_INPUTS: recipe=" + recipe + "; " + string.Join(" | ", inputs.Select(DescribeInput)));
                Tell(1, (byte)recipe); phase = 2;
            }
            else if (phase == 2 && recipe != 3 && NativeSaveProduction.Pending(bunker, "BuildCannabisPack"))
            {
                if (recipe == 2 && inputs.Any(value => value != null)) return false; // Unity destruction settles at end of frame.
                SettleAdmittedRecipe();
            }
            else if (phase == 3)
            {
                var output = NewOutputs(Outputs[recipe], outputsBefore);
                if (output.Length == 1 && items.TestBoundId(output[0]) != 0)
                {
                    Require(world.RemoteClicks.TransactionsSettled && InputsPaid(), "Settlement changed the paid source balance or left the producer running.");
                    fixtures.Add(output[0]);
                    Debug.Log("MONMP_PRODUCTION_NATIVE: recipe=" + recipe + "; guest native button; inputs=" + inputs.Count + "; exact shared output=" + items.TestBoundId(output[0]).ToString("X") + "; retained iterator settled; elapsed=" + (Time.unscaledTime - operationStarted));
                    Tell(2, (byte)recipe); phase = 4;
                }
                else Require(output.Length <= 1, "Bunker producer created duplicate native outputs.");
            }
            else if (phase == 4 && ack == 2 && argument == recipe)
            {
                StopOrderRetry();
                DestroyCurrentRecipe(); ack = 0;
                if (++recipe < Outputs.Length) { PrepareRecipe(); phase = 1; next = Time.unscaledTime + .5f; }
                else { PrepareSaveInputs(); operationStarted = Time.unscaledTime; phase = 11; next = Time.unscaledTime + .25f; }
            }
            else if (phase == 11)
            {
                Require(Time.unscaledTime - operationStarted < 12, "Native empty-can pickup did not settle: " + CrimperInputStatus);
                Require(emptyCan != null, "The staged empty can disappeared before its native pickup.");
                var can = emptyCan!;
                ulong id = items.TestBoundId(can);
                if (id == 0) { items.DiscoverSoon(); return false; }
                if (!emptyCanApproached)
                {
                    Require(!gameplay.Player.IsHoldingAnObject, "Preserve the host's existing held item before crimper preparation.");
                    MoveNear(can.transform.position); emptyCanApproached = true;
                    next = Time.unscaledTime + .5f; return false; // Publish the approach pose before acquiring the shared can.
                }
                if (gameplay.Player.Get_HoldingItem != can)
                {
                    Require(!gameplay.Player.IsHoldingAnObject, "An unrelated native hand state blocked the disposable empty-can pickup.");
                    if (!items.TestPickupPending(id))
                    {
                        Require(emptyCanPickupAttempts++ < 6, "The empty can did not receive its native host pickup lease: " + CrimperInputStatus);
                        items.TestGrab(id); next = Time.unscaledTime + .5f;
                    }
                    return false;
                }
                Require(items.TestState(id).Lease.Owner == 0 && can.itemCollider != null && can.itemCollider.isTrigger,
                    "Native held-can preparation did not produce the host-owned triggering collider: " + CrimperInputStatus);
                Require(crimper.GetComponent<SOUND_SugarShack_CrimpingMachine>() != null, "The native crimper has not completed Start.");
                Debug.Log("MONMP_PRODUCTION_CRIMPER_HELD: " + CrimperInputStatus + "; solid receiver=" + SharedBuildingDoors.StablePath(crimperReception.transform));
                Tell(3, (byte)SelectedPlanterKind()); phase = 5;
            }
            else if (phase == 5 && !world.DealerMachines.ProductionSettled)
            {
                StopOrderRetry();
                Require(NewOutputs(typeof(Garden_OutdoorPlanter), plantersBefore).Length < expectedPlanters, "The planter fixture missed the partial paid multipack interval.");
                Require(Object.FindObjectOfType<Cash_Manager>().GetCash == cashBefore - priceCents, "Native planter purchase did not debit the host price exactly once.");
                Require(world.DealerMachines.TestNativePurchases == nativePlanterBaseline + 1, "The planter fixture admitted more than one native purchase.");
                Require(shack.Get_SyrupLevel == 3 && shack.Get_CrimpingMachine_LidsAmount == 4, "Crimper fixture changed before its native trigger.");
                Require(crimperReception != null && crimperReception.enabled && crimperReception.gameObject.activeInHierarchy,
                    "The native crimper receiver collider became unavailable after preparation.");
                Require(shack.Get_HomeGenerator != null && shack.Get_HomeGenerator.Get_isEngineRunning,
                    "The crimper has no native generator/hydro power.");
                Require(crimper.GetComponent<SOUND_SugarShack_CrimpingMachine>() != null,
                    "The newly activated native crimper has not completed Start.");
                Require(emptyCan != null && emptyCan.itemCollider != null && emptyCan.itemCollider.enabled && emptyCan.itemCollider.isTrigger &&
                    gameplay.Player.Get_HoldingItem == emptyCan && items.TestState(items.TestBoundId(emptyCan)).Lease.Owner == 0,
                    "The disposable empty can is no longer the native host-held triggering input: " + CrimperInputStatus);
                var can = emptyCan!;
                var receiver = crimperReception!;
                ulong emptyId = items.TestBoundId(can);
                MoveNear(receiver.bounds.center);
                can.transform.position = receiver.bounds.center; Physics.SyncTransforms();
                bool automaticEntry = Field<bool>(crimper, "isWorking");
                Debug.Log("MONMP_PRODUCTION_CRIMPER: receiver=" + SharedBuildingDoors.StablePath(receiver.transform) +
                    "; receiverTrigger=" + receiver.isTrigger + "; inputTrigger=True; power=True; started=True; empty=" + emptyId.ToString("X") +
                    "; native physics entry=" + automaticEntry);
                // Native Grab makes the CAN a trigger; the machine's colliders stay solid.
                // If physics already delivered that entry, never dispatch the action again.
                if (!automaticEntry)
                {
                    Require(emptyCan != null && emptyCan.itemCollider != null, "The held can was consumed without a running native crimper.");
                    AccessTools.Method(typeof(SugarShack_CrimpingMachine), "OnTriggerEnter").Invoke(crimper, new object[] { emptyCan!.itemCollider! });
                }
                Require(Field<bool>(crimper, "isWorking") && NativeSaveProduction.Pending(crimper, "Working"), "The real crimper trigger did not start a retained paid iterator.");
                Require(shack.Get_SyrupLevel == 2 && shack.Get_CrimpingMachine_LidsAmount == 3 && !world.SugarShack.ProductionSettled, "Crimper did not debit exactly one syrup and one lid before output.");
                Require(NewOutputs(typeof(Item_MapleSyrupCan), cansBefore).Length == 0, "The test missed the crimper pre-output interval.");
                operationStarted = Time.unscaledTime;
                SaveSystem.Save(House_Location.SpecialMenu);
                Require(world.Saves!.Pending, "Native save did not enter coordinated collection during paid work.");
                Debug.Log("MONMP_PRODUCTION_SAVE_REQUEST: planter pending=True; crimper pending=True; syrup=2; lids=3; planter debit=" + priceCents + "; frame=" + Time.frameCount);
                phase = 6;
            }
            else if (phase == 6 && !world.Saves!.Pending)
            {
                Require(world.SaveRounds!.Phase == SavePhase.Complete && world.Saves.LastFailure == "", "Coordinated production save failed: " + world.Saves.LastFailure);
                Require(world.NativeProductionSettled && world.DealerMachines.ProductionSettled && world.SugarShack.ProductionSettled, "Published save left paid native work unsettled.");
                var cans = NewOutputs(typeof(Item_MapleSyrupCan), cansBefore);
                var planters = NewOutputs(typeof(Garden_OutdoorPlanter), plantersBefore);
                Require(cans.Length == 1 && planters.Length == expectedPlanters, "Paid production did not finish with exactly one can and the complete planter batch.");
                Require(emptyCan == null && shack.Get_SyrupLevel == 2 && shack.Get_CrimpingMachine_LidsAmount == 3 && Object.FindObjectOfType<Cash_Manager>().GetCash == cashBefore - priceCents, "Save settlement consumed its native recipe or cash twice.");
                Require(world.DealerMachines.TestNativePurchases == nativePlanterBaseline + 1, "Save settlement replayed the native planter purchase.");
                fixtures.AddRange(cans); fixtures.AddRange(planters);
                var savedPlayer = SaveSystem.GetPlayerFromSave(LocalTestIsolation.GetString("SaveName"), SaveSystem.Get_LastSaveTimestamp);
                var savedItems = SaveSystem.GetItemsFromTheSave();
                Require(savedPlayer != null && savedPlayer.SyrupLevel == 2 && savedPlayer.CrimpingMachine_LidsAmount == 3, "The published native PlayerData omitted the crimper debit.");
                Require(savedItems != null && savedItems.Count(value => value.id == cans[0].PrefabName) == canBaseline + 1 &&
                    savedItems.Count(value => value.id == planters[0].PrefabName) == planterBaseline + expectedPlanters &&
                    savedItems.Count(value => value.id == EmptyPrefabName) == emptyBaseline, "Native saved item readback omitted/duplicated the completed output or retained the consumed empty can.");
                savedDirectory = world.Saves.LastSavedDirectory;
                Debug.Log("MONMP_PRODUCTION_SAVED_READBACK: directory=" + savedDirectory + "; native syrup=2; lids=3; finished can delta=1; empty can delta=0; planter delta=" + expectedPlanters + "; request-to-complete=" + (Time.unscaledTime - operationStarted) + "; full scene reload not exercised");
                publishedOutputs = cans.Concat(planters).ToArray(); outputBindingStarted = Time.unscaledTime;
                phase = 12;
            }
            else if (phase == 12)
            {
                Require(world.NativeProductionSettled && world.DealerMachines.TestNativePurchases == nativePlanterBaseline + 1 &&
                    emptyCan == null && shack.Get_SyrupLevel == 2 && shack.Get_CrimpingMachine_LidsAmount == 3 &&
                    Object.FindObjectOfType<Cash_Manager>().GetCash == cashBefore - priceCents,
                    "Post-save replication changed the exact paid production balances.");
                Require(NewOutputs(typeof(Item_MapleSyrupCan), cansBefore).Length == 1 &&
                    NewOutputs(typeof(Garden_OutdoorPlanter), plantersBefore).Length == expectedPlanters,
                    "Post-save replication changed the exact native output count.");
                bool bound = publishedOutputs.All(value =>
                {
                    if (value == null) return false;
                    ulong id = items.TestBoundId(value);
                    return id != 0 && world.Channel.Records.TryGetValue(id, out var record) && record.Adapter == ItemState.Adapter &&
                        items.TestState(id).Exists && (!(value is Garden_OutdoorPlanter) || world.Garden.TestPlanterBound(id));
                });
                string identities = string.Join(" | ", publishedOutputs.Select(DescribePublishedOutput));
                if (!bound)
                {
                    if (identities != lastOutputBindingWait)
                    {
                        Debug.Log("MONMP_PRODUCTION_OUTPUT_BINDING_WAIT: elapsed=" + (Time.unscaledTime - outputBindingStarted) +
                            "; frame=" + Time.frameCount + "; " + identities);
                        lastOutputBindingWait = identities;
                    }
                    // Native serialization uses the native objects directly. Items discovers
                    // loose outputs on its normal frame+2/one-second schedule; Garden binds
                    // the planter after that. Wait for those real adapters without registering,
                    // rediscovering, replacing or changing any already-saved output here.
                    Require(Time.unscaledTime - outputBindingStarted < 8,
                        "Published production outputs did not acquire authoritative item/garden identities: " + identities);
                    return false;
                }
                Debug.Log("MONMP_PRODUCTION_OUTPUT_IDENTITIES: elapsed=" + (Time.unscaledTime - outputBindingStarted) + "; " + identities);
                Tell(4, (byte)expectedPlanters); phase = 10;
            }
            return false;
        }
        private void SettleAdmittedRecipe()
        {
            StopOrderRetry();
            var world = plugin.World!;
            Require(!world.RemoteClicks.TransactionsSettled, "Native delayed producer did not block save readiness.");
            Require(NewOutputs(Outputs[recipe], outputsBefore).Length == 0, "The test missed the paid pre-output interval.");
            Require(InputsPaid(), "Native guest bunker button did not debit every host source exactly once.");
            Require(world.RemoteClicks.SettleForSave(), "The retained native bunker iterator did not settle.");
            operationStarted = Time.unscaledTime; phase = 3; next = Time.unscaledTime + .5f;
        }
        private bool Guest()
        {
            var items = plugin.World!.Items;
            // A pending join placement can otherwise undo the machine approach before its click.
            if (phase == 0 && (guestOrder == 1 || guestOrder == 3) &&
                plugin.World.ReconnectPositions?.Settled != true) return false;
            if (guestOrder == 1)
            {
                recipe = argument;
                if (phase == 0)
                {
                    bool admitted = bunker.gameObject.activeInHierarchy && RecipeMachine().activeInHierarchy && bunker.isBunkerPowerOn && GuestSourcesReady();
                    string admission = AdmissionStatus();
                    if (admission != lastAdmission || Time.unscaledTime >= nextAdmissionLog)
                    {
                        Debug.Log("MONMP_PRODUCTION_ADMISSION: recipe=" + recipe + "; ready=" + admitted + "; " + admission);
                        lastAdmission = admission; nextAdmissionLog = Time.unscaledTime + 5;
                    }
                    if (!admitted) return false;
                    outputsBefore = Snapshot(Outputs[recipe], sharedOnly: true);
                    MoveNear(Field<Transform>(bunker, Anchors[recipe]).position);
                    phase = 1; next = Time.unscaledTime + 1.2f;
                }
                else if (phase == 1)
                {
                    Debug.Log("MONMP_PRODUCTION_CLICK: recipe=" + recipe + "; playerRange=" + Vector3.Distance(gameplay.PlayerWalking.transform.position, Field<Transform>(bunker, Anchors[recipe]).position) + "; " + AdmissionStatus());
                    ClickRecipe();
                    Require(!NativeSaveProduction.Pending(bunker, recipe == 3 ? "BuildFrenchFriesPack" : "BuildCannabisPack"), "Guest ran its own native bunker producer.");
                    Require(GuestSourcesReady(), "Guest native button debited local source replicas before host approval.");
                    phase = 2;
                }
            }
            else if (guestOrder == 2)
            {
                recipe = argument;
                var output = NewOutputs(Outputs[recipe], outputsBefore, sharedOnly: true);
                if (output.Length == 1 && GuestSourcesPaid())
                {
                    Require(items.TestState(items.TestBoundId(output[0])).Exists, "Guest output has no live authoritative record.");
                    Signal(2, (byte)recipe); guestOrder = 0;
                }
                else Require(output.Length <= 1, "Guest received duplicate native bunker outputs.");
            }
            else if (guestOrder == 3)
            {
                planterKind = (DealerMachineKind)argument;
                var dealer = plugin.World!.DealerMachines;
                var sale = dealer.TestDisplay(planterKind);
                if (sale == null || phase != 2 && (!sale.gameObject.activeInHierarchy || !Field<GameObject>(sale, "AllPlanters").activeSelf)) return false;
                if (phase == 0)
                {
                    var store = Field<Stores>(sale, "store");
                    int price = Mathf.RoundToInt(Field<float>(sale, "price") * 100f);
                    if (store == null || !store.isStoreOpen() || Object.FindObjectOfType<Cash_Manager>().GetCash < price ||
                        !dealer.TransactionsSettled || plugin.World.SaveGate.Active) return false;
                    if (planterClickAttempts == 0)
                    { cansBefore = Snapshot(typeof(Item_MapleSyrupCan), true); plantersBefore = Snapshot(typeof(Garden_OutdoorPlanter), true); }
                    MoveNear(sale.transform.position); phase = 1; next = Time.unscaledTime + 1.2f;
                }
                else if (phase == 1)
                {
                    if (Vector3.Distance(gameplay.PlayerWalking.transform.position, sale.transform.position) > 3.5f)
                    { MoveNear(sale.transform.position); next = Time.unscaledTime + .5f; return false; }
                    Require(dealer.TransactionsSettled, "A prior planter operation is still pending before the native click: " + PlanterStatus);
                    Require(planterClickAttempts++ < 3, "Safe planter admission retries were exhausted: " + PlanterStatus);
                    planterReply = null;
                    Debug.Log("MONMP_PRODUCTION_PLANTER_CLICK: " + PlanterStatus);
                    sale.MouseDown(); phase = 2;
                    Require(!dealer.TransactionsSettled, "The native planter click did not queue a purchase operation: " + PlanterStatus);
                }
                else if (phase == 2 && planterReply.HasValue && planterReply.Value.Status != PurchaseStatus.Placed)
                {
                    var status = planterReply.Value.Status;
                    Require(status == PurchaseStatus.Closed || status == PurchaseStatus.TooFar || status == PurchaseStatus.PriceChanged,
                        "The planter purchase did not have an explicitly safe rejection; do not retry: " + PlanterStatus);
                    // These host decisions precede every debit. Never replay a pending,
                    // missing, placed or uncertain result as a fresh purchase operation.
                    Require(dealer.TransactionsSettled, "The rejected planter operation did not finish before retry: " + PlanterStatus);
                    Debug.Log("MONMP_PRODUCTION_PLANTER_RETRY: explicit rejection=" + status + "; " + PlanterStatus);
                    planterReply = null; phase = 0; next = Time.unscaledTime + .5f;
                }
            }
            else if (guestOrder == 4)
            {
                var cans = NewOutputs(typeof(Item_MapleSyrupCan), cansBefore, true);
                var planters = NewOutputs(typeof(Garden_OutdoorPlanter), plantersBefore, true);
                if (cans.Length != 1 || planters.Length != argument) return false;
                if (plugin.World!.SugarShack.TestState.Syrup != 2 || plugin.World.SugarShack.TestState.Lids != 3 ||
                    planters.Any(value => !plugin.World.Garden.TestPlanterBound(items.TestBoundId(value)))) return false;
                Debug.Log("MONMP_PRODUCTION_GUEST: all four bunker recipes received exactly one shared output; complete paid planter batch and crimper source/output state received; native saved checkpoint readback is host evidence.");
                plugin.EndPairedTestSession(); Cleanup(); return true;
            }
            return false;
        }
        private void PreparePrerequisites()
        {
            var home = gameplay.home;
            var originalBunker = new DealerBunkerData(home)
            {
                Bunker_MainSwitchIsOn = bunker.Get_BunkerMainSwitchIsOn,
                isAutoWaterPlants = bunker.Get_isAutoWaterPlants,
                FertilizerAmounInReservoir = bunker.Get_FertilizerAmounInReservoir
            };
            SnapshotActive(bunker.gameObject); SnapshotActive(home.bunker_Bathtub.gameObject);
            foreach (var name in new[] { "Bathtub_Normal", "BathroomDoor_Broken", "BathroomDoor_Repaired" }) SnapshotActive(Field<GameObject>(home, name));
            foreach (var target in home.BunkerInConstruction_Objects) SnapshotActive(target);
            foreach (var name in new[] { "PackagerMachine_Big", "PackagerMachine_Huge", "BunkerFrenchFriesPress" }) SnapshotActive(Field<GameObject>(bunker, name));
            SnapshotActive(bunker.BunkerCompostMachine.gameObject);
            SnapshotField(home, "Bunker_DaySinceBuildStarted", 7);
            restore.Add(() => { if (bunker != null) bunker.LoadFromSave(originalBunker); });
            var hydro = Object.FindObjectOfType<Store_HydroSaintClin>();
            SnapshotField(hydro, "DaySinceBuildStarted", 5); SnapshotField(hydro, "HydroCut", false);
            RememberFixtureLine(ToDoList_Line.Dealer_BunkerBuilt);
            CompleteFixtureLine(ToDoList_Line.Dealer_BunkerBought);
            // Load an isolated completed-construction checkpoint through the native bunker
            // branch of Home.DealerBunkerLoadFromSave. Its separate dealer-stock reload
            // must not reconstruct unrelated display packs in this already loaded scene.
            home.UpdateBunkerConstruction();
            var checkpoint = new DealerBunkerData(home)
            {
                Bunker_DaySinceBuildStarted = 7,
                Bunker_MainSwitchIsOn = true,
                isAutoWaterPlants = false
            };
            bunker.LoadFromSave(checkpoint);
            Require(bunker.gameObject.activeInHierarchy && Object.FindObjectOfType<ToDoList_Manager>().isDone(ToDoList_Line.Dealer_BunkerBuilt),
                "Native completed-construction load did not unlock the bunker.");
            Require(bunker.isBunkerPowerOn, "Fixture could not establish native bunker power.");
            prerequisitesPrepared = true;
            Debug.Log("MONMP_PRODUCTION_PREREQUISITES: isolated native completed-construction load; bunker built=True; native power=True; automatic watering=False; machine upgrades follow recipe order");
        }
        private void PrepareRecipe()
        {
            if (recipe == 1) { CompleteFixtureLine(ToDoList_Line.Dealer_HugePackagerBought); bunker.HugePackager_Bought(); }
            if (recipe == 3) { CompleteFixtureLine(ToDoList_Line.Dealer_FrenchFriesPressBought); bunker.FrenchFriesPress_Bought(); }
            Require(RecipeMachine().activeInHierarchy, "Native recipe machine is unavailable; use a clean isolated save for the original big-pack recipe.");
            inputs.Clear(); outputsBefore = Snapshot(Outputs[recipe]);
            var scan = Field<Transform>(bunker, "Packagers_SpawnBigPack").position;
            Require(Physics.OverlapSphere(scan, 6f).Select(value => value.GetComponent<InteractableItems>()).Where(value => value != null).Distinct().All(value =>
                !(value is Item_WeedPreRollPack) && !(value is Item_WeedPreRollBigPack) && !(value is Tool_HarvestBasket basket && basket.Value1 > 0)), "Fixture input area contains unrelated recipe stock; preserve it and use a clean isolated save.");
            int count = recipe == 2 ? 27 : recipe == 3 ? 2 : 1;
            for (int i = 0; i < count; i++)
            {
                Type type = recipe == 2 ? typeof(Item_WeedPreRollBigPack) : typeof(Tool_HarvestBasket);
                var offset = recipe == 3 ? new Vector3(i * 1.25f - .6f, .8f, .4f) : new Vector3((i % 6) * .25f - .6f, .8f + i / 6 * .3f, .4f);
                var item = plugin.World!.Items.TestSpawn(scan + offset, plugin.World.Items.TestPrefabs(type)[0]);
                item.isSpawnFromTheSaveLoadSystem = true; item.CreateBasics();
                // SharedItems correctly makes unowned host items dynamic. Constraints keep
                // only these disposable recipe inputs fixed without changing native scan
                // colliders, values or production after admission.
                if (item.rb != null) { item.rb.constraints = RigidbodyConstraints.FreezeAll; item.rb.isKinematic = true; }
                if (item is Tool_HarvestBasket) { item.IntValue1 = (int)(recipe == 3 ? SeedsType.Potato : SeedsType.Cannabis); item.Value1 = 100; }
                inputs.Add(item); fixtures.Add(item);
            }
            Physics.SyncTransforms(); plugin.World!.Items.DiscoverSoon(); recipePrepared = true;
            Debug.Log("MONMP_PRODUCTION_FIXTURE_FROZEN: recipe=" + recipe + "; native inputs=" + count + "; fixture values will only be changed by production until cleanup");
        }
        private void ClickRecipe()
        {
            if (recipe == 0) bunker.Button_BigPackMachine();
            else if (recipe == 1) bunker.Button_HugePackMachine(false);
            else if (recipe == 2) bunker.Button_HugePackMachine(true);
            else bunker.Button_FrenchFriesPress();
        }
        private GameObject RecipeMachine() => Field<GameObject>(bunker, recipe == 0 ? "PackagerMachine_Big" : recipe == 3 ? "BunkerFrenchFriesPress" : "PackagerMachine_Huge");
        private bool InputsPaid() => recipe == 2 ? inputs.All(value => value == null || value.gameObject == null) : inputs.All(value => value != null && value.Value1 == 0);
        private IEnumerable<InteractableItems> GuestSources() => SceneItems().Where(value => plugin.World!.Items.TestBoundId(value) != 0 &&
            Vector3.Distance(value.transform.position, Field<Transform>(bunker, "Packagers_SpawnBigPack").position) < 6 &&
            (recipe == 2 ? value is Item_WeedPreRollBigPack : value is Tool_HarvestBasket && value.IntValue1 == (int)(recipe == 3 ? SeedsType.Potato : SeedsType.Cannabis)));
        private bool GuestSourcesReady() => recipe == 2 ? GuestSources().Count() == 27 : GuestSources().Count(value => value.Value1 == 100) == (recipe == 3 ? 2 : 1);
        private bool GuestSourcesPaid() => recipe == 2 ? !GuestSources().Any() : GuestSources().Count(value => value.Value1 == 0) == (recipe == 3 ? 2 : 1);
        private string DescribeInput(InteractableItems value)
        {
            if (value == null) return "destroyed";
            ulong id = plugin.World?.Items.TestBoundId(value) ?? 0;
            return id.ToString("X") + "/" + value.GetType().Name + "/seed=" + value.IntValue1 + "/amount=" + value.Value1 +
                "/range=" + Vector3.Distance(value.transform.position, Field<Transform>(bunker, "Packagers_SpawnBigPack").position).ToString("F2") +
                "/collider=" + (value.itemCollider != null && value.itemCollider.enabled) +
                "/constraints=" + (value.rb != null ? value.rb.constraints.ToString() : "none");
        }
        private string DescribePublishedOutput(InteractableItems value)
        {
            if (value == null) return "destroyed native output";
            var world = plugin.World!;
            ulong id = world.Items.TestBoundId(value);
            bool record = id != 0 && world.Channel.Records.TryGetValue(id, out var state) && state.Adapter == ItemState.Adapter;
            return value.GetType().Name + "/" + value.PrefabName + "/id=" + id.ToString("X") +
                "/record=" + record + "/body=" + (value.rb != null) + "/active=" + value.gameObject.activeInHierarchy +
                "/garden=" + (!(value is Garden_OutdoorPlanter) || id != 0 && world.Garden.TestPlanterBound(id));
        }
        private string AdmissionStatus()
        {
            if (recipe < 0 || recipe >= Outputs.Length || bunker == null || plugin.World == null) return "not an attached bunker recipe";
            var machine = RecipeMachine();
            var scan = Field<Transform>(bunker, "Packagers_SpawnBigPack").position;
            var stock = SceneItems().Where(value => value is Tool_HarvestBasket || value is Item_WeedPreRollBigPack)
                .Where(value => Vector3.Distance(value.transform.position, scan) < 12).ToArray();
            return "root=" + bunker.gameObject.activeInHierarchy + "; power=" + (bunker.gameObject.activeInHierarchy && bunker.isBunkerPowerOn) +
                "; machineSelf/hierarchy=" + machine.activeSelf + "/" + machine.activeInHierarchy +
                "; settled=" + (plugin.World?.ReconnectPositions?.Settled == true) + "; sourcesReady=" + GuestSourcesReady() +
                "; expected=" + (recipe == 2 ? "27 big packs" : recipe == 3 ? "2 potato baskets at 100" : "1 cannabis basket at 100") +
                "; nearby=" + string.Join(" | ", stock.Select(DescribeInput));
        }
        private void DestroyCurrentRecipe()
        {
            foreach (var item in inputs.Concat(NewOutputs(Outputs[recipe], outputsBefore)).Distinct()) if (item != null) { item.gameObject.SetActive(false); Object.Destroy(item.gameObject); }
            inputs.Clear(); recipePrepared = false; plugin.World!.Items.DiscoverSoon();
        }
        private string EmptyPrefabName = "";
        private void PrepareSaveInputs()
        {
            shack = plugin.World!.SugarShack.TestShack!;
            if (shack == null) throw new InvalidOperationException("Production settlement: Missing native sugar shack.");
            crimper = Field<SugarShack_CrimpingMachine>(shack, "sugarShack_CrimpingMachine");
            Require(!Field<bool>(crimper, "isWorking"), "Preserve an already-running native crimper batch.");
            for (var at = crimper.transform; at != null; at = at.parent)
            {
                var target = at.gameObject; bool active = target.activeSelf;
                if (!active) { restore.Add(() => { if (target != null) target.SetActive(active); }); target.SetActive(true); }
            }
            CompleteFixtureLine(ToDoList_Line.Sugarshack_SugarshackIsBuild);
            CompleteFixtureLine(ToDoList_Line.Dealer_BunkerBuilt);
            CompleteFixtureLine(ToDoList_Line.SugarShack_CrimpingMachine);
            shack.CrimpingMachine_Bought();
            var plateau = Field<Transform>(crimper, "UpDownPlateau");
            // The supported native plateau has a solid BoxCollider. InteractableItems.Grab
            // supplies the triggering CAN collider; neither Start nor a stock update turns
            // the machine into a trigger. Retain that exact native receiver geometry.
            crimperReception = plateau.GetComponent<Collider>();
            Require(crimperReception != null && crimperReception.enabled && crimperReception.gameObject.activeInHierarchy &&
                crimperReception.GetComponentInParent<SugarShack_CrimpingMachine>() == crimper,
                "The native UpDownPlateau has no active receiver collider.");
            var previousCan = Field<Item_MapleSyrupCan>(crimper, "LastCastSpawned");
            Require(previousCan == null || previousCan.transform.parent != plateau, "Preserve the existing finished can on the native crimper plateau.");
            Require(shack.Get_HomeGenerator != null && shack.Get_HomeGenerator.Get_isEngineRunning,
                "Prepared hydro checkpoint did not power the native crimper.");
            int syrup = shack.Get_SyrupLevel, lids = shack.Get_CrimpingMachine_LidsAmount;
            restore.Add(() => { if (shack != null) { shack.Get_SyrupLevel = syrup; shack.Get_CrimpingMachine_LidsAmount = lids; } });
            shack.Get_SyrupLevel = 3; shack.Get_CrimpingMachine_LidsAmount = 4;
            cansBefore = Snapshot(typeof(Item_MapleSyrupCan)); plantersBefore = Snapshot(typeof(Garden_OutdoorPlanter));
            canBaseline = cansBefore.Count; planterBaseline = plantersBefore.Count; emptyBaseline = Snapshot(typeof(Item_MapleSyrupCan_Empty)).Count; saveBaselinesCaptured = true;
            planterSale = Enumerable.Range(4, 4).Select(value => plugin.World.DealerMachines.TestDisplay((DealerMachineKind)value))
                .Where(value => value != null && value.gameObject.activeInHierarchy && Field<GameObject>(value!, "AllPlanters").activeSelf && Field<int>(value!, "AmountOfPlanter") > 1).OrderBy(value => Field<int>(value!, "AmountOfPlanter")).FirstOrDefault()!;
            if (planterSale == null) throw new InvalidOperationException("Production settlement: No unpurchased native multipack planter listing is available.");
            expectedPlanters = Field<int>(planterSale, "AmountOfPlanter");
            planterKind = SelectedPlanterKind(); nativePlanterBaseline = plugin.World.DealerMachines.TestNativePurchases;
            var store = Field<Stores>(planterSale, "store");
            PrepareDealerHours(store);
            var stack = Field<GameObject>(planterSale, "AllPlanters"); bool stackActive = stack.activeSelf;
            restore.Add(() => { if (stack != null) stack.SetActive(stackActive); });
            var cash = Object.FindObjectOfType<Cash_Manager>(); int priorCash = cash.GetCash;
            restore.Add(() => AccessTools.Field(typeof(Cash_Manager), "cash").SetValue(cash, priorCash));
            priceCents = Mathf.RoundToInt(Field<float>(planterSale, "price") * 100f);
            cashBefore = Math.Max(priorCash, priceCents + 100000); AccessTools.Field(typeof(Cash_Manager), "cash").SetValue(cash, cashBefore);
            // Keep the held trigger outside the machine until the guest's planter debit is
            // pending, so the save request still exercises both paid pre-output intervals.
            emptyCan = (Item_MapleSyrupCan_Empty)plugin.World.Items.TestSpawn(crimper.transform.position + Vector3.back * 5 + Vector3.up * .6f,
                plugin.World.Items.TestPrefabs(typeof(Item_MapleSyrupCan_Empty))[0]);
            emptyCan.isSpawnFromTheSaveLoadSystem = true; emptyCan.CreateBasics();
            if (emptyCan.rb != null) { emptyCan.rb.constraints = RigidbodyConstraints.FreezeAll; emptyCan.rb.isKinematic = true; }
            fixtures.Add(emptyCan); EmptyPrefabName = emptyCan.PrefabName; plugin.World.Items.DiscoverSoon();
        }
        private void PrepareDealerHours(Stores store)
        {
            var clock = plugin.World!.Clock;
            var before = clock.TestNative;
            var sky = UniStormSystem.Instance;
            var realTime = sky.RealWorldTime;
            Require(!before.FastForwardRunning && store.StoreOpeningHour < store.StoreClosingHour && store.StoreDaysOpen[before.Weekday] == 1,
                "No ordinary native dealer opening interval is available for this fixture.");
            restore.Add(() => { clock.TestRestoreFixtureClock(before); sky.RealWorldTime = realTime; });
            int minute = (store.StoreOpeningHour + store.StoreClosingHour) / 2;
            clock.TestRestoreFixtureClock(new ClockState((minute + .25f) / 1440f, before.Year, before.Month, before.Day, before.Weekday,
                before.ElapsedDays, before.TotalTicks, false, before.ComputerTicks, before.ComputerUser, before.SleepTicks, before.RefillAt,
                before.RefillComputerTicks, before.RefillSleepTicks, before.FastForwardStart, before.FastForwardTarget, before.FastForwardRunning));
            sky.RealWorldTime = UniStormSystem.EnableFeature.Disabled;
            bool wasOpen = store.isOpen;
            restore.Add(() => { if (store != null) store.isOpen = wasOpen; });
            store.OpenStore();
            Require(store.isStoreOpen() && clock.TestNative.TotalTicks == before.TotalTicks,
                "Native dealer-hours preparation changed accounting ticks or failed to open the store.");
            Debug.Log("MONMP_PRODUCTION_DEALER_HOURS: minute=" + clock.TestNative.MinuteOfDay + "; totalTicks=" + before.TotalTicks +
                "; calendar/counters preserved; native OpenStore; UniStorm flow paused; Unity production timers unchanged");
        }
        private DealerMachineKind SelectedPlanterKind() => Enumerable.Range(4, 4).Select(value => (DealerMachineKind)value).Single(value => ReferenceEquals(plugin.World!.DealerMachines.TestDisplay(value), planterSale));
        private void CompleteFixtureLine(ToDoList_Line line)
        {
            RememberFixtureLine(line);
            var todo = Object.FindObjectOfType<ToDoList_Manager>(); var detail = todo.Get_Lines[line];
            detail.CheckLine();
        }
        private void RememberFixtureLine(ToDoList_Line line)
        {
            if (!originalProgress.ContainsKey(line)) originalProgress.Add(line, Object.FindObjectOfType<ToDoList_Manager>().Get_Lines[line].value);
        }
        private void SnapshotActive(GameObject target)
        {
            bool active = target.activeSelf;
            restore.Add(() => { if (target != null) target.SetActive(active); });
        }
        private static T Field<T>(object target, string name) => (T)AccessTools.Field(target.GetType(), name).GetValue(target);
        private void SnapshotField<T>(object target, string name, T value)
        {
            var field = AccessTools.Field(target.GetType(), name); var previous = field.GetValue(target);
            restore.Add(() => field.SetValue(target, previous)); field.SetValue(target, value);
        }
        private static InteractableItems[] SceneItems() => Object.FindObjectsOfType<InteractableItems>(true).Where(value => value != null && value.gameObject.scene.IsValid() && value.gameObject.activeInHierarchy).ToArray();
        private HashSet<InteractableItems> Snapshot(Type type, bool sharedOnly = false) => new HashSet<InteractableItems>(SceneItems().Where(value => value.GetType() == type && (!sharedOnly || plugin.World!.Items.TestBoundId(value) != 0)));
        private InteractableItems[] NewOutputs(Type type, HashSet<InteractableItems> baseline, bool sharedOnly = false) => Snapshot(type, sharedOnly).Where(value => !baseline.Contains(value)).ToArray();
        private void MoveNear(Vector3 target)
        {
            var at = target + Vector3.back * 1.7f;
            gameplay.PlayerWalking.transform.position = at; gameplay.Get_Player_Rigidbody.position = at; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }
        internal void Cleanup()
        {
            if (cleaned) return; cleaned = true;
            pendingOrder = 0; pendingAckSequence = 0;
            if (ReferenceEquals(nativeObserver, this)) nativeObserver = null;
            nativeObservationPatch?.UnpatchSelf(); nativeObservationPatch = null;
            fixtureChannel.CommandRequested -= OnCommand; fixtureChannel.ReplyReceived -= OnReply;
            if (plugin.TestOptions.IsHost)
            {
                // Never abandon already-paid native work when a diagnostic assertion fails.
                if (plugin.World != null) { plugin.World.RemoteClicks.SettleForSave(); plugin.World.DealerMachines.SettleForSave(); plugin.World.SugarShack.SettleForSave(); }
                if (emptyCan != null && gameplay.Player.Get_HoldingItem == emptyCan) gameplay.Player.CheckIfHoldingItemToCancel(emptyCan);
                if (recipePrepared && recipe < Outputs.Length) fixtures.AddRange(NewOutputs(Outputs[recipe], outputsBefore));
                if (saveBaselinesCaptured) { fixtures.AddRange(NewOutputs(typeof(Item_MapleSyrupCan), cansBefore)); fixtures.AddRange(NewOutputs(typeof(Garden_OutdoorPlanter), plantersBefore)); }
                foreach (var item in fixtures.Distinct()) if (item != null) Object.Destroy(item.gameObject);
                for (int i = restore.Count - 1; i >= 0; i--) restore[i]();
                Array.Copy(originalStatistics, GameManager.Get_PlayerStats, originalStatistics.Length);
                var todo = Object.FindObjectOfType<ToDoList_Manager>();
                foreach (var value in originalProgress) { todo.Get_Lines[value.Key].value = value.Value; todo.Get_Lines[value.Key].UpdateStrike(); }
                if (prerequisitesPrepared || preparedSaveName != null) { LocalTestIsolation.SetString("SaveName", originalSave); LocalTestIsolation.SetInt("SaveTimestamp", originalTimestamp); }
            }
            gameplay.PlayerWalking.transform.position = originalPosition; gameplay.Get_Player_Rigidbody.position = originalPosition; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
            gameplay.Player.mainCamera.transform.rotation = originalCamera;
        }
        private static void Require(bool ok, string reason) { if (!ok) throw new InvalidOperationException("Production settlement: " + reason); }
    }
}
