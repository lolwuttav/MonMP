using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UniStorm;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Diagnostics
{
    // Real native schedule/open/close/restock and one native bolt-pack purchase. Camera targeting
    // remains manual. Only this active scene is staged; no save is loaded, funded or written.
    internal sealed class PairedTravelingVanTest
    {
        private const byte Marker = 244;
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly bool host;
        private readonly Store_TravelingVan van;
        private readonly Cash_Manager cash;
        private readonly ItemForSale_Display bolt;
        private readonly ulong listing;
        private readonly Guid operation = Guid.NewGuid();
        private readonly float deadline = Time.unscaledTime + 100;
        private readonly Vector3 originalPosition, originalPlayerPosition;
        private readonly Quaternion originalPlayerRotation;
        private readonly bool originalOpen;
        private readonly int originalCash, originalRogerSpent, originalPurchases;
        private readonly float originalSpent;
        private readonly UniStormSystem sky;
        private readonly UniStormSystem.EnableFeature originalFlow;
        private readonly Dictionary<GameObject, bool> activity = new Dictionary<GameObject, bool>();
        private readonly Dictionary<ItemForSale_Display, int> colors = new Dictionary<ItemForSale_Display, int>();
        private readonly Dictionary<MeshRenderer, Material[]> materials = new Dictionary<MeshRenderer, Material[]>();
        private readonly List<IEnumerator> motions = new List<IEnumerator>();
        private readonly List<ulong> boughtItems = new List<ulong>();
        private WorldChannel? subscribed;
        private SharedTravelingVan? disconnected;
        private Harmony? guard;
        private static PairedTravelingVanTest? nativeOwner;
        private int phase, acknowledgment, guestMotions;
        private uint firstPeer, guestPeer;
        private float next, retryAt, purchaseDeliveryDeadline;
        private byte pendingStage;
        private bool stageAcknowledged, cleaned, departureObserved;
        private byte[] restock = Array.Empty<byte>();
        private int purchasedColor, cashAfterPurchase;
        internal string Status => "traveling van phase=" + phase + "; ack=" + acknowledgment +
            "; open=" + van.isOpen + "; visible=" + Visible + "; y=" + van.transform.position.y.ToString("F3") +
            "; guestNativeMotion=" + guestMotions + "; cash=" + cash.GetCash;
        private bool Visible => Field<GameObject>(van, "Van").activeInHierarchy;

        internal PairedTravelingVanTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay; host = plugin.TestOptions.IsHost;
            van = plugin.World!.TravelingVan.TestVan ?? throw new InvalidOperationException("Native traveling van missing.");
            cash = Object.FindObjectOfType<Cash_Manager>(); sky = UniStormSystem.Instance;
            bolt = plugin.World.TravelingVan.TestBolt(0) ?? throw new InvalidOperationException("Van bolt display missing.");
            listing = plugin.World.Stores.TestListings.Single(id => plugin.World.Stores.TestDisplay(id) == bolt);
            Require(van.gameObject.scene.IsValid() && van.gameObject.activeInHierarchy && van.StoreDoors.Length == 0,
                "Fixture requires the active native van without door side effects.");
            Require(gameplay.Player.Get_HoldingItem == null && gameplay.Player.Get_ActiveTool == null, "Fixture requires empty hands.");
            originalPosition = van.transform.position; originalOpen = van.isOpen;
            originalPlayerPosition = gameplay.PlayerWalking.transform.position; originalPlayerRotation = gameplay.PlayerWalking.transform.rotation;
            originalCash = cash.GetCash; originalSpent = GameManager.Get_PlayerStats[4]; originalRogerSpent = GameManager.Get_CashSpentAtRoger;
            originalPurchases = plugin.World.Stores.TestPurchases; originalFlow = sky.TimeFlow;
            foreach (var t in van.GetComponentsInChildren<Transform>(true)) activity[t.gameObject] = t.gameObject.activeSelf;
            foreach (var display in van.GetComponentsInChildren<ItemForSale_Display>(true)) colors[display] = display.CustomIntValue1;
            foreach (var renderer in van.GetComponentsInChildren<MeshRenderer>(true)) materials[renderer] = renderer.sharedMaterials;
            Require(nativeOwner == null, "Another van fixture is active."); nativeOwner = this;
            guard = new Harmony("MonMP.LocalTest.TravelingVan");
            try
            {
                foreach (string name in new[] { "RaiseVan", "LowerVan" })
                {
                    var method = AccessTools.Method(typeof(Store_TravelingVan), name, Type.EmptyTypes);
                    guard.Patch(method, postfix: new HarmonyMethod(typeof(PairedTravelingVanTest), nameof(CaptureMotion)));
                    PatchHealth.VerifyManualPatchInstalled(guard, method);
                }
                if (host)
                {
                    Require(!originalOpen && !Visible && !GameManager.Get_RogerStoreIsOpen, "Host fixture requires a closed van and locked Roger store.");
                    Require(cash.isCanAfford(bolt.Get_price) && originalRogerSpent + Mathf.CeilToInt(bolt.Get_price) < 5000,
                        "Host cannot afford one bolt pack or purchase would unlock Roger; refusing to alter funding/progression.");
                    sky.TimeFlow = UniStormSystem.EnableFeature.Disabled;
                    Schedule(600, 4); Require(van.isOpen && Visible && motions.Count == 1, "Native Thursday schedule did not open/roll/raise the van.");
                    Require(plugin.World.TravelingVan.TestSprays.Count(s => s.activeSelf) == 1 &&
                        bolt.CustomIntValue1 > 1 && bolt.CustomIntValue1 != plugin.World.TravelingVan.TestBolt(1)!.CustomIntValue1,
                        "Native opening did not select one spray and distinct first two bolt colours.");
                    Debug.Log("MonMP TravelingVan started real Thursday native open/stock roll; " + Status);
                }
            }
            catch { Cleanup(); throw; }
        }

        private static void CaptureMotion(Store_TravelingVan __instance, IEnumerator __result)
        {
            var owner = nativeOwner;
            if (owner == null || __instance != owner.van) return;
            if (owner.host) owner.motions.Add(__result); else owner.guestMotions++;
        }
        private void Schedule(int minute, int day)
        {
            var manager = Object.FindObjectOfType<StoreManagers>();
            Require(manager != null, "Native StoreManagers missing.");
            var field = AccessTools.Field(typeof(StoreManagers), "stores"); var original = field.GetValue(manager);
            try { field.SetValue(manager, new List<Stores> { van }); manager.Tick(minute, day); }
            finally { field.SetValue(manager, original); }
        }

        internal bool Tick()
        {
            Require(Time.unscaledTime < deadline, "Fixture stalled: " + Status);
            Require(guestMotions == 0, "Guest ran a native roll/movement coroutine.");
            if (!host && phase == 5)
            {
                if (Time.unscaledTime < next) return false;
                Require(disconnected!.TestRestored, "Guest offline van baseline was not restored exactly.");
                plugin.BeginPairedTestSession(); phase = 6; return false;
            }
            var world = plugin.World;
            if (world?.Channel.FullySynchronized != true || !world.PersonalProfileReady) return false;
            Attach(world.Channel); RetryStage();
            if (Time.unscaledTime < next) return false;
            return host ? Host(world) : Guest(world);
        }
        private bool Parity(SharedTravelingVan shared) => shared.TestNativeState.SequenceEqual(shared.TestCanonicalState) && shared.TestMaterialsMatch;

        private bool Host(SharedWorld world)
        {
            if (phase == 0 && acknowledgment >= 1)
            {
                Require(van.isOpen && Visible, "Host opening disappeared.");
                purchasedColor = bolt.CustomIntValue1; cashAfterPurchase = originalCash - Mathf.RoundToInt(bolt.Get_price * 100);
                phase = 1;
            }
            else if (phase == 1 && acknowledgment >= 2)
            {
                Require(world.Stores.TestPurchases == originalPurchases + 1 && cash.GetCash == cashAfterPurchase &&
                    GameManager.Get_PlayerStats[4] == originalSpent + originalCash - cashAfterPurchase &&
                    GameManager.Get_CashSpentAtRoger == originalRogerSpent + Mathf.CeilToInt(bolt.Get_price),
                    "Native purchase was not charged/progressed exactly once.");
                var item = world.Items.TestItem(world.Items.TestNewestPrefab(world.Stores.TestState(listing).Prefab));
                Require(item is Pack_Bolts12 && item.IntValue1 == purchasedColor, "Host factory did not stamp its rolled bolt colour.");
                boughtItems.Add(world.Items.TestBoundId(item!));
                Debug.Log("MonMP TravelingVan native bolt purchase charged once; color=" + purchasedColor + "; item=" + boughtItems[0] + "; " + Status);
                Schedule(600, 0); Require(!van.isOpen && motions.Count == 2, "Native Monday schedule did not close/lower the van.");
                phase = 2; next = Time.unscaledTime + 5.5f;
            }
            else if (phase == 2 && acknowledgment >= 3)
            {
                Require(!Visible, "Native close did not hide the van.");
                Schedule(600, 4); Require(van.isOpen && motions.Count == 3, "Native Thursday restock failed.");
                restock = world.TravelingVan.TestNativeState; phase = 3; next = Time.unscaledTime + 5.5f;
            }
            else if (phase == 3)
            {
                departureObserved |= plugin.Session!.Peers.Count == 1;
                Require(cash.GetCash == cashAfterPurchase && world.Stores.TestPurchases == originalPurchases + 1,
                    "Restock/reconnect/retry changed the committed purchase.");
                if (acknowledgment >= 5)
                {
                    Require(departureObserved && guestPeer != firstPeer, "Host did not observe a real departure and new peer.");
                    Cleanup(); return true;
                }
            }
            return false;
        }
        private bool Guest(SharedWorld world)
        {
            var shared = world.TravelingVan;
            if (phase == 0 && Parity(shared) && van.isOpen && Visible &&
                Math.Abs(van.transform.position.y - Field<Vector3>(van, "PositionWhileOpen").y) < 0.05f)
            {
                firstPeer = plugin.Session!.LocalPeerId;
                Require(Math.Abs(van.transform.position.y - Field<Vector3>(van, "PositionWhileOpen").y) < 0.05f,
                    "Menu join left the open host van below ground.");
                Signal(1); Move(bolt.transform.position + Vector3.up * 0.4f + Vector3.forward); phase = 1; next = Time.unscaledTime + 1;
            }
            else if (phase == 1 && stageAcknowledged)
            {
                purchasedColor = bolt.CustomIntValue1;
                world.Stores.TestRequest(listing, operation, world.Stores.TestState(listing).Price);
                world.Stores.TestRequest(listing, operation, world.Stores.TestState(listing).Price); phase = 2;
            }
            else if (phase == 2 && world.Stores.TestLastResult?.Status == PurchaseStatus.Held)
            {
                // Replies send immediately; item records are batched by WorldChannel.Pump. A paid
                // receipt can arrive a frame before the corresponding native item/hand grant.
                var result = world.Stores.TestLastResult;
                var item = gameplay.Player.Get_HoldingItem;
                ulong heldId = item == null ? 0 : world.Items.TestBoundId(item);
                if (purchaseDeliveryDeadline == 0) purchaseDeliveryDeadline = Time.unscaledTime + 5;
                if (!world.Channel.Records.ContainsKey(result.Item) || item == null || heldId != result.Item)
                {
                    Require(Time.unscaledTime < purchaseDeliveryDeadline,
                        "Paid item did not reach the native hand within 5 seconds; receipt=" + result.Item.ToString("X16") +
                        "; record=" + world.Channel.Records.ContainsKey(result.Item) + "; held=" + heldId.ToString("X16") +
                        "; native=" + (item == null ? "null" : item.GetType().Name) + "; quotedColour=" + purchasedColor);
                    return false;
                }
                int canonicalColor = world.Items.TestState(result.Item).Values.IntValue1;
                Require(item is Pack_Bolts12 && item.IntValue1 == purchasedColor &&
                    canonicalColor == purchasedColor,
                    "Guest bolt colour mismatch; quoted=" + purchasedColor + "; native=" + item.IntValue1 +
                    "; canonical=" + canonicalColor + "; listing=" + bolt.CustomIntValue1 + "; type=" + item.GetType().Name);
                Debug.Log("MonMP TravelingVan guest delivered native pack; quoted=" + purchasedColor + "; native=" + item.IntValue1 +
                    "; canonical=" + canonicalColor + "; item=" + heldId.ToString("X16"));
                world.Items.TestDrop(false); Signal(2); phase = 3;
            }
            else if (phase == 3 && !van.isOpen && !Visible && Parity(shared)) { Signal(3); phase = 4; }
            else if (phase == 4 && van.isOpen && Visible && Parity(shared) &&
                Math.Abs(van.transform.position.y - Field<Vector3>(van, "PositionWhileOpen").y) < 0.05f)
            {
                Require(world.Stores.TestState(listing).Available && bolt.gameObject.activeSelf,
                    "Native restock did not re-enable its bolt listing.");
                restock = shared.TestNativeState;
                // Simulate a local colour/spray reset; this must converge without running OpenStore.
                bolt.CustomIntValue1 = (bolt.CustomIntValue1 + 1) % Enum.GetValues(typeof(BoltColor)).Length;
                foreach (var spray in shared.TestSprays) spray.SetActive(false);
                phase = 8; next = Time.unscaledTime + 0.4f;
            }
            else if (phase == 8 && Parity(shared))
            {
                Require(shared.TestNativeState.Take(4).SequenceEqual(restock.Take(4)), "Guest repair changed the host roll.");
                disconnected = shared; Signal(4); phase = 9;
            }
            else if (phase == 9 && stageAcknowledged)
            {
                plugin.EndPairedTestSession(); Detach(); Require(disconnected!.TestRestored, "Disconnect did not restore van baseline.");
                phase = 5; next = Time.unscaledTime + 1;
            }
            else if (phase == 6 && Parity(shared) && van.isOpen && Visible)
            {
                Require(plugin.Session!.LocalPeerId != firstPeer && shared.TestNativeState.Take(4).SequenceEqual(restock.Take(4)),
                    "Reconnect failed to seed the same restocked host stock.");
                world.Stores.TestRequest(listing, operation, world.Stores.TestState(listing).Price); phase = 7; next = Time.unscaledTime + 0.6f;
            }
            else if (phase == 7)
            {
                Require(world.Stores.TestSuccessfulReplies > 0 && gameplay.Player.Get_HoldingItem == null,
                    "Reconnect operation replay did not preserve its original purchase result.");
                Signal(5); phase = 10;
            }
            else if (phase == 10 && stageAcknowledged)
            {
                Debug.Log("MonMP TravelingVan guest proved native schedule/roll/colour purchase, restock, repair and reconnect. " + Status);
                disconnected = shared; plugin.EndPairedTestSession(); Detach(); Require(disconnected.TestRestored, "Final guest disconnect restoration failed.");
                Cleanup(); return true;
            }
            return false;
        }

        private void Move(Vector3 position)
        { gameplay.PlayerWalking.transform.position = position; gameplay.Get_Player_Rigidbody.position = position; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero; }
        private void Attach(WorldChannel channel)
        {
            if (subscribed == channel) return;
            Detach(); subscribed = channel;
            if (host) channel.CommandRequested += Command; else channel.ReplyReceived += Reply;
        }
        private void Detach()
        { if (subscribed != null) { subscribed.CommandRequested -= Command; subscribed.ReplyReceived -= Reply; } subscribed = null; }
        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (peer == 0 || entity != ClockState.Entity || payload.Length != 2 || payload[0] != Marker || payload[1] < 1 || payload[1] > 5) return;
            if (firstPeer == 0) firstPeer = peer;
            guestPeer = peer; acknowledgment = Math.Max(acknowledgment, payload[1]);
            subscribed!.Reply(peer, entity, new[] { Marker, payload[1] });
        }
        private void Reply(ulong entity, byte[] payload)
        { if (entity == ClockState.Entity && payload.Length == 2 && payload[0] == Marker && payload[1] == pendingStage) stageAcknowledged = true; }
        private void Signal(byte stage) { pendingStage = stage; stageAcknowledged = false; retryAt = 0; RetryStage(); }
        private void RetryStage()
        {
            if (host || pendingStage == 0 || stageAcknowledged || subscribed == null || Time.unscaledTime < retryAt) return;
            subscribed.Request(ClockState.Entity, new[] { Marker, pendingStage }); retryAt = Time.unscaledTime + 0.4f;
        }
        internal void Cleanup()
        {
            if (cleaned) return;
            var failures = new List<Exception>();
            void Restore(Action action) { try { action(); } catch (Exception error) { failures.Add(error); } }
            try
            {
                if (host && van != null)
                {
                    foreach (var motion in motions) Restore(() => van.StopCoroutine(motion));
                    Restore(() => { van.transform.position = originalPosition; van.isOpen = originalOpen; });
                    foreach (var pair in activity) if (pair.Key != null) Restore(() => pair.Key.SetActive(pair.Value));
                    foreach (var pair in colors) if (pair.Key != null) Restore(() => pair.Key.CustomIntValue1 = pair.Value);
                    foreach (var pair in materials) if (pair.Key != null) Restore(() => pair.Key.sharedMaterials = pair.Value);
                    Restore(() => { AccessTools.Field(typeof(Cash_Manager), "cash").SetValue(cash, originalCash);
                        GameManager.Get_PlayerStats[4] = originalSpent; GameManager.Get_CashSpentAtRoger = originalRogerSpent;
                        Object.FindObjectOfType<UI_ShowCash>()?.UpdateCash(originalCash); sky.TimeFlow = originalFlow; });
                    foreach (var id in boughtItems) Restore(() => { var item = plugin.Items?.TestItem(id); if (item != null) Object.Destroy(item.gameObject); });
                    Restore(() => plugin.Items?.DiscoverSoon());
                }
                Restore(() => { Move(originalPlayerPosition); gameplay.PlayerWalking.transform.rotation = originalPlayerRotation; });
            }
            finally { Detach(); guard?.UnpatchSelf(); guard = null; if (nativeOwner == this) nativeOwner = null; cleaned = true; }
            if (failures.Count > 0) throw new AggregateException("Traveling van fixture restoration failed.", failures);
        }
        private static T Field<T>(object target, string name) => (T)AccessTools.Field(target.GetType(), name).GetValue(target);
        private static void Require([DoesNotReturnIf(false)] bool ok, string message)
        { if (!ok) throw new InvalidOperationException("Traveling van test: " + message); }
    }
}
