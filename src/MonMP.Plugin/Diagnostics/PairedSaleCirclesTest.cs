using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Every native sale circle, one accepted item type at a time: the host first sells its own copy
    // (the single-player baseline for this save), then the guest carries a tagged copy into the same
    // circle and drops it. A circle the host can sell into must also take the guest's drop. Circles
    // this save's progress keeps closed (a quest, an empty keg) are reported, not failed.
    internal sealed class PairedSaleCirclesTest
    {
        private sealed class Circle
        {
            public MonoBehaviour Reception = null!;
            public Type ItemType = null!;
            public ushort Prefab;
            public float Tag;
            public string Name => Reception.GetType().Name + "/" + ItemType.Name;
        }

        private static readonly (Type Reception, Type[] Items)[] Accepts =
        {
            (typeof(Store_Sawmill_ReceptionWoodLogs), new[] { typeof(Item_WoodLog) }),
            (typeof(SugarShack_ReceptionWoodLogs), new[] { typeof(Item_WoodLog) }),
            (typeof(BrotherHouse_ReceptionFirewood), new[] { typeof(Item_FireWood) }),
            (typeof(Store_FederationSyrup_Reception), new[] { typeof(Item_WoodenKeg) }),
            (typeof(Store_IslandHouses_Reception), new[] { typeof(Item_WoodenKeg) }),
            (typeof(Store_Bar_MapleWineReception), new[] { typeof(Item_MapleWineBottle), typeof(Item_WoodenKeg) }),
            (typeof(Store_PizzaSnack_FriesReception), new[] { typeof(Item_FrenchFries) }),
            (typeof(Store_FarmKiosque_ReceptionFish), new[] { typeof(Item_Fish) }),
            (typeof(Store_Dealer_WeedReception), new[] { typeof(Item_WeedPreRollBigPack), typeof(Item_WeedPreRollHugePack) }),
        };

        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly List<Circle> circles = new List<Circle>();
        private readonly List<string> results = new List<string>();
        private readonly List<string> failures = new List<string>();
        private int index, step;
        private float next, stepDeadline;
        private InteractableItems? baseline;
        private ulong baselineId, guestId;
        private bool hostSold, seenTaken, hadGuest;
        private float droppedAt;
        private int cashBefore;
        internal string Summary => string.Join("; ", results);
        internal string Status => "circle " + index + "/" + circles.Count + " step=" + step +
            (index < circles.Count ? " " + circles[index].Name : "") + "; " + Summary;
        internal PairedSaleCirclesTest(MonMPPlugin plugin, Gameplay gameplay) { this.plugin = plugin; this.gameplay = gameplay; }

        private void Discover()
        {
            var items = plugin.Items!;
            int n = 0;
            foreach (var (receptionType, types) in Accepts)
                foreach (MonoBehaviour reception in UnityEngine.Object.FindObjectsOfType(receptionType, true)
                    .OfType<MonoBehaviour>().Where(r => r.gameObject.scene.IsValid())
                    .OrderBy(r => RuntimeProbe.CanonicalPath(r.transform), StringComparer.Ordinal))
                    foreach (var type in types)
                    {
                        var prefab = items.TestPrefabs(type).FirstOrDefault();
                        if (prefab == 0) { results.Add(receptionType.Name + "/" + type.Name + "=no-prefab"); continue; }
                        circles.Add(new Circle { Reception = reception, ItemType = type, Prefab = prefab, Tag = 0.9f + 0.004f * n++ });
                    }
        }

        private static Collider? Trigger(Circle c) => c.Reception.GetComponentsInChildren<Collider>(true)
            .FirstOrDefault(col => col != null && col.isTrigger);
        private static Vector3 Center(Circle c) => Trigger(c)?.bounds.center ?? c.Reception.transform.position;
        private static Vector3 Aside(Circle c)
        {
            var trigger = Trigger(c);
            float reach = trigger == null ? 2f : trigger.bounds.extents.magnitude + 2f;
            return Center(c) + c.Reception.transform.forward * reach + Vector3.up * 0.5f;
        }
        private int Cash => UnityEngine.Object.FindObjectOfType<Cash_Manager>()?.GetCash ?? 0;

        private InteractableItems Spawn(Circle c, Vector3 at, float scale)
        {
            var item = plugin.Items!.TestSpawn(at, c.Prefab);
            if (scale > 0) item.transform.localScale = Vector3.one * scale;
            item.isSpawnFromTheSaveLoadSystem = true;
            item.CreateBasics();
            plugin.Items.DiscoverSoon();
            return item;
        }

        private void Offer(Circle c, InteractableItems item)
        {
            item.transform.position = Center(c);
            if (item.itemCollider == null) return;
            var enter = AccessTools.Method(c.Reception.GetType(), "OnTriggerEnter", new[] { typeof(Collider) });
            try { enter?.Invoke(c.Reception, new object[] { item.itemCollider }); }
            catch (Exception ex) { Debug.LogWarning("MonMP sale circle " + c.Name + " threw: " + (ex.InnerException ?? ex).Message); }
        }

        private bool Gone(ulong id) => id != 0 && !plugin.Items!.TestState(id).Exists;

        internal bool Tick()
        {
            if (Time.unscaledTime < next) return false;
            var items = plugin.Items!;
            if (circles.Count == 0 && results.Count == 0) { Discover(); if (circles.Count == 0) throw new InvalidOperationException("No sale circles found in the scene."); }
            if (index >= circles.Count)
            {
                if (plugin.TestOptions.IsHost)
                {
                    if (plugin.Session!.Peers.Keys.Any(peer => peer != 0)) return false;
                    Debug.Log("MonMP sale circles: " + Summary);
                    if (failures.Count != 0) throw new InvalidOperationException("Guest drops were not sold where the host could sell: " + string.Join("; ", failures) + " | " + Summary);
                    plugin.EndPairedTestSession();
                }
                else plugin.EndPairedTestSession();
                return true;
            }
            var c = circles[index];
            return plugin.TestOptions.IsHost ? HostTick(c, items) : GuestTick(c, items);
        }

        private bool HostTick(Circle c, Replication.SharedItems items)
        {
            uint guest = plugin.Session!.Peers.Keys.FirstOrDefault(peer => peer != 0);
            if (guest == 0)
            {
                // A store can open on the host (the dealer's hours) after the guest skipped it as closed
                // and finished; the remaining circles have no guest to carry for them.
                if (hadGuest) { while (index < circles.Count) { results.Add(circles[index].Name + "=guest-left"); index++; } }
                return false;
            }
            hadGuest = true;
            switch (step)
            {
                case 0:
                    Stand(Aside(c) + c.Reception.transform.right * 2f);
                    step = 1; next = Time.unscaledTime + 1.5f; return false;
                case 1:
                    if (!c.Reception.gameObject.activeInHierarchy)
                    { results.Add(c.Name + "=inactive"); Advance(); return false; }
                    cashBefore = Cash;
                    baseline = Spawn(c, Aside(c), 0f); baselineId = 0;
                    step = 2; stepDeadline = Time.unscaledTime + 5f; return false;
                case 2:
                    baselineId = baseline == null ? 0 : items.TestBoundId(baseline);
                    if (baselineId == 0) { if (Time.unscaledTime > stepDeadline) { results.Add(c.Name + "=unbound"); Advance(); } return false; }
                    Offer(c, baseline!);
                    step = 3; stepDeadline = Time.unscaledTime + 2f; return false;
                case 3:
                    hostSold = Gone(baselineId) || baseline == null;
                    if (!hostSold && Time.unscaledTime < stepDeadline) return false;
                    int hostEarned = Cash - cashBefore;
                    // Out of the circle so it does not stand in for the guest's copy.
                    if (!hostSold && baseline != null) baseline.transform.position = Aside(c) - c.Reception.transform.forward * 4f;
                    results.Add(c.Name + " host=" + (hostSold ? "sold(" + hostEarned + ")" : "kept"));
                    cashBefore = Cash;
                    Spawn(c, Aside(c), c.Tag); guestId = 0; seenTaken = false; droppedAt = 0;
                    step = 4; stepDeadline = Time.unscaledTime + 25f; return false;
                case 4:
                    if (guestId == 0) guestId = items.TestTaggedItem(c.Prefab, c.Tag);
                    bool taken = guestId != 0 && items.TestState(guestId).Lease.Owner == guest;
                    bool sold = Gone(guestId);
                    if (taken) seenTaken = true;
                    else if (seenTaken && droppedAt == 0) droppedAt = Time.unscaledTime;
                    bool settled = droppedAt > 0 && Time.unscaledTime - droppedAt > 5f;
                    if (!sold && !settled && Time.unscaledTime < stepDeadline) return false;
                    string outcome = sold ? "sold(" + (Cash - cashBefore) + ")" : guestId == 0 ? "unbound" : "kept(" + (Cash - cashBefore) + ")";
                    results[results.Count - 1] += " guest=" + outcome;
                    if (hostSold && !sold) { failures.Add(c.Name); Debug.LogWarning("MonMP sale circles: guest copy kept at " + c.Name + ": " + Describe(c, items.ItemOf(guestId))); }
                    Advance(); return false;
            }
            return false;
        }

        private float carryUntil;
        private bool GuestTick(Circle c, Replication.SharedItems items)
        {
            switch (step)
            {
                case 0:
                    // The host skips circles that are closed on this save; skip them here too.
                    if (!c.Reception.gameObject.activeInHierarchy) { Advance(); return false; }
                    guestId = items.TestTaggedItem(c.Prefab, c.Tag);
                    if (guestId == 0 || items.ItemOf(guestId) == null)
                    {
                        if (LaterCircleStarted(items)) { results.Add(c.Name + "=missed"); Advance(); return false; }
                        // The host moved on (or never reached this circle's guest copy): follow along.
                        if (stepDeadline == 0) stepDeadline = Time.unscaledTime + 30f;
                        if (Time.unscaledTime > stepDeadline) { results.Add(c.Name + "=missed"); Advance(); }
                        return false;
                    }
                    Stand(items.ItemOf(guestId)!.transform.position + Vector3.forward);
                    step = 1; next = Time.unscaledTime + 0.7f; return false;
                case 1:
                    if (items.ItemOf(guestId) == null) { Advance(); return false; }
                    items.TestGrab(guestId);
                    step = 2; stepDeadline = Time.unscaledTime + 5f; return false;
                case 2:
                    if (items.TestState(guestId).Lease.Owner != plugin.Session!.LocalPeerId)
                    { if (Time.unscaledTime > stepDeadline) { results.Add(c.Name + "=not-grabbed"); Advance(); } return false; }
                    carryUntil = Time.unscaledTime + 1.2f; step = 3; return false;
                case 3:
                    if (items.ItemOf(guestId) is not InteractableItems held) { results.Add(c.Name + "=sold-in-hand"); Advance(); return false; }
                    Stand(Center(c) + c.Reception.transform.forward);
                    held.transform.position = Center(c);
                    if (Time.unscaledTime < carryUntil) return false;
                    if (items.TestState(guestId).Lease.Owner == plugin.Session!.LocalPeerId) items.TestDrop(false);
                    step = 4; stepDeadline = Time.unscaledTime + 7f; return false;
                case 4:
                    if (!Gone(guestId) && Time.unscaledTime < stepDeadline) return false;
                    results.Add(c.Name + "=" + (Gone(guestId) ? "sold" : "kept"));
                    Advance(); return false;
            }
            return false;
        }

        private bool LaterCircleStarted(Replication.SharedItems items)
        {
            for (int i = index + 1; i < circles.Count; i++)
                if (items.TestTaggedItem(circles[i].Prefab, circles[i].Tag) != 0) return true;
            return false;
        }

        private static string Describe(Circle c, InteractableItems? item)
        {
            if (item == null) return "no host copy";
            var trigger = Trigger(c);
            var body = item.rb;
            return "at " + item.transform.position.ToString("F2") + " trigger=" + (trigger == null ? "none" : trigger.bounds.ToString("F2") +
                " inside=" + trigger.bounds.Contains(item.transform.position)) + " parent=" + (item.transform.parent == null ? "none" : item.transform.parent.name) +
                " kinematic=" + (body == null ? "no-body" : body.isKinematic.ToString()) + " collider=" + (item.itemCollider == null ? "none" :
                item.itemCollider.enabled + "/trigger=" + item.itemCollider.isTrigger);
        }

        private void Advance()
        {
            Debug.Log("MonMP sale circles progress: " + (results.Count == 0 ? "" : results[results.Count - 1]) + " (" + (index + 1) + "/" + circles.Count + ")");
            index++; step = 0; stepDeadline = 0; next = Time.unscaledTime + 0.5f;
        }

        private void Stand(Vector3 at)
        {
            gameplay.PlayerWalking.transform.position = at;
            if (gameplay.Get_Player_Rigidbody != null) { gameplay.Get_Player_Rigidbody.position = at; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero; }
        }
    }
}
