using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // Sale drop-offs (sawmill logs, kiosk fish, syrup barrels, firewood for the brother) slide the
    // item onto a palette or into a freezer. The host runs the sale; a guest receives the item's
    // removal marked Sold and plays the same native slide on its own copy, with no payment.
    internal static class SalePresentation
    {
        // Francis's garage palette and the dealer's dock sit well away from their store's origin; the
        // nearest store is taken, so the reach only rules out a store on the far side of the map (#190).
        private const float StoreReach = 1500f;
        private static readonly MethodInfo? SawmillMove = AccessTools.Method(typeof(Store_Sawmill), "MoveLogToPalette", new[] { typeof(Item_WoodLog) });
        private static readonly MethodInfo? KioskMove = AccessTools.Method(typeof(Store_FarmKiosque), "MoveFishToFreezerAndDelete", new[] { typeof(GameObject) });
        private static readonly MethodInfo? FederationMove = AccessTools.Method(typeof(Store_FederationSyrup), "Move_barrelToPalette", new[] { typeof(GameObject) });
        private static readonly MethodInfo? IslandMove = AccessTools.Method(typeof(Store_IslandHouses), "Move_barrelToPalette", new[] { typeof(GameObject) });
        private static readonly MethodInfo? BrotherMove = AccessTools.Method(typeof(BrotherHouse), "MoveLogToPalette", new[] { typeof(Item_FireWood), typeof(bool) });
        private static readonly MethodInfo? FriesMove = AccessTools.Method(typeof(Store_PizzaSnack), "Move_barrelToPalette", new[] { typeof(GameObject) });
        private static readonly MethodInfo? BigPackMove = AccessTools.Method(typeof(Store_Dealer), "Move_BigPackToPalette", new[] { typeof(GameObject) });
        private static readonly MethodInfo? HugePackMove = AccessTools.Method(typeof(Store_Dealer), "Move_HugePackToPalette", new[] { typeof(GameObject), typeof(bool) });
        private static readonly MethodInfo? BarMove = AccessTools.Method(typeof(Store_Bar), "MoveMapleWineToInteriorAndDelete", new[] { typeof(GameObject) });
        private static readonly FieldInfo? WeedHuge = AccessTools.Field(typeof(Store_Dealer_WeedReception), "isHugePack");
        private static readonly FieldInfo? WeedDock = AccessTools.Field(typeof(Store_Dealer_WeedReception), "isFromDock");
        private static readonly FieldInfo? PostStart = AccessTools.Field(typeof(Store_PostOffice), "EnveloppeOriginalPosition");
        private static readonly FieldInfo? PostEnd = AccessTools.Field(typeof(Store_PostOffice), "EnveloppePosition");
        private static readonly FieldInfo? PostAnimator = AccessTools.Field(typeof(Store_PostOffice), "Animator_ReceptionBox");
        internal static readonly FieldInfo? BrotherGaragePalette = AccessTools.Field(typeof(BrotherHouse), "GarageStartedPalette");

        internal static FieldInfo[] HealthFields() => new[] { WeedHuge, WeedDock, PostStart, PostEnd, PostAnimator, ReceptionBrother, ReceptionGarage }
            .Select(f => f ?? throw new InvalidOperationException("A native sale presentation field is missing.")).ToArray()!;

        internal static MethodBase[] HealthTargets() => new MethodBase?[] { SawmillMove, KioskMove, FederationMove, IslandMove, BrotherMove, FriesMove, BigPackMove, HugePackMove, BarMove }
            .Select(m => m ?? throw new InvalidOperationException("A native sale presentation coroutine is missing.")).ToArray()!;

        // Guest: returns true when the native slide now owns the object (it is not destroyed here).
        internal static bool Present(InteractableItems item)
        {
            try
            {
                var position = item.transform.position;
                MonoBehaviour? store = null;
                IEnumerator? slide = null;
                switch (item)
                {
                    case Item_WoodLog log when Nearest<Store_Sawmill>(position) is { } sawmill:
                        store = sawmill; slide = SawmillMove?.Invoke(sawmill, new object[] { log }) as IEnumerator; break;
                    case Item_Fish fish when Nearest<Store_FarmKiosque>(position) is { } kiosk:
                        store = kiosk; slide = KioskMove?.Invoke(kiosk, new object[] { fish.gameObject }) as IEnumerator; break;
                    case Item_Barrel120 barrel:
                        var federation = Nearest<Store_FederationSyrup>(position);
                        var island = Nearest<Store_IslandHouses>(position);
                        if (federation != null && (island == null || Distance(federation, position) <= Distance(island, position)))
                        { store = federation; slide = FederationMove?.Invoke(federation, new object[] { barrel.gameObject }) as IEnumerator; }
                        else if (island != null)
                        {
                            store = island; slide = IslandMove?.Invoke(island, new object[] { barrel.gameObject }) as IEnumerator;
                            // Native Store_IslandHouses.Receive grants this on the host only.
                            GuestAchievements.Award(AchievementType.SellMapleSyrupBarelOnBigIsland);
                        }
                        break;
                    case Item_FrenchFries fries when Nearest<Store_PizzaSnack>(position) is { } snack:
                        store = snack; slide = FriesMove?.Invoke(snack, new object[] { fries.gameObject }) as IEnumerator; break;
                    case Item_WeedPreRollBigPack big when Nearest<Store_Dealer>(position) is { } dealer:
                        store = dealer; slide = BigPackMove?.Invoke(dealer, new object[] { big.gameObject }) as IEnumerator; break;
                    case Item_WeedPreRollHugePack huge when Nearest<Store_Dealer>(position) is { } dealer:
                        // The dock's reception is a slow 8 s crane ride; the green circle's is a 1 s slide.
                        store = dealer; slide = HugePackMove?.Invoke(dealer, new object[] { huge.gameObject, FromDock(position) }) as IEnumerator;
                        // Big money: native ReceiveHugePack completes this line on the host only, and only once per world.
                        GuestAchievements.AwardToDo(ToDoList_Line.Dealer_HugePackSold);
                        break;
                    case Item_MapleWineBottle wine when Nearest<Store_Bar>(position) is { } bar:
                        store = bar; slide = BarMove?.Invoke(bar, new object[] { wine.gameObject }) as IEnumerator; break;
                    case Pack_MapleSyrupCrate crate when Nearest<Store_Bar>(position) is { } bar:
                        store = bar; slide = BarMove?.Invoke(bar, new object[] { crate.gameObject }) as IEnumerator; break;
                    case Item_FireWood firewood when FirewoodReception(position, out var brother, out bool forGarage, out var runner):
                        // The circle runs the slide: it is next to the player, the brother's house may be far off.
                        store = runner; slide = BrotherMove?.Invoke(brother, new object[] { firewood, forGarage }) as IEnumerator; break;
                }
                if (store == null || slide == null)
                {
                    // #190: a sold item with no store slide here vanishes; say which, for the next retest.
                    Debug.LogWarning("MonMP sale presentation: no store slide for " + item.GetType().Name + " at " + position +
                        (item is Item_FireWood ? " (reception " + FirewoodReception(position, out _, out _, out _) + ")" : ""));
                    return false;
                }
                if (item is Item_WoodLog placedLog) placedLog.isPlaced = true;
                if (item is Item_FireWood placedWood) placedWood.isPlaced = true;
                // The slide picks its palette slot before its first yield; on a guest the garage
                // counter is the host's, so it must not advance here.
                var garage = Singleton<HomeGarage>.i;
                int garageWood = garage != null ? garage.Get_FirewoodAmountAtBrotherHouse : 0;
                store.StartCoroutine(slide);
                if (garage != null) garage.Get_FirewoodAmountAtBrotherHouse = garageWood;
                if (item.rb != null) Object.Destroy(item.rb);
                if (item.itemCollider != null) Object.Destroy(item.itemCollider);
                Object.Destroy(item);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("MonMP sale presentation failed: " + ex.Message);
                return false;
            }
        }

        // The green circle the wood was dropped in: Francis's house palette or the garage storage, which
        // sits far from the BrotherHouse origin. Picking the store by its origin within reach missed the
        // garage, so a guest's sold wood shrank away while the host's slid into storage (#190).
        private static readonly FieldInfo? ReceptionBrother = AccessTools.Field(typeof(BrotherHouse_ReceptionFirewood), "brotherHouse");
        private static readonly FieldInfo? ReceptionGarage = AccessTools.Field(typeof(BrotherHouse_ReceptionFirewood), "isForTheGarage");
        private static bool FirewoodReception(Vector3 position, out BrotherHouse brother, out bool forGarage, out MonoBehaviour runner)
        {
            brother = null!; forGarage = false; runner = null!;
            var reception = Object.FindObjectsOfType<BrotherHouse_ReceptionFirewood>()
                .OrderBy(r => (r.transform.position - position).sqrMagnitude).FirstOrDefault();
            if (reception != null && ReceptionBrother?.GetValue(reception) is BrotherHouse owner && owner != null)
            {
                brother = owner; forGarage = ReceptionGarage?.GetValue(reception) is true;
                runner = owner.gameObject.activeInHierarchy ? owner : reception; return true;
            }
            var fallback = Object.FindObjectsOfType<BrotherHouse>().OrderBy(b => Distance(b, position)).FirstOrDefault();
            if (fallback == null) return false;
            // The garage palette only shows while the brother is collecting wood for it.
            brother = fallback; runner = fallback; forGarage = BrotherGaragePalette?.GetValue(fallback) is GameObject palette && palette.activeSelf;
            return true;
        }

        private static bool FromDock(Vector3 position) =>
            Object.FindObjectsOfType<Store_Dealer_WeedReception>().Where(r => WeedHuge?.GetValue(r) is true)
                .OrderBy(r => Distance(r, position)).FirstOrDefault() is { } reception && WeedDock?.GetValue(reception) is true;

        // Guest: the host accepted this guest's letter and plays the native post on its own copy; the
        // guest's copy stayed in its hand until the host removed it. Play the box's slide on it without
        // the native coroutine, which would run the host's shipping effects (bill paid, construction).
        internal static void PresentPost(Tool_Envelope envelope)
        {
            try
            {
                var office = Object.FindObjectOfType<Store_PostOffice>();
                if (office == null || envelope == null || !(PostStart?.GetValue(office) is Transform start) || !(PostEnd?.GetValue(office) is Transform end)) return;
                Singleton<Gameplay>.i.Player.CheckIfHoldingItemToCancel(envelope);
                envelope.ForceCloseLetter();
                if (envelope.rb != null) envelope.rb.isKinematic = true;
                if (envelope.itemCollider != null) envelope.itemCollider.enabled = false;
                (PostAnimator?.GetValue(office) as Animator)?.SetTrigger("EnvelopeShipped");
                Singleton<SOUND_Manager>.i.PlaySound_PartTempInstalled();
                office.StartCoroutine(PostSlide(envelope.gameObject, start, end));
            }
            catch (Exception ex) { Debug.LogWarning("MonMP post presentation failed: " + ex.Message); }
        }

        private static IEnumerator PostSlide(GameObject envelope, Transform start, Transform end)
        {
            envelope.transform.SetParent(start);
            envelope.transform.localPosition = Vector3.zero;
            envelope.transform.localRotation = Quaternion.identity;
            yield return new WaitForSeconds(0.5f);
            envelope.transform.SetParent(end.parent);
            for (float t = 0; t < 0.5f && envelope != null; t += Time.deltaTime)
            {
                envelope.transform.SetPositionAndRotation(Vector3.Lerp(start.position, end.position, t / 0.5f), Quaternion.Lerp(start.rotation, end.rotation, t / 0.5f));
                yield return null;
            }
            // The native pause, then the poster's own confirmation (the host no longer shows it on its screen).
            yield return new WaitForSeconds(1f);
            try
            {
                Singleton<SOUND_Manager>.i.PlaySound_BoltScrewingUnInstalled();
                Singleton<UI_EventMessage>.i.Show(TEXT.GetText("POSTOFFICE_LetterPosted"), TEXT.GetText("POSTOFFICE"), 5f);
            }
            catch (Exception ex) { Debug.LogWarning("MonMP post confirmation failed: " + ex.Message); }
            // The host's removal normally arrives first.
            yield return new WaitForSeconds(3f);
            if (envelope != null) Object.Destroy(envelope);
        }

        private static float Distance(Component store, Vector3 position) => (store.transform.position - position).sqrMagnitude;
        private static T? Nearest<T>(Vector3 position) where T : Component =>
            Object.FindObjectsOfType<T>().Where(store => Distance(store, position) <= StoreReach * StoreReach)
                .OrderBy(store => Distance(store, position)).FirstOrDefault();
    }
}
