using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using MonMP.Plugin.Diagnostics;
using NWH.VehiclePhysics2.VehicleGUI;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // The sugar shack production line. The host's shack is authoritative: only it runs the native minute
    // tick (vacuum, tank-to-evaporator valve, heating, syrup, barrel and bed-tank filling) and the daily
    // tree efficiency roll. Guests show the host's levels, gauges, valves, switches and running effects,
    // and follow its build (logs on the palette, then the finished shack) and purchased upgrades. A
    // guest's valve or switch becomes a host command; a guest's evaporator fire is sent as heat. The
    // crimping machine and the log reception run on the host, fed by the replicas of guests' items.
    internal sealed class SharedSugarShack : IDisposable
    {
        private static FieldInfo F(string name) => AccessTools.Field(typeof(SugarShack), name);
        internal static readonly string[] BoolFields = { "Valve_SyrupToBarril", "isAutoElectricHeaterOn", "isOutsidePumpOn", "SOUND_VacuumPumpIsRunning", "SOUND_RunningWater_TankToEvap",
            "SOUND_RunningWater_SyrupToBaril", "CheminerSmoke_Activated", "HeatDistortion_Active", "ReverseOsmosisInstalled", "WaterTankInstalled", "WaterTank50kInstalled" };
        internal static readonly string[] ObjectFields = { "ElectricAutoHeater", "Siroptier480L", "TapWater_Particle", "TapSyrup_Particle", "CheminerSmoke_Particle" };
        private static readonly FieldInfo ValveSyrup = F("Valve_SyrupToBarril"), Heater = F("isAutoElectricHeaterOn"), Pump = F("isOutsidePumpOn");
        private static readonly FieldInfo Vacuum = F("SOUND_VacuumPumpIsRunning"), TapWaterOn = F("SOUND_RunningWater_TankToEvap"), TapSyrupOn = F("SOUND_RunningWater_SyrupToBaril");
        private static readonly FieldInfo Smoke = F("CheminerSmoke_Activated"), Boiling = F("HeatDistortion_Active");
        private static readonly FieldInfo Osmosis = F("ReverseOsmosisInstalled"), Tank = F("WaterTankInstalled"), Tank50k = F("WaterTank50kInstalled");
        private static readonly FieldInfo HeaterObject = F("ElectricAutoHeater"), Siroptier480 = F("Siroptier480L"), Crimper = F("sugarShack_CrimpingMachine"), OutsidePump = F("PumpSyrupOutside");
        private static readonly FieldInfo WaterValve = F("Valve_WaterTank");
        private static readonly FieldInfo HeaterSwitch = F("ElectricAutoHeater_Switch"), PumpSwitch = F("OutsidePump_Switch");
        private static readonly FieldInfo TapWater = F("TapWater_Particle"), TapSyrup = F("TapSyrup_Particle"), SmokeParticle = F("CheminerSmoke_Particle"), Distortion = F("HeatDistortion_Particles");
        private static readonly FieldInfo Sound = F("SOUND_SugarShack"), Temperature = F("WaterTempInEvap"), Trees = F("AmountOfTreeConnected");
        private static readonly FieldInfo TempGauge = F("Gauge_WaterTemp"), FireGauge = F("Gauge_FirePower");
        private static readonly FieldInfo SyrupValveOwner = AccessTools.Field(typeof(SugarShack_Valve_SyrupToBaril), "SugarShack");
        private static readonly MethodInfo HeaterLight = AccessTools.Method(typeof(SugarShack), "ElectricHeater_UpdateLight");
        private static readonly MethodInfo PumpLight = AccessTools.Method(typeof(SugarShack), "OutsidePump_UpdateLight");
        private static readonly MethodInfo GlowLight = AccessTools.Method(typeof(SugarShack), "Tick_GlowLight");
        private static readonly MethodInfo NativeTick = AccessTools.Method(typeof(SugarShack), "Tick", new[] { typeof(int) });
        private static readonly MethodInfo SyrupValveVisual = AccessTools.Method(typeof(SugarShack_Valve_SyrupToBaril), "updateVisual", new[] { typeof(bool) });
        private static readonly FieldInfo SalePrice = AccessTools.Field(typeof(ItemForSale_SugarShack), "price");
        private static readonly FieldInfo SaleStore = AccessTools.Field(typeof(ItemForSale_SugarShack), "store");
        private static readonly FieldInfo CrimperWorking = AccessTools.Field(typeof(SugarShack_CrimpingMachine), "isWorking");
        private static readonly FieldInfo WaterCapacity = F("Capacity_Watertank"), SyrupCapacity = F("Capacity_Siroptier");
        private static readonly FieldInfo SlabAmount = AccessTools.Field(typeof(SugarShack_SlabBuild), "AmountOnPalette");
        private static readonly FieldInfo SlabLogs = AccessTools.Field(typeof(SugarShack_SlabBuild), "LogsStartLocation");
        private static readonly FieldInfo SlabShack = AccessTools.Field(typeof(SugarShack_SlabBuild), "SugarShack");
        private static readonly FieldInfo SlabCube = AccessTools.Field(typeof(SugarShack_SlabBuild), "CubeToClearTheSpace");
        private static readonly FieldInfo SlabPalette = AccessTools.Field(typeof(SugarShack_SlabBuild), "Palette");
        private static readonly FieldInfo SlabDust = AccessTools.Field(typeof(SugarShack_SlabBuild), "ParticleDustOnCollision");
        private static readonly FieldInfo SlabReception = AccessTools.Field(typeof(SugarShack_SlabBuild), "ReceptionLocation");
        private const float BuildClearRadius = 20, BuildComeBackRadius = 18, LogMoveSeconds = 1, ClaimedLogSeconds = 2;
        private static readonly string[] StructureObjects = { "ReverseOsmosis", "Pipe_TankToEvap", "WaterTankUpgrade", "WaterTank50kUpgrade",
            "ElectricAutoHeater", "Siroptier", "Siroptier480L", "sugarShack_CrimpingMachine", "PumpSyrupOutside" };
        private static readonly string[] SlabObjects = { "Palette", "ReceptionLocation", "ParticleDustOnCollision", "CubeToClearTheSpace" };
        private static readonly (string Field, ShackUpgrades Upgrade)[] SaleFlags = {
            ("isReverseOsmosis", ShackUpgrades.ReverseOsmosis), ("isWaterTank", ShackUpgrades.WaterTank),
            ("IsWaterTank50k", ShackUpgrades.WaterTank50k), ("isElectricAutoHeater", ShackUpgrades.Heater),
            ("isSiroptier", ShackUpgrades.Siroptier), ("isCrimpingMachine", ShackUpgrades.Crimping),
            ("isBedTankOutdoorPump", ShackUpgrades.OutdoorPump) };
        private const float CommandRange = 30, PressInterval = 0.3f;
        private readonly SugarShack? shack;
        private readonly SugarShack_SlabBuild? slab;
        private readonly SugarShack_Valve_SyrupToBaril? syrupValve;
        private readonly Dictionary<ShackUpgrades, ItemForSale_SugarShack> sales = new Dictionary<ShackUpgrades, ItemForSale_SugarShack>();
        private readonly Dictionary<ItemForSale_SugarShack, ShackUpgrades> saleTypes = new Dictionary<ItemForSale_SugarShack, ShackUpgrades>();
        private readonly PurchaseClaims purchaseClaims = new PurchaseClaims();
        private readonly Dictionary<(Guid Profile, Guid Operation), SugarUpgradeReply> purchaseResults = new Dictionary<(Guid, Guid), SugarUpgradeReply>();
        private readonly Dictionary<Guid, float> pendingPurchases = new Dictionary<Guid, float>();
        private readonly HashSet<Guid> timedOutPurchases = new HashSet<Guid>();
        private readonly Dictionary<GameObject, bool> originalStructure = new Dictionary<GameObject, bool>();
        private readonly List<GameObject> addedPaletteLogs = new List<GameObject>();
        private readonly List<(uint Peer, Guid Profile, Guid Operation, Part_Spoiler_BedTank Item, float Deadline)> deliveries =
            new List<(uint, Guid, Guid, Part_Spoiler_BedTank, float)>();
        private readonly Gameplay gameplay;
        private readonly SharedItems items;
        private readonly ulong id;
        private readonly bool host;
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Dictionary<(uint, ShackOp), float> lastPress = new Dictionary<(uint, ShackOp), float>();
        private readonly Dictionary<uint, (int Tick, float Heat)> heatBudget = new Dictionary<uint, (int, float)>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private SugarShackState state, original;
        private bool applying, disposed, applied;
        private bool uncertainPurchase;
        private int originalWaterCapacity, originalSyrupCapacity, originalPalette;
        private float nextCapture, nextHeat, nextGlow, nextPresentation, reapplyAt, pendingHeat;
        private int hostTicks;
        // Build ceremony. The host's phase goes out in the record; guests run the move-back prompts and
        // a presentation-only copy of the native build animation from it.
        private ShackBuildPhase buildPhase;
        private Coroutine? guestBuild;
        private bool guestAnimating;
        private float nextBuildMessage;
        private SOUND_VFX? guestBuildSound;
        // Guest palette presentation: removed shared logs claimed at the reception circle, and palette
        // increases that were animated from the circle before the log's removal arrived.
        private readonly List<(GameObject Log, float Expires)> claimedLogs = new List<(GameObject, float)>();
        private readonly List<float> unmatchedPlaceholders = new List<float>();
        private readonly List<Coroutine> logMoves = new List<Coroutine>();
        public int Count => shack != null ? 1 : 0;
        public bool NewPurchasesBlocked { get; set; }
        internal bool ProductionSettled => !host || shack == null || !(Crimper.GetValue(shack) is SugarShack_CrimpingMachine crimper) ||
            crimper == null || !(bool)CrimperWorking.GetValue(crimper)!;
        public bool TransactionsSettled => !uncertainPurchase && pendingPurchases.Count == 0 && timedOutPurchases.Count == 0 && deliveries.Count == 0 && ProductionSettled;

        internal bool SettleForSave()
        {
            if (ProductionSettled) return true;
            var crimper = (SugarShack_CrimpingMachine)Crimper.GetValue(shack)!;
            bool finished = NativeSaveProduction.Finish(crimper, "Working") && ProductionSettled;
            items.DiscoverSoon();
            return finished;
        }

#if MONMP_DEVELOPMENT
        internal SugarUpgradeReply? TestLastPurchase { get; private set; }
#endif

        private bool Ready => !disposed && session?.IsRunning == true && channel?.Ready == true && shack != null;
        // Guests leave the minute tick, the daily roll, crimping and log reception to the host's shack.
        internal bool GuestBlocks => !host && !applying && !disposed && session?.IsRunning == true;
        public SharedSugarShack(Gameplay gameplay, bool host, SharedItems items, string diagnostics, NativeEntityRegistry identities)
        {
            this.gameplay = gameplay; this.host = host; this.items = items;
            shack = Object.FindObjectsOfType<SugarShack>(true).FirstOrDefault(s => s.gameObject.scene.IsValid());
            slab = Object.FindObjectsOfType<SugarShack_SlabBuild>(true).FirstOrDefault(s => s.gameObject.scene.IsValid());
            string line = "none";
            if (shack != null)
            {
                syrupValve = Object.FindObjectsOfType<SugarShack_Valve_SyrupToBaril>(true).FirstOrDefault(v => SyrupValveOwner.GetValue(v) as SugarShack == shack);
                string identity = RuntimeProbe.CanonicalPath(shack.transform) + "/sugar-shack-v1";
                id = StaticId.Hash(identity);
                state = original = Capture();
                if (!host) CaptureStructure();
                identities.Bind(id, SugarShackState.Adapter, shack.gameObject, identity, true, EntityPersistence.SessionOnly);
                line = id.ToString("X16") + "\t" + identity + "\t" + state.Flags + "\t" + state.Upgrades + "\ttank=" + state.Tank + "\tsyrup=" + state.Syrup;
            }
            foreach (var sale in Object.FindObjectsOfType<ItemForSale_SugarShack>(true).Where(x => x.gameObject.scene.IsValid()))
            {
                var matches = SaleFlags.Where(f => (bool)AccessTools.Field(typeof(ItemForSale_SugarShack), f.Field).GetValue(sale)).ToArray();
                if (matches.Length != 1 || sales.ContainsKey(matches[0].Upgrade))
                    throw new InvalidOperationException("Ambiguous sugar shack upgrade listing: " + RuntimeProbe.CanonicalPath(sale.transform));
                sales.Add(matches[0].Upgrade, sale); saleTypes.Add(sale, matches[0].Upgrade);
            }

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-sugar-shack.tsv"), new[] { "id\tidentity\tflags\tupgrades\tlevels", line });


            File.WriteAllLines(Path.Combine(diagnostics, "shared-sugar-upgrades.tsv"),
                new[] { "upgrade\tprice\tpath" }.Concat(sales.Select(p => p.Key + "\t" + Price(p.Value) + "\t" + RuntimeProbe.CanonicalPath(p.Value.transform))));
#endif

        }
        public IEnumerable<WorldRecord> InitialRecords()
        {
            if (shack != null) yield return new WorldRecord(id, 1, SugarShackState.Adapter, state.Encode());
        }
        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.CommandRequested += Command; channel.CommandRejected += Rejected; channel.ReplyReceived += PurchaseReply;
            channel.ReplyReceived += NativeReply;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
        }
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer) { presence.Remove(peer); foreach (var key in lastPress.Keys.Where(k => k.Item1 == peer).ToArray()) lastPress.Remove(key); heatBudget.Remove(peer); }
        private static float Price(ItemForSale_SugarShack sale) => (float)SalePrice.GetValue(sale);
        internal ItemForSale_SugarShack? HoverSale { get; set; }
        internal bool CanQuote(float price) => Ready && channel?.FullySynchronized == true && HoverSale != null &&
            saleTypes.ContainsKey(HoverSale) && Price(HoverSale) == price;
        private static ToDoList_Line Line(ShackUpgrades upgrade) => upgrade switch
        {
            ShackUpgrades.ReverseOsmosis => ToDoList_Line.SugarShack_ReverseOsmosis,
            ShackUpgrades.WaterTank => ToDoList_Line.SugarShack_WaterTank,
            ShackUpgrades.WaterTank50k => ToDoList_Line.SugarShack_WaterTank50k,
            ShackUpgrades.Heater => ToDoList_Line.SugarShack_ElectricAutoHeater,
            ShackUpgrades.Siroptier => ToDoList_Line.SugarShack_Siroptier,
            ShackUpgrades.Crimping => ToDoList_Line.SugarShack_CrimpingMachine,
            ShackUpgrades.OutdoorPump => ToDoList_Line.SugarShack_BedTankOutdoorPump,
            _ => throw new InvalidDataException("Unknown sugar upgrade")
        };
        // True means the guest attempt was handled and native purchase must not run locally.
        internal bool Purchase(ItemForSale_SugarShack sale)
        {
            if (disposed || session?.IsRunning != true) return false;
            if (host)
            {
                if (!NewPurchasesBlocked) return false;
                MonMPPlugin.Instance!.ReportItemUseUnavailable("Wait for the multiplayer save to finish before buying.");
                return true;
            }
            if (!saleTypes.TryGetValue(sale, out var upgrade) || !Ready || channel?.FullySynchronized != true)
            { MonMPPlugin.Instance!.ReportItemUseUnavailable("This sugar shack upgrade is unavailable to guests."); return true; }
            if (NewPurchasesBlocked || pendingPurchases.Count + timedOutPurchases.Count >= 8)
            { MonMPPlugin.Instance!.ReportItemUseUnavailable("Wait for pending purchases or the multiplayer save to finish."); return true; }
            var request = new SugarUpgradeRequest(Guid.NewGuid(), upgrade, Price(sale));
            pendingPurchases.Add(request.Operation, Time.unscaledTime + 10);
            items.NotePurchaseView();
            // The shack's fluid and heat records change frequently. A purchase validates current
            // price and prerequisites on the host, so it does not need a matching record revision.
            channel!.Request(id, request.Encode(), true); return true;
        }
        private void PurchaseReply(ulong entity, byte[] payload)
        {
            if (host || entity != id || !SugarUpgradeReply.Is(payload)) return;
            var reply = SugarUpgradeReply.Decode(payload);
            if (!pendingPurchases.Remove(reply.Operation) && !timedOutPurchases.Remove(reply.Operation)) return;

#if MONMP_DEVELOPMENT
            TestLastPurchase = reply;
#endif

            if (reply.Status == PurchaseStatus.Uncertain) uncertainPurchase = true;
            if (reply.Status == PurchaseStatus.Placed || reply.Status == PurchaseStatus.Held)
            {
                // The native counter's confirmation; the debit animation and ToDo check follow the
                // shared cash and checklist records.
                try
                {
                    string text = TEXT.GetText("ERROR_FederationSyrup_MachineBought") + (reply.Status == PurchaseStatus.Held
                        ? "\n\n<b><i>" + TEXT.GetText("FederationSyrup_BedTankBoughtInstruction") + "</i></b>" : "</i></b>");
                    Singleton<UI_EventMessage>.i.Show(text, TEXT.GetText("SyrupFederation_Name"), 15f);
                }
                catch (Exception ex) { Debug.LogWarning("MonMP syrup federation message failed: " + ex.Message); }
                return;
            }
            MonMPPlugin.Instance!.ReportItemUseUnavailable(reply.Status switch
            {
                PurchaseStatus.Closed => "The syrup federation is closed.",
                PurchaseStatus.Funds => "Not enough shared cash.",
                PurchaseStatus.TooFar => "Move closer to the upgrade counter.",
                PurchaseStatus.PriceChanged => "The upgrade listing changed. Try again.",
                PurchaseStatus.Unavailable => "That upgrade is unavailable or already installed.",
                PurchaseStatus.Uncertain => "Upgrade outcome is uncertain. Check the shack and cash before retrying.",
                _ => "The sugar shack upgrade was declined."
            });
        }
        private void ExpirePurchases(float now)
        {
            foreach (var entry in pendingPurchases.Where(p => now >= p.Value).ToArray())
            {
                pendingPurchases.Remove(entry.Key);
                timedOutPurchases.Add(entry.Key);
                MonMPPlugin.Instance!.ReportItemUseUnavailable("Upgrade confirmation is delayed. Check cash and the shack before retrying.");
            }
        }
        private void Rejected(uint peer, ulong entity, byte[] payload) => PurchaseCommand(peer, entity, payload, true);
        private void PurchaseCommand(uint peer, ulong entity, byte[] payload, bool stale)
        {
            if (!host || peer == 0 || entity != id || !SugarUpgradeRequest.Is(payload) || session == null) return;
            var request = SugarUpgradeRequest.Decode(payload);
            if (!session.Peers.TryGetValue(peer, out var buyer)) return;
            var key = (buyer.ProfileId, request.Operation);
            if (!purchaseClaims.Claim(key.ProfileId, key.Operation))
            {
                if (purchaseResults.TryGetValue(key, out var prior)) channel!.Reply(peer, id, prior.Encode());
                else if (!deliveries.Any(d => d.Profile == key.ProfileId && d.Operation == key.Operation))
                    channel!.Reply(peer, id, new SugarUpgradeReply(request.Operation, PurchaseStatus.Capacity).Encode());
                return;
            }
            void Finish(PurchaseStatus status)
            {
                var reply = new SugarUpgradeReply(request.Operation, status);
                purchaseResults[key] = reply; channel!.Reply(peer, id, reply.Encode());
            }
            if (stale) { Finish(PurchaseStatus.PriceChanged); return; }
            if (NewPurchasesBlocked) { Finish(PurchaseStatus.NativeRejected); return; }
            if (!sales.TryGetValue(request.Upgrade, out var sale) || !sale.gameObject.activeInHierarchy)
            { Finish(PurchaseStatus.Unavailable); return; }
            if (request.Price != Price(sale)) { Finish(PurchaseStatus.PriceChanged); return; }
            if (!(SaleStore.GetValue(sale) is Store_FederationSyrup store) || !store.isStoreOpen())
            { Finish(PurchaseStatus.Closed); return; }
            if (!presence.TryGetValue(peer, out var player) || Time.unscaledTime - player.Time > 2 ||
                (new Vector3(player.Pose.X, player.Pose.Y, player.Pose.Z) - sale.transform.position).sqrMagnitude > 16)
            { Finish(PurchaseStatus.TooFar); return; }
            var todo = Object.FindObjectOfType<ToDoList_Manager>();
            if (todo == null || !todo.isDone(ToDoList_Line.Sugarshack_SugarshackIsBuild) || todo.isDone(Line(request.Upgrade)) ||
                (request.Upgrade == ShackUpgrades.Heater && !todo.isDone(ToDoList_Line.Intermediate_HydroCompleted)))
            { Finish(PurchaseStatus.Unavailable); return; }
            var cash = Object.FindObjectOfType<Cash_Manager>();
            if (cash == null || !cash.isCanAfford(Price(sale))) { Finish(PurchaseStatus.Funds); return; }
            if (request.Upgrade == ShackUpgrades.OutdoorPump && (items.PeerHolding(peer) || gameplay.Player.IsHoldingAnObject ||
                deliveries.Any(d => d.Peer == peer))) { Finish(PurchaseStatus.HandsBusy); return; }
            try
            {
                using (Patches.RemoteActionScope.Enter()) sale.MouseDown();
                var next = Capture();
                if (!next.Equals(state)) Publish(next);
                if (request.Upgrade == ShackUpgrades.OutdoorPump && todo.isDone(Line(request.Upgrade)) && next.Has(request.Upgrade))
                {
                    if (!(gameplay.Player.Get_HoldingItem is Part_Spoiler_BedTank tank))
                    { uncertainPurchase = true; Finish(PurchaseStatus.Uncertain); return; }
                    gameplay.Player.CheckIfHoldingItemToCancel(tank);
                    tank.transform.SetParent(null, true); tank.transform.position = sale.transform.position + Vector3.up;
                    tank.UnGrab();
                    deliveries.Add((peer, buyer.ProfileId, request.Operation, tank, Time.unscaledTime + 5));
                    return;
                }
                Finish(todo.isDone(Line(request.Upgrade)) && next.Has(request.Upgrade) ? PurchaseStatus.Placed : PurchaseStatus.NativeRejected);
            }
            catch (Exception ex) { uncertainPurchase = true; Finish(PurchaseStatus.Uncertain); Debug.LogException(ex); }
        }

        // ---- Native capture/apply --------------------------------------------------------------
        private static bool B(FieldInfo field, object target) => (bool)field.GetValue(target);
        private static bool Active(FieldInfo field, SugarShack s) => field.GetValue(s) is GameObject go && go != null && go.activeSelf;
        private static bool ActiveComponent(FieldInfo field, SugarShack s) => field.GetValue(s) is Component c && c != null && c.gameObject.activeSelf;
        private static GameObject? FieldObject(FieldInfo field, object owner) => field.GetValue(owner) switch
        {
            GameObject go => go,
            Component component => component.gameObject,
            _ => null
        };
        private static void SetObject(FieldInfo field, object owner, bool active) => FieldObject(field, owner)?.SetActive(active);
        private void CaptureStructure()
        {
            void Remember(GameObject? go) { if (go != null && !originalStructure.ContainsKey(go)) originalStructure.Add(go, go.activeSelf); }
            var s = shack!;
            Remember(s.gameObject);
            foreach (string name in StructureObjects) Remember(FieldObject(F(name), s));
            if (HeaterObject.GetValue(s) != null && F("ElectricAutoHeater_ToDisable").GetValue(s) is GameObject[] disabled)
                foreach (var go in disabled) Remember(go);
            originalWaterCapacity = (int)WaterCapacity.GetValue(s);
            originalSyrupCapacity = (int)SyrupCapacity.GetValue(s);
            if (slab == null) return;
            Remember(slab.gameObject);
            foreach (string name in SlabObjects) Remember(FieldObject(AccessTools.Field(typeof(SugarShack_SlabBuild), name), slab));
            originalPalette = slab.GetAmountOnPalette;
            if (SlabLogs.GetValue(slab) is Transform logs)
                foreach (Transform child in logs) Remember(child.gameObject);
        }
        private void RestoreStructure()
        {
            var s = shack!;
            foreach (var go in addedPaletteLogs) if (go != null) { go.SetActive(false); Object.Destroy(go); }
            addedPaletteLogs.Clear();
            if (slab != null) SlabAmount.SetValue(slab, originalPalette);
            Osmosis.SetValue(s, original.Has(ShackUpgrades.ReverseOsmosis));
            Tank.SetValue(s, original.Has(ShackUpgrades.WaterTank));
            Tank50k.SetValue(s, original.Has(ShackUpgrades.WaterTank50k));
            WaterCapacity.SetValue(s, originalWaterCapacity);
            SyrupCapacity.SetValue(s, originalSyrupCapacity);
            foreach (var pair in originalStructure) if (pair.Key != null) pair.Key.SetActive(pair.Value);
            s.UpdateFluidLevel();
        }
        private void ReconcileStructure(SugarShackState next, bool animate = false)
        {
            var s = shack!;
            bool built = next.Has(ShackFlags.Built);
            if (built && slab != null) slab.LoadAndShackIsAlreadyBuilt();
            else if (!built)
            {
                s.gameObject.SetActive(false);
                if (slab != null)
                {
                    slab.gameObject.SetActive(true);
                    SetObject(AccessTools.Field(typeof(SugarShack_SlabBuild), "Palette"), slab, true);
                    SetObject(AccessTools.Field(typeof(SugarShack_SlabBuild), "ReceptionLocation"), slab, next.Palette < SugarShackState.MaxPalette);
                    SetObject(AccessTools.Field(typeof(SugarShack_SlabBuild), "ParticleDustOnCollision"), slab, false);
                }
            }
            if (slab != null && SlabLogs.GetValue(slab) is Transform logs)
            {
                if (!built && next.Palette > logs.childCount)
                {
                    int before = logs.childCount;
                    if (animate) for (int i = before; i < next.Palette; i++) AnimateLogToPalette(logs, i);
                    else
                    {
                        SlabAmount.SetValue(slab, before);
                        slab.Load_SpawnLogs(next.Palette - before);
                        for (int i = before; i < logs.childCount; i++) addedPaletteLogs.Add(logs.GetChild(i).gameObject);
                    }
                }
                for (int i = 0; i < logs.childCount; i++) logs.GetChild(i).gameObject.SetActive(!built && i < next.Palette);
                SlabAmount.SetValue(slab, (int)next.Palette);
            }
            // Native purchase methods only install. Reconcile both directions for direct-join guests
            // whose local save may have more upgrades than the host's world.
            bool reverse = next.Has(ShackUpgrades.ReverseOsmosis), water = next.Has(ShackUpgrades.WaterTank);
            bool water50 = next.Has(ShackUpgrades.WaterTank50k), heater = next.Has(ShackUpgrades.Heater);
            bool siroptier = next.Has(ShackUpgrades.Siroptier);
            Osmosis.SetValue(s, reverse); SetObject(F("ReverseOsmosis"), s, reverse); SetObject(F("Pipe_TankToEvap"), s, !reverse);
            Tank.SetValue(s, water); Tank50k.SetValue(s, water50);
            SetObject(F("WaterTankUpgrade"), s, water); SetObject(F("WaterTank50kUpgrade"), s, water50);
            WaterCapacity.SetValue(s, water50 ? 69200 : water ? 19200 : 9600);
            SetObject(HeaterObject, s, heater);
            if (F("ElectricAutoHeater_ToDisable").GetValue(s) is GameObject[] disabled)
                foreach (var go in disabled) if (go != null) go.SetActive(!heater);
            SetObject(F("Siroptier"), s, !siroptier); SetObject(Siroptier480, s, siroptier);
            SyrupCapacity.SetValue(s, siroptier ? 480 : 240);
            SetObject(Crimper, s, next.Has(ShackUpgrades.Crimping));
            SetObject(OutsidePump, s, next.Has(ShackUpgrades.OutdoorPump));
        }
        private SugarShackState Capture()
        {
            var s = shack!;
            var flags = ShackFlags.None;
            void Set(bool on, ShackFlags flag) { if (on) flags |= flag; }
            Set(s.Get_Valve_TankToEvap, ShackFlags.ValveEvap); Set(B(ValveSyrup, s), ShackFlags.ValveSyrup); Set(B(Heater, s), ShackFlags.Heater); Set(B(Pump, s), ShackFlags.Pump);
            Set(B(Vacuum, s), ShackFlags.Vacuum); Set(B(TapWaterOn, s), ShackFlags.TapWater); Set(B(TapSyrupOn, s), ShackFlags.TapSyrup); Set(B(Smoke, s), ShackFlags.Smoke);
            Set(B(Boiling, s), ShackFlags.Boiling); Set(s.gameObject.activeInHierarchy, ShackFlags.Built);
            if (host) flags |= SugarShackState.PhaseFlags(buildPhase);
            var upgrades = ShackUpgrades.None;
            void Up(bool on, ShackUpgrades upgrade) { if (on) upgrades |= upgrade; }
            Up(B(Osmosis, s), ShackUpgrades.ReverseOsmosis); Up(B(Tank, s), ShackUpgrades.WaterTank); Up(B(Tank50k, s), ShackUpgrades.WaterTank50k);
            Up(Active(HeaterObject, s), ShackUpgrades.Heater); Up(Active(Siroptier480, s), ShackUpgrades.Siroptier);
            Up(ActiveComponent(Crimper, s), ShackUpgrades.Crimping); Up(ActiveComponent(OutsidePump, s), ShackUpgrades.OutdoorPump);
            float fire = FireGauge.GetValue(s) is AnalogGauge g && g != null ? g.Value : 0;
            return new SugarShackState(flags, upgrades, (byte)Mathf.Clamp(slab != null ? slab.GetAmountOnPalette : 0, 0, SugarShackState.MaxPalette),
                Mathf.Clamp(s.Get_WaterLevel_Tank, 0, SugarShackState.MaxTank), Mathf.Clamp(s.Get_WaterLevel_Evaporator, 0, SugarShackState.MaxEvaporator),
                Mathf.Clamp(s.Get_SyrupLevel, 0, SugarShackState.MaxSyrup), Mathf.Clamp(s.Get_CrimpingMachine_LidsAmount, 0, SugarShackState.MaxLids),
                (ushort)Mathf.Clamp(s.GetAmountOfMapleTreeConnected, 0, SugarShackState.MaxTrees), Mathf.Clamp((float)Temperature.GetValue(s), -199, 199),
                Mathf.Clamp(fire, 0, 999), Mathf.Clamp(s.Get_TodayTreeEfficiency, 0, 2));
        }
        private void ApplyNative(SugarShackState next, bool snapshot, bool structure, bool animate = false)
        {
            var s = shack!;
            if (structure)
                ReconcileStructure(next, animate);
            s.Get_WaterLevel_Tank = next.Tank; s.Get_WaterLevel_Evaporator = next.Evaporator; s.Get_SyrupLevel = next.Syrup;
            s.Get_CrimpingMachine_LidsAmount = next.Lids; s.Get_TodayTreeEfficiency = next.Efficiency;
            Trees.SetValue(s, (int)next.Trees); Temperature.SetValue(s, next.Temperature);
            var sound = Sound.GetValue(s) as SOUND_SugarShack;
            bool effects = SugarShackPresentation.Ready(sound) && s.gameObject.activeInHierarchy && !gameplay.Get_isGamePaused;
            // Valves: the native setter turns the water valve's handle; the syrup valve's handle is private.
            bool evap = next.Has(ShackFlags.ValveEvap);
            if (s.Get_Valve_TankToEvap != evap || snapshot) { s.Get_Valve_TankToEvap = evap; if (effects && !snapshot) { if (evap) sound!.ValveEvap_On(); else sound!.ValveEvap_Off(); } }
            bool syrup = next.Has(ShackFlags.ValveSyrup);
            if (B(ValveSyrup, s) != syrup || snapshot)
            {
                ValveSyrup.SetValue(s, syrup);
                if (syrupValve != null) SyrupValveVisual.Invoke(syrupValve, new object[] { syrup });
                if (effects && !snapshot) { if (syrup) sound!.ValveSyrup_On(); else sound!.ValveSyrup_Off(); }
            }
            Heater.SetValue(s, next.Has(ShackFlags.Heater)); Switch(HeaterSwitch.GetValue(s) as Furniture_SwitchButton, next.Has(ShackFlags.Heater));
            Pump.SetValue(s, next.Has(ShackFlags.Pump)); Switch(PumpSwitch.GetValue(s) as Furniture_SwitchButton, next.Has(ShackFlags.Pump));
            if (OutsidePump.GetValue(s) is SugarShack_PumpSyrupOutside pump && pump != null && pump.gameObject.activeInHierarchy) pump.IsPumpMotorEnable(next.Has(ShackFlags.Pump));
            if (s.gameObject.activeInHierarchy) { HeaterLight.Invoke(s, null); PumpLight.Invoke(s, null); }
            Vacuum.SetValue(s, next.Has(ShackFlags.Vacuum)); TapWaterOn.SetValue(s, next.Has(ShackFlags.TapWater));
            TapSyrupOn.SetValue(s, next.Has(ShackFlags.TapSyrup)); Smoke.SetValue(s, next.Has(ShackFlags.Smoke)); Boiling.SetValue(s, next.Has(ShackFlags.Boiling));
            ReconcilePresentation(next);
            if (TempGauge.GetValue(s) is AnalogGauge temp && temp != null) temp.Value = next.Temperature;
            if (FireGauge.GetValue(s) is AnalogGauge fire && fire != null) fire.Value = next.Fire;
            s.UpdateFluidLevel();
        }
        private static void Switch(Furniture_SwitchButton? button, bool on)
        {
            if (button == null || button.isOn == on) return;
            button.isOn = on; button.UpdateVisual();
        }
        private void ReconcilePresentation(SugarShackState next)
        {
            var s = shack!;
            if (!s.gameObject.activeInHierarchy) return;
            SetObject(TapWater, s, next.Has(ShackFlags.TapWater)); SetObject(TapSyrup, s, next.Has(ShackFlags.TapSyrup));
            SetObject(SmokeParticle, s, next.Has(ShackFlags.Smoke));
            bool boiling = next.Has(ShackFlags.Boiling);
            foreach (var particle in (Distortion.GetValue(s) as ParticleSystem[]) ?? Array.Empty<ParticleSystem>())
                if (particle != null && particle.gameObject.activeInHierarchy)
                {
                    if (boiling && !particle.isPlaying) particle.Play();
                    else if (!boiling && particle.isPlaying) particle.Stop();
                }
            HeaterLight.Invoke(s, null); PumpLight.Invoke(s, null);
            if (!gameplay.Get_isGamePaused)
                SugarShackPresentation.Reconcile(Sound.GetValue(s) as SOUND_SugarShack, next.Has(ShackFlags.Vacuum),
                    next.Has(ShackFlags.TapWater), next.Has(ShackFlags.TapSyrup), boiling);
        }

        internal void SyrupPressed(SOUND_SugarShack sound)
        {
            if (!host || !Ready || SharedPresentationEvents.Replaying || Sound.GetValue(shack) as SOUND_SugarShack != sound) return;
            MonMPPlugin.Instance?.World?.Events?.Post(PresentationKind.SugarShackPress, 0, id, sound.transform.position);
        }
        internal void PlaySyrupPress(PresentationEvent e)
        {
            if (host || !Ready || !applied || e.Target != id || !shack!.gameObject.activeInHierarchy || gameplay.Get_isGamePaused) return;
            if (Sound.GetValue(shack) is SOUND_SugarShack sound && SugarShackPresentation.Ready(sound)) sound.PressSyrup();
        }

        // ---- Guest interception ----------------------------------------------------------------
        // Returns true when the native action must not run locally.
        internal bool Intercept(SugarShack native, ShackOp op)
        {
            if (host || applying || disposed || session?.IsRunning != true || native != shack) return false;
            if (Ready) channel!.Request(id, new SugarShackCommand(op).Encode(), true);
            reapplyAt = Time.unscaledTime + 1.5f; // a refused switch flips back to the host's state
            return true;
        }
        internal bool Valve(SugarShack native, ShackOp op, ref bool result)
        {
            if (!Intercept(native, op)) return false;
            result = op == ShackOp.ValveEvap ? native.Get_Valve_TankToEvap : B(ValveSyrup, native);
            return true;
        }
        internal bool Burn(SugarShack native, float power)
        {
            if (host || applying || disposed || session?.IsRunning != true || native != shack) return false;
            // Replayed shared logs are presentation copies of a fire already ticked by the
            // host. Suppress their native heat call without sending it back as a new input.
            if (SharedItems.GuestFireTicking) return true;
            if (power > 0 && !float.IsNaN(power) && !float.IsInfinity(power)) pendingHeat = Mathf.Min(pendingHeat + power, SugarShackCommand.MaxHeat * 4);
            return true;
        }
        internal void Ticked(SugarShack native) { if (host && native == shack) hostTicks++; }

        // ---- Host ------------------------------------------------------------------------------
        [MonMP.Core.World.WorldCommandAdapters(SugarShackState.Adapter)]
        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (SugarUpgradeRequest.Is(payload)) { PurchaseCommand(peer, entity, payload, false); return; }
            if (!host || peer == 0 || entity != id || shack == null || !SugarShackCommand.Is(payload)) return;
            SugarShackCommand command;
            try { command = SugarShackCommand.Decode(payload); } catch (InvalidDataException) { return; }
            if (!presence.TryGetValue(peer, out var p) || Time.unscaledTime - p.Time > 2) return;
            if ((new Vector3(p.Pose.X, p.Pose.Y, p.Pose.Z) - shack.transform.position).sqrMagnitude > CommandRange * CommandRange) return;
            if (!shack.gameObject.activeInHierarchy) return;
            var s = shack;
            if (command.Op == ShackOp.Heat)
            {
                var budget = heatBudget.TryGetValue(peer, out var b) && b.Tick == hostTicks ? b.Heat : 0;
                float power = Mathf.Min(command.Power, SugarShackCommand.MaxHeat - budget);
                if (power <= 0) return;
                heatBudget[peer] = (hostTicks, budget + power);
                s.WoodBurningInEvap(power);
                return;
            }
            // Per control, so pressing one valve right after another is not dropped.
            if (lastPress.TryGetValue((peer, command.Op), out var last) && Time.unscaledTime - last < PressInterval) return;
            lastPress[(peer, command.Op)] = Time.unscaledTime;
            applying = true;
            Patches.RemoteActionScope.LastError = null;
            try
            {
                using (Patches.RemoteActionScope.Enter())
                {
                    switch (command.Op)
                    {
                        case ShackOp.ValveEvap: s.Get_Valve_TankToEvap = s.ToggleValve_TankToEvap(); break;
                        case ShackOp.ValveSyrup:
                            bool open = s.ToggleValve_SyrupToBaril();
                            if (syrupValve != null) SyrupValveVisual.Invoke(syrupValve, new object[] { open });
                            break;
                        case ShackOp.HeaterOn:
                        case ShackOp.HeaterOff:
                            if (!Active(HeaterObject, s)) return;
                            bool heat = command.Op == ShackOp.HeaterOn;
                            if (heat) s.ElectricAutoHeater_TurnOn(); else s.ElectricAutoHeater_TurnOff();
                            Switch(HeaterSwitch.GetValue(s) as Furniture_SwitchButton, heat);
                            break;
                        case ShackOp.PumpOn:
                        case ShackOp.PumpOff:
                            if (!(OutsidePump.GetValue(s) is SugarShack_PumpSyrupOutside pump) || pump == null || !pump.gameObject.activeInHierarchy) return;
                            bool on = command.Op == ShackOp.PumpOn;
                            if (on) s.OutsidePump_TurnOn(); else s.OutsidePump_TurnOff();
                            Switch(PumpSwitch.GetValue(s) as Furniture_SwitchButton, on);
                            break;
                    }
                }
            }
            finally { applying = false; }
            if (Patches.RemoteActionScope.LastError is string error)
                channel!.Reply(peer, id, NativeErrorReply.Encode(error));
            Publish(Capture());
        }
        private void NativeReply(ulong entity, byte[] payload)
        {
            if (!host && entity == id && NativeErrorReply.TryDecode(payload, out string error))
                Singleton<UI_ErrorMessage>.i?.Show(error);
        }
        private void Publish(SugarShackState next) { state = next; channel!.Publish(id, next.Encode()); }

        // ---- Guest apply -----------------------------------------------------------------------
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            foreach (var record in records.Values.Where(r => r.Adapter == SugarShackState.Adapter).ToArray()) Apply(record);
        }
        private void Changed(WorldRecord record) { if (!host && record.Adapter == SugarShackState.Adapter) Apply(record); }
        private void Apply(WorldRecord record)
        {
            if (record.Id != id || shack == null) throw new InvalidDataException("Unknown sugar shack record.");
            var previous = state;
            state = SugarShackState.Decode(record.Payload);
            bool snapshot = !applied; applied = true;
            var phase = state.BuildPhase;
            if (!snapshot && phase != previous.BuildPhase)
            {
                if (previous.BuildPhase == ShackBuildPhase.None && phase != ShackBuildPhase.None)
                    Singleton<SOUND_Manager>.i?.PlaySound_BuggyBuildCompleted();
                if (phase == ShackBuildPhase.Animating && guestBuild == null && slab != null)
                {
                    Singleton<UI_EventMessage>.i?.Hide();
                    guestAnimating = true;
                    guestBuild = MonMPPlugin.Instance!.StartCoroutine(GuestBuildAnimation());
                }
                else if (phase == ShackBuildPhase.None && (previous.BuildPhase == ShackBuildPhase.Clearing || previous.BuildPhase == ShackBuildPhase.ComeBack))
                    Singleton<UI_EventMessage>.i?.Hide();
            }
            // Lids only drop when the host's crimper seals a can, which grants this natively.
            if (!snapshot && state.Lids < previous.Lids) GuestAchievements.Award(AchievementType.FirstHomemadeSyrupCan);
            applying = true;
            try { ApplyNative(state, snapshot, !guestAnimating, !snapshot); }
            finally { applying = false; }
        }

        public void Update()
        {
            if (!Ready) return;
            float now = Time.unscaledTime;
            if (host)
            {
                foreach (var delivery in deliveries.ToArray())
                {
                    if (delivery.Item != null && items.DeliverPurchase(delivery.Item, delivery.Peer, out _, out bool held))
                    {
                        deliveries.Remove(delivery);
                        var reply = new SugarUpgradeReply(delivery.Operation, held ? PurchaseStatus.Held : PurchaseStatus.Placed);
                        purchaseResults[(delivery.Profile, delivery.Operation)] = reply;
                        if (session!.Peers.TryGetValue(delivery.Peer, out var buyer) && buyer.ProfileId == delivery.Profile)
                            channel!.Reply(delivery.Peer, id, reply.Encode());
                    }
                    else if (delivery.Item == null || now >= delivery.Deadline)
                    {
                        deliveries.Remove(delivery); uncertainPurchase = true;
                        var reply = new SugarUpgradeReply(delivery.Operation, PurchaseStatus.Uncertain);
                        purchaseResults[(delivery.Profile, delivery.Operation)] = reply;
                        if (session!.Peers.TryGetValue(delivery.Peer, out var buyer) && buyer.ProfileId == delivery.Profile)
                            channel!.Reply(delivery.Peer, id, reply.Encode());
                    }
                }
            }
            ExpirePurchases(now);
            if (host)
            {
                if (now < nextCapture) return;
                nextCapture = now + 0.25f;
                var next = Capture();
                if (!next.Equals(state)) Publish(next);
                return;
            }
            if (pendingHeat > 0 && now >= nextHeat)
            {
                nextHeat = now + 0.5f;
                float power = Mathf.Min(pendingHeat, SugarShackCommand.MaxHeat);
                pendingHeat -= power;
                channel!.Request(id, new SugarShackCommand(ShackOp.Heat, power).Encode(), true);
            }
            if (applied && reapplyAt > 0 && now >= reapplyAt)
            {
                reapplyAt = 0;
                applying = true;
                try { ApplyNative(state, false, false); }
                finally { applying = false; }
            }
            UpdateBuildPresentation(now);
            if (applied && now >= nextPresentation)
            {
                nextPresentation = now + 0.25f;
                ReconcilePresentation(state);
            }
            // The generator glow is part of the native tick, which guests do not run.
            if (now >= nextGlow && shack!.gameObject.activeInHierarchy) { nextGlow = now + 1; GlowLight.Invoke(shack, null); }
        }
        public void Dispose()
        {
            if (disposed) return;
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null) { channel.CommandRequested -= Command; channel.CommandRejected -= Rejected; channel.ReplyReceived -= PurchaseReply; channel.ReplyReceived -= NativeReply; channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; }
            StopBuildPresentation();
            if (!host && applied && shack != null)
            {
                applying = true;
                try { ApplyNative(original, true, false); RestoreStructure(); }
                finally { applying = false; }
            }
            deliveries.Clear(); pendingPurchases.Clear(); timedOutPurchases.Clear(); disposed = true; session = null; channel = null;
        }

        // ---- Build ceremony and palette presentation ---------------------------------------------
        // Host: replaces the native build coroutine, which waits only for the host player to move back
        // 20 m. Every player (host and fresh guest poses) must clear the slab before the shack drops.
        internal bool HostBuild(SugarShack_SlabBuild native, out IEnumerator routine)
        {
            routine = null!;
            if (!host || disposed || session?.IsRunning != true || native != slab) return false;
            routine = HostBuildRoutine(native); return true;
        }
        private IEnumerable<Vector3> PlayerPositions()
        {
            yield return gameplay.Player.transform.position;
            if (disposed) yield break;
            float now = Time.unscaledTime;
            foreach (var entry in presence.Values)
                if (now - entry.Time < 5) yield return new Vector3(entry.Pose.X, entry.Pose.Y, entry.Pose.Z);
        }
        private float NearestPlayer(Vector3 at) => PlayerPositions().Select(p => Vector3.Distance(p, at)).DefaultIfEmpty(float.PositiveInfinity).Min();
        private static void ShowBuildMessage(float distance, ShackBuildPhase phase)
        {
            var ui = Singleton<UI_EventMessage>.i;
            if (ui == null) return;
            string name = TEXT.GetText("Sugarshack_Name");
            if (phase == ShackBuildPhase.ComeBack)
            {
                if (distance > BuildComeBackRadius) ui.Show(TEXT.GetText("Sugarshack_Build_ComeBack"), name);
                return;
            }
            if (distance >= BuildClearRadius) return;
            string key = distance < 6 ? "Sugarshack_Build_MoveBack1" : distance < 11 ? "Sugarshack_Build_MoveBack2" :
                distance < 15 ? "Sugarshack_Build_MoveBack3" : "Sugarshack_Build_MoveBack4";
            ui.Show(TEXT.GetText(key), name);
        }
        private IEnumerator HostBuildRoutine(SugarShack_SlabBuild native)
        {
            Singleton<SOUND_Manager>.i.PlaySound_BuggyBuildCompleted();
            var ui = Singleton<UI_EventMessage>.i;
            Vector3 at = native.transform.position;
            buildPhase = ShackBuildPhase.Clearing;
            while (NearestPlayer(at) < BuildClearRadius)
            {
                ShowBuildMessage(Vector3.Distance(gameplay.Player.transform.position, at), ShackBuildPhase.Clearing);
                yield return new WaitForSeconds(0.3f);
            }
            buildPhase = ShackBuildPhase.ComeBack;
            float comeBack = 10f;
            while (NearestPlayer(at) > BuildComeBackRadius && comeBack > 0f)
            {
                ShowBuildMessage(Vector3.Distance(gameplay.Player.transform.position, at), ShackBuildPhase.ComeBack);
                comeBack -= 0.3f;
                yield return new WaitForSeconds(0.3f);
            }
            ui.Hide();
            buildPhase = ShackBuildPhase.Animating;
            var sound = native.gameObject.AddComponent<SOUND_VFX>();
            sound.UseForBuildingCompleted = true;
            AccessTools.Field(typeof(SugarShack_SlabBuild), "SOUND_VFX")?.SetValue(native, sound);
            yield return BuildAnimation(native, sound);
            Singleton<ToDoList_Manager>.i.SugarShackBuilt();
            buildPhase = ShackBuildPhase.None;
            yield return new WaitForSeconds(2f);
            native.gameObject.SetActive(false);
        }
        // The visual part of the native BuildSugarShack coroutine: the palette logs fly into the shack
        // while it grows in the air, then it drops with dust and the hit-ground sound.
        private static IEnumerator BuildAnimation(SugarShack_SlabBuild native, SOUND_VFX sound)
        {
            var building = (GameObject)SlabShack.GetValue(native);
            var cube = (GameObject)SlabCube.GetValue(native);
            var palette = (GameObject)SlabPalette.GetValue(native);
            var dust = (GameObject)SlabDust.GetValue(native);
            var logs = (Transform)SlabLogs.GetValue(native);
            building.SetActive(true);
            cube.SetActive(true);
            building.transform.localPosition = new Vector3(0f, 4f, 0f);
            float delay = 0f;
            for (int i = logs.childCount - 1; i >= 0; i--)
            {
                var log = logs.GetChild(i).gameObject;
                if (!log.activeSelf) continue;
                var move = log.AddComponent<MoveObjectToTarget>();
                move.TargetPosition = building.transform.position + Vector3.up;
                move.DelayBeforeStart = delay; move.TotalTimeToTarget = 0.6f;
                move.ScaleDownWhileMoving = true; move.DestroyAtTheEnd = true;
                delay += 0.03f;
            }
            var paletteCollider = palette.GetComponent<Collider>();
            if (paletteCollider != null) paletteCollider.enabled = false;
            float total = delay + 0.6f, timer = 0f;
            while (timer < total)
            {
                timer += Time.fixedDeltaTime;
                building.transform.localScale = Vector3.one * Mathf.SmoothStep(0.1f, 1f, timer / total);
                building.transform.localPosition = new Vector3(0f, Mathf.Lerp(4f, 4.1f, timer / total), 0f);
                cube.transform.localScale = new Vector3(Mathf.SmoothStep(0.1f, 14f, timer / total), 3.5f, Mathf.SmoothStep(0.1f, 7f, timer / total));
                yield return new WaitForFixedUpdate();
            }
            for (float pause = 0f; pause < 1.6f; )
            {
                pause += Time.fixedDeltaTime;
                building.transform.localPosition = new Vector3(0f, Mathf.Lerp(4.1f, 4.15f, pause / 1.6f), 0f);
                yield return new WaitForFixedUpdate();
            }
            palette.SetActive(false);
            dust.SetActive(true);
            for (float drop = 0f; drop < 0.13f; )
            {
                drop += Time.fixedDeltaTime;
                float y = Mathf.Lerp(4.15f, 0f, drop / 0.13f);
                building.transform.localPosition = new Vector3(0f, y, 0f);
                cube.transform.localScale = new Vector3(11f, y, 7f);
                yield return new WaitForFixedUpdate();
            }
            if (sound != null) sound.BuildingHitGround();
            cube.SetActive(false);
            building.transform.localScale = Vector3.one;
            building.transform.localPosition = Vector3.zero;
        }
        private IEnumerator GuestBuildAnimation()
        {
            var native = slab!;
            guestBuildSound = native.gameObject.AddComponent<SOUND_VFX>();
            guestBuildSound.UseForBuildingCompleted = true;
            yield return BuildAnimation(native, guestBuildSound);
            guestAnimating = false;
            if (!disposed && shack != null)
            {
                applying = true;
                try { ApplyNative(state, false, true); }
                finally { applying = false; }
            }
            yield return new WaitForSeconds(2f);
            if (!disposed && state.Has(ShackFlags.Built)) native.gameObject.SetActive(false);
            guestBuild = null;
        }
        private void UpdateBuildPresentation(float now)
        {
            for (int i = claimedLogs.Count - 1; i >= 0; i--)
                if (claimedLogs[i].Log == null || now >= claimedLogs[i].Expires)
                {
                    if (claimedLogs[i].Log != null) Object.Destroy(claimedLogs[i].Log);
                    claimedLogs.RemoveAt(i);
                }
            unmatchedPlaceholders.RemoveAll(time => now - time > ClaimedLogSeconds);
            var phase = state.BuildPhase;
            if ((phase == ShackBuildPhase.Clearing || phase == ShackBuildPhase.ComeBack) && slab != null && now >= nextBuildMessage)
            {
                nextBuildMessage = now + 0.3f;
                ShowBuildMessage(Vector3.Distance(gameplay.Player.transform.position, slab.transform.position), phase);
            }
        }
        // Guest: the host consumed a shared log at the reception circle. Keep our copy as the palette
        // visual so it can glide onto the palette like the native reception, instead of vanishing.
        internal bool ClaimRemovedLog(InteractableItems item)
        {
            if (host || disposed || slab == null || !(item is Item_WoodLog log) || log.tree_Type != Tree_Type.Pine ||
                state.Has(ShackFlags.Built) || !(SlabReception.GetValue(slab) is GameObject reception) || reception == null ||
                (item.transform.position - reception.transform.position).sqrMagnitude > 16) return false;
            // The palette count already arrived and a placeholder is flying; let this copy go.
            if (unmatchedPlaceholders.Count > 0) { unmatchedPlaceholders.RemoveAt(0); return false; }
            if (item.rb != null) Object.Destroy(item.rb);
            foreach (var collider in item.GetComponentsInChildren<Collider>()) collider.enabled = false;
            if (item.itemCollider != null) Object.Destroy(item.itemCollider);
            var go = item.gameObject;
            Object.Destroy(item);
            claimedLogs.Add((go, Time.unscaledTime + ClaimedLogSeconds));
            return true;
        }
        private void AnimateLogToPalette(Transform logs, int index)
        {
            claimedLogs.RemoveAll(entry => entry.Log == null);
            GameObject log;
            if (claimedLogs.Count > 0)
            { log = claimedLogs[0].Log; claimedLogs.RemoveAt(0); }
            else
            {
                var start = SlabReception.GetValue(slab!) is GameObject reception && reception != null ? reception.transform.position : logs.position;
                log = Object.Instantiate(Singleton<GameAssets>.i.PineLogVisual, start + Vector3.up * 0.5f, logs.rotation);
                unmatchedPlaceholders.Add(Time.unscaledTime);
            }
            addedPaletteLogs.Add(log);
            log.transform.SetParent(logs, true);
            var target = new Vector3(-(index % 8) * 0.45f, 0f, Mathf.Floor(index / 8f) * 0.4f);
            logMoves.Add(MonMPPlugin.Instance!.StartCoroutine(MoveLogToPalette(log, logs, target)));
        }
        private static IEnumerator MoveLogToPalette(GameObject log, Transform logs, Vector3 target)
        {
            var filter = log.GetComponent<MeshFilter>() ?? log.GetComponentInChildren<MeshFilter>();
            var size = filter != null && filter.sharedMesh != null ? filter.sharedMesh.bounds.size : Vector3.one;
            var targetScale = new Vector3(0.5f / Mathf.Max(size.x, 0.001f), 2.6f / Mathf.Max(size.y, 0.001f), 0.5f / Mathf.Max(size.z, 0.001f));
            var fromPosition = log.transform.localPosition; var fromRotation = log.transform.rotation; var fromScale = log.transform.localScale;
            for (float left = LogMoveSeconds; left > 0f; )
            {
                if (log == null || logs == null) yield break;
                left -= Time.fixedDeltaTime;
                float t = Mathf.Clamp01(left / LogMoveSeconds);
                log.transform.localPosition = Vector3.Lerp(target, fromPosition, t);
                log.transform.rotation = Quaternion.Lerp(logs.rotation, fromRotation, t);
                log.transform.localScale = Vector3.Lerp(targetScale, fromScale, t);
                yield return new WaitForFixedUpdate();
            }
        }
        private void StopBuildPresentation()
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin != null)
            {
                if (guestBuild != null) plugin.StopCoroutine(guestBuild);
                foreach (var move in logMoves) if (move != null) plugin.StopCoroutine(move);
            }
            guestBuild = null; logMoves.Clear(); guestAnimating = false;
            if (guestBuildSound != null) Object.Destroy(guestBuildSound);
            foreach (var entry in claimedLogs) if (entry.Log != null) Object.Destroy(entry.Log);
            claimedLogs.Clear(); unmatchedPlaceholders.Clear();
            if (!host && slab != null)
            {
                if (SlabShack.GetValue(slab) is GameObject building && building != null) { building.transform.localScale = Vector3.one; building.transform.localPosition = Vector3.zero; }
                if (SlabCube.GetValue(slab) is GameObject cube && cube != null) cube.SetActive(false);
            }
        }

        // ---- Test hooks ------------------------------------------------------------------------

