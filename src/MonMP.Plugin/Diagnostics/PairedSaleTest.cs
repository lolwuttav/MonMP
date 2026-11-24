using System;
using System.Linq;
using HarmonyLib;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // The host sells a shared wood log to the sawmill; the guest's copy must slide onto the
    // sawmill palette (the native presentation) instead of vanishing.
    internal sealed class PairedSaleTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private Store_Sawmill? sawmill;
        private Store_Sawmill_ReceptionWoodLogs? reception;
        private InteractableItems? hostLog;
        private GameObject? guestLog;
        private InteractableItems? hostHeldLog;
        private GameObject? heldGuestLog;
        private ulong log, heldLog;
        private int phase;
        private float next, deadline;
        private float heldAt;
        private bool heldTriggerAttempted;
        private ushort heldLogPrefab;
        private const float HeldLogScale = 0.93f;
        internal string Status => "sale phase=" + phase + "; log=" + log.ToString("X16") +
            "; held=" + heldLog.ToString("X16") + "; attempted=" + heldTriggerAttempted +
            (guestLog == null ? "" : "; guestParent=" + guestLog.transform.parent?.name);
        internal PairedSaleTest(MonMPPlugin plugin, Gameplay gameplay) { this.plugin = plugin; this.gameplay = gameplay; }

        private Transform Palette => (Transform)AccessTools.Field(typeof(Store_Sawmill), nameof(Store_Sawmill.Palette)).GetValue(sawmill);
        private Vector3 DropPoint => Palette.position + Vector3.up * 1.5f + sawmill!.transform.right * 2f;
        private Store_Sawmill_ReceptionWoodLogs Reception => reception ??= UnityEngine.Object.FindObjectOfType<Store_Sawmill_ReceptionWoodLogs>()!;
        private Collider ReceptionTrigger => Reception.GetComponentsInChildren<Collider>(true)
            .First(c => c != null && c.enabled && c.isTrigger);
        private Vector3 ReceptionCenter => ReceptionTrigger.bounds.center;

        internal bool Tick()
        {
            var items = plugin.Items!;
            if (deadline == 0) deadline = Time.unscaledTime + 120f;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Sale test stalled: " + Status);
            if (Time.unscaledTime < next) return false;
            sawmill ??= UnityEngine.Object.FindObjectOfType<Store_Sawmill>();
            if (sawmill == null) return false;
            if (plugin.TestOptions.IsHost)
            {
                switch (phase)
                {
                    case 0:
                        Stand(DropPoint + sawmill.transform.forward * 3f);
                        hostLog = items.TestSpawn(DropPoint, items.TestPrefabs(typeof(Item_WoodLog)).First());
                        phase = 1; next = Time.unscaledTime + 1f; return false;
                    case 1:
                        log = items.TestBoundId(hostLog!);
                        if (log == 0 || !plugin.Session!.Peers.Keys.Any(peer => peer != 0)) return false;
                        // Let the guest bind its copy before the sale.
                        phase = 2; next = Time.unscaledTime + 6f; return false;
                    case 2:
                        sawmill.Receive((Item_WoodLog)hostLog!);
                        Require(!items.TestState(log).Exists || hostLog == null || hostLog.transform.IsChildOf(sawmill.transform), "The sawmill refused the host log.");
                        phase = 3; next = Time.unscaledTime + 1f; return false;
                    case 3:
                        Require(!items.TestState(log).Exists, "The host kept the sold log as a shared item.");
                        Require((items.TestState(log).Values.Flags & Core.World.ItemFlags.Sold) != 0, "The host did not mark the sold log Sold.");
                        phase = 4; return false;
                    case 4:
                        if (!plugin.Session!.Peers.Keys.Any(peer => peer != 0)) return false;
                        heldLogPrefab = items.TestPrefabs(typeof(Item_WoodLog)).First();
                        var spawn = ReceptionCenter + Reception.transform.forward * (ReceptionTrigger.bounds.extents.magnitude + 2f);
                        hostHeldLog = items.TestSpawn(spawn, heldLogPrefab);
                        hostHeldLog.transform.localScale = Vector3.one * HeldLogScale;
                        hostHeldLog.isSpawnFromTheSaveLoadSystem = true;
                        hostHeldLog.CreateBasics();
                        items.DiscoverSoon();
                        phase = 5; return false;
                    case 5:
                        // Keep the identity after the native sale replaces its loose-item state.
                        if (heldLog == 0) heldLog = items.TestTaggedItem(heldLogPrefab, HeldLogScale);
                        if (heldLog == 0) return false;
                        var state = items.TestState(heldLog);
                        uint guest = plugin.Session!.Peers.Keys.FirstOrDefault(peer => peer != 0);
                        if (guest != 0 && state.Lease.Owner == guest)
                        {
                            Require(state.Exists, "The sawmill consumed a remote-held log before its owner dropped it.");
                            if (items.ItemOf(heldLog) is not Item_WoodLog held) return false;
                            hostHeldLog = held;
                            if (!heldTriggerAttempted)
                            {
                                held.transform.position = ReceptionCenter;
                                AccessTools.Method(typeof(Store_Sawmill_ReceptionWoodLogs), "OnTriggerEnter", new[] { typeof(Collider) })!
                                    .Invoke(Reception, new object[] { held.itemCollider });
                                Require(items.TestState(heldLog).Exists && items.TestState(heldLog).Lease.Owner == guest,
                                    "The sawmill consumed a remote-held log before its owner dropped it.");
                                heldTriggerAttempted = true; heldAt = Time.unscaledTime;
                            }
                            else if (Time.unscaledTime - heldAt > 0.75f)
                                Require(items.TestState(heldLog).Exists, "The sawmill consumed a remote-held log before release.");
                            return false;
                        }
                        if (!heldTriggerAttempted || state.Lease.Owner != ItemLease.Unowned) return false;
                        phase = 6; next = Time.unscaledTime + 0.5f; return false;
                    case 6:
                        if (items.TestState(heldLog).Exists) return false;
                        Require((items.TestState(heldLog).Values.Flags & ItemFlags.Sold) != 0,
                            "Dropping inside the sawmill receiver did not replay the native sale.");
                        phase = 7; return false;
                    case 7:
                        if (plugin.Session!.Peers.Keys.Any(peer => peer != 0)) return false;
                        plugin.EndPairedTestSession(); return true;
                }
                return false;
            }
            switch (phase)
            {
                case 0:
                    Stand(DropPoint + sawmill.transform.forward * 4f);
                    phase = 1; return false;
                case 1:
                    foreach (ushort prefab in items.TestPrefabs(typeof(Item_WoodLog)))
                    {
                        ulong id = items.TestNewestPrefab(prefab);
                        if (id == 0 || !items.TestState(id).Exists) continue;
                        var copy = items.TestItem(id);
                        if (copy != null && Vector3.Distance(copy.transform.position, DropPoint) < 4f) { log = id; guestLog = copy.gameObject; break; }
                    }
                    if (log == 0) return false;
                    phase = 2; return false;
                case 2:
                    if (items.TestState(log).Exists) return false;
                    Require(guestLog != null, "The guest deleted its sold log instead of presenting the sale.");
                    Require(guestLog!.transform.IsChildOf(Palette), "The guest's sold log did not move onto the sawmill palette.");
                    phase = 3; next = Time.unscaledTime + 1.5f; return false;
                case 3:
                    Require(guestLog != null && guestLog.transform.IsChildOf(Palette), "The guest's sold log left the palette.");
                    Require(Vector3.Distance(guestLog!.transform.position, Palette.position) < 3f, "The guest's sold log never slid onto the palette.");
                    phase = 4; return false;
                case 4:
                    heldLog = items.TestTaggedItem(heldLogPrefab == 0 ? items.TestPrefabs(typeof(Item_WoodLog)).First() : heldLogPrefab, HeldLogScale);
                    if (heldLog == 0 || items.ItemOf(heldLog) is not Item_WoodLog held) return false;
                    heldGuestLog = held.gameObject;
                    Stand(held.transform.position + Vector3.forward);
                    phase = 40; next = Time.unscaledTime + 0.7f; return false;
                case 40:
                    if (items.ItemOf(heldLog) is not Item_WoodLog readyToGrab) return false;
                    items.TestGrab(heldLog); phase = 5; return false;
                case 5:
                    if (items.TestState(heldLog).Lease.Owner != plugin.Session!.LocalPeerId) return false;
                    Require(gameplay.Player.Get_HoldingItem == items.ItemOf(heldLog), "The guest did not hold the second sawmill log.");
                    Stand(ReceptionCenter + Vector3.forward);
                    items.ItemOf(heldLog)!.transform.position = ReceptionCenter;
                    heldAt = Time.unscaledTime; phase = 6; return false;
                case 6:
                    if (items.ItemOf(heldLog) is not Item_WoodLog heldAgain) return false;
                    Stand(ReceptionCenter + Vector3.forward);
                    heldAgain.transform.position = ReceptionCenter;
                    if (Time.unscaledTime - heldAt < 1f) return false;
                    Require(items.TestState(heldLog).Lease.Owner == plugin.Session!.LocalPeerId,
                        "The held sawmill log was released before the trigger guard check.");
                    items.TestDrop(false); phase = 7; return false;
                case 7:
                    if (items.TestState(heldLog).Exists) return false;
                    Require(heldGuestLog != null, "The guest deleted its released sawmill log instead of presenting the sale.");
                    Require(heldGuestLog!.transform.IsChildOf(Palette), "The released guest log did not move onto the sawmill palette.");
                    phase = 8; next = Time.unscaledTime + 1.5f; return false;
                case 8:
                    Require(heldGuestLog != null && heldGuestLog.transform.IsChildOf(Palette),
                        "The released guest log left the sawmill palette while its native sale presentation settled.");
                    Require(Vector3.Distance(heldGuestLog!.transform.position, Palette.position) < 3f,
                        "The released guest log did not finish the native sawmill presentation.");
                    plugin.EndPairedTestSession(); return true;
            }
            return false;
        }
        private void Stand(Vector3 at)
        {
            gameplay.PlayerWalking.transform.position = at;
            if (gameplay.Get_Player_Rigidbody != null) { gameplay.Get_Player_Rigidbody.position = at; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero; }
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