#if MONMP_DEVELOPMENT
        internal ulong TestId => id;


        internal SugarShack? TestShack => shack;


        internal SugarShackState TestState => state;


        internal SugarShackState TestLocal => Capture();


        internal int TestHostTicks => hostTicks;


        internal ItemForSale_SugarShack TestSale(ShackUpgrades upgrade) => sales[upgrade];


        internal float TestSalePrice(ShackUpgrades upgrade) => Price(sales[upgrade]);


        internal void TestBuild() { if (shack != null && !shack.gameObject.activeInHierarchy) slab?.LoadAndShackIsAlreadyBuilt(); }


        internal void TestTick(int tick) => NativeTick.Invoke(shack, new object[] { tick });


        internal void TestLevels(int tank, int evaporator, int syrup)
        {
            shack!.Get_WaterLevel_Tank = tank; shack.Get_WaterLevel_Evaporator = evaporator; shack.Get_SyrupLevel = syrup; shack.UpdateFluidLevel();
        }


        internal void TestWaterValve() => ((SugarShack_Valve_WaterTank)WaterValve.GetValue(shack)).MouseDown();


        internal void TestSyrupValve() => syrupValve!.MouseDown();


        internal Furniture_SwitchButton TestHeaterSwitch => (Furniture_SwitchButton)HeaterSwitch.GetValue(shack);


        internal bool TestSyrupValveOpen => B(ValveSyrup, shack!);


        internal string TestActivationDiagnostic
        {
            get
            {
                if (shack == null) return "no shack";
                var targetsField = AccessTools.Field(typeof(DisableWhenPlayerTooFar), "ToDisableWhenPlayerTooFar");
                var radiusField = AccessTools.Field(typeof(DisableWhenPlayerTooFar), "DistanceToDisable");
                var controllers = Object.FindObjectsOfType<DisableWhenPlayerTooFar>(true).Where(controller => controller.gameObject.scene.IsValid())
                    .Select(controller => (Controller: controller, Targets: targetsField.GetValue(controller) as GameObject[] ?? Array.Empty<GameObject>()))
                    .Where(entry => entry.Targets.Any(target => target != null &&
                        (target.transform.IsChildOf(shack.transform) || shack.transform.IsChildOf(target.transform)))).ToArray();
                return "builtSelf/hierarchy=" + shack.gameObject.activeSelf + "/" + shack.gameObject.activeInHierarchy +
                    "; hostDistance=" + Vector3.Distance(gameplay.PlayerWalking.transform.position, shack.transform.position).ToString("F1") +
                    "; nearestFreshPlayer=" + NearestPlayer(shack.transform.position).ToString("F1") + "; gates=" + controllers.Length + "; " +
                    string.Join(" | ", controllers.Select(entry => RuntimeProbe.CanonicalPath(entry.Controller.transform) +
                        "[radius=" + radiusField.GetValue(entry.Controller) + "; enabled=" + entry.Controller.isActiveAndEnabled +
                        "; targets=" + string.Join(",", entry.Targets.Where(target => target != null).Select(target =>
                            RuntimeProbe.CanonicalPath(target.transform) + ":" + target.activeSelf + "/" + target.activeInHierarchy)) + "]"));
            }
        }


        internal float TestGauge => TempGauge.GetValue(shack) is AnalogGauge g && g != null ? g.Value : float.NaN;


        internal float TestGaugeMax => TempGauge.GetValue(shack) is AnalogGauge g && g != null ? g.maxValue : float.NaN;


        internal bool TestLateReplySettles()
        {
            var operation = Guid.NewGuid();
            pendingPurchases.Add(operation, Time.unscaledTime - 1);
            ExpirePurchases(Time.unscaledTime);
            bool blocked = timedOutPurchases.Contains(operation) && !TransactionsSettled;
            PurchaseReply(id, new SugarUpgradeReply(operation, PurchaseStatus.Unavailable).Encode());
            return blocked && !timedOutPurchases.Contains(operation) && TransactionsSettled && TestLastPurchase?.Operation == operation;
        }


        internal bool TestStructureRestored => originalStructure.All(pair => pair.Key == null || pair.Key.activeSelf == pair.Value) &&
            (int)WaterCapacity.GetValue(shack) == originalWaterCapacity && (int)SyrupCapacity.GetValue(shack) == originalSyrupCapacity &&
            (slab == null || slab.GetAmountOnPalette == originalPalette);
#endif

    }
}
