using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Resource paths and runtime types come only from local, hash-gated assets.
    // Network records select an already validated numeric entry; they never ask
    // Unity to load a remotely supplied path or construct a remotely named type.
    internal sealed class ItemPrefabCatalog
    {
        internal sealed class Entry
        {
            public PrefabDefinition Definition = null!;
            public GameObject Asset = null!;
            public Type Type = null!;
            public int ItemComponents, Rigidbodies;
        }
        private static ItemPrefabCatalog? cached;
        public IReadOnlyDictionary<ushort, Entry> Entries { get; }
        public PrefabCatalog Definitions { get; }
        private ItemPrefabCatalog()
        {
            var assets = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            foreach (var asset in Resources.LoadAll<GameObject>(""))
            {
                var item = asset.GetComponent<InteractableItems>();
                if (item == null || string.IsNullOrWhiteSpace(item.PrefabName)) continue;
                // Some game assets reference a resource prefab while being a scene
                // variant themselves. Only the canonical resource is a factory.
                var canonical = Resources.Load<GameObject>(item.PrefabName);
                if (canonical == null || canonical.GetComponent<InteractableItems>() == null) continue;
                if (canonical.GetComponent<InteractableItems>().PrefabName != item.PrefabName)
                    throw new InvalidOperationException("Resource item identity mismatch: " + item.PrefabName);
                assets[item.PrefabName] = canonical;
            }
            var entries = new Dictionary<ushort, Entry>();
            ushort next = 3;
            foreach (var pair in assets.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                ushort id = pair.Key == "Drink/MapleSyrup" ? (ushort)1 : pair.Key == "Drink/TomHouston_Cup" ? (ushort)2 : next++;
                if (id == 0 || next == 0) throw new InvalidOperationException("Item prefab identity space exhausted.");
                var item = pair.Value.GetComponent<InteractableItems>();
                var features = id <= 2 ? PrefabFeatures.Carry | PrefabFeatures.Consume : PrefabFeatures.Registered;
                if (item.GetType() == typeof(Item_Lantern)) features = PrefabFeatures.Carry | PrefabFeatures.Values | PrefabFeatures.Interact;
                if (item.GetType() == typeof(Item_ElectricCoffeePercolator))
                {
                    features = PrefabFeatures.Carry | PrefabFeatures.Values;
                    if (DecorationState.CanFreeze((Furniture_Decoration)item)) features |= PrefabFeatures.Interact;
                }
                if (item.GetType() == typeof(Item_Poutine))
                { FoodState.ValidatePrefab((Item_Poutine)item); features = PrefabFeatures.Carry | PrefabFeatures.Consume | PrefabFeatures.Values; }
                if (item.GetType() == typeof(Furniture_Decoration))
                {
                    var decoration = (Furniture_Decoration)item; DecorationState.ValidatePrefab(decoration);
                    features = PrefabFeatures.Carry | PrefabFeatures.Values;
                    if (DecorationState.CanFreeze(decoration)) features |= PrefabFeatures.Interact;
                }
                if (item.GetType() == typeof(Garden_OutdoorPlanter)) features = PrefabFeatures.Carry | PrefabFeatures.Values | PrefabFeatures.Interact;
                if (item.GetType() == typeof(Furniture_CarLift))
                {
                    var lift = (Furniture_CarLift)item; DecorationState.ValidatePrefab(lift);
                    features = PrefabFeatures.Carry | PrefabFeatures.Values;
                    if (DecorationState.CanFreeze(lift)) features |= PrefabFeatures.Interact;
                }
                if (Simple(item.GetType()) && SimpleCarry(pair.Value))
                    features = PrefabFeatures.Carry | (SimpleConsumable(item) ? PrefabFeatures.Consume : PrefabFeatures.Registered);
                else if (General(item.GetType()) && SimpleCarry(pair.Value))
                    features = PrefabFeatures.Carry | PrefabFeatures.Values | (HostUse(item.GetType()) ? PrefabFeatures.Interact : 0);
                var entry = new Entry
                {
                    Asset = pair.Value, Type = item.GetType(), ItemComponents = pair.Value.GetComponentsInChildren<InteractableItems>(true).Length,
                    Rigidbodies = pair.Value.GetComponentsInChildren<Rigidbody>(true).Length,
                    Definition = new PrefabDefinition(id, pair.Key, item.GetType().FullName!, Shape(pair.Value.transform), features)
                };
                entries.Add(id, entry);
            }
            Definitions = new PrefabCatalog(entries.Values.Select(e => e.Definition));
            Entries = entries;
        }
        // Reviewed types with no per-instance state or world behavior of their own. Their Use is either
        // absent, the base food/drink effect, or a personal effect that touches only the user's needs.
        private static readonly Type[] CarryTypes = { typeof(Item_Event), typeof(Item_Event_ChristmasStocking), typeof(Item_MapleSyupLids),
            typeof(Item_MapleSyrupCan_Empty), typeof(Item_Driveline_Tires), typeof(Item_CurdsCheese), typeof(Item_FrenchFries) };
        private static readonly Type[] PersonalConsumables = { typeof(Item_MapleWineBottle), typeof(Item_HomemadeCoffeeMug), typeof(Item_WeedPreRollPack), typeof(Item_MapleSyrupCan) };
        // A consumed item the native use replaces with another in the same hand (a drunk syrup can
        // leaves its empty can). The host spawns it and hands it to the consumer.
        internal static GameObject? Leftover(Type type) => type == typeof(Item_MapleSyrupCan) ? Singleton<GameAssets>.i.MapleSyrupCan_Empty : null;
        // General items whose native use only spawns other items and ends itself (joint packs that
        // unpack into smaller packs): a guest's use runs natively on the host's copy.
        private static readonly Type[] HostUsed = { typeof(Item_WeedPreRollBigPack), typeof(Item_WeedPreRollHugePack) };
        internal static bool HostUse(Type type) => HostUsed.Contains(type);
        internal static bool Simple(Type type) => CarryTypes.Contains(type) || PersonalConsumables.Contains(type);
        // Every other physical item: grab, drop, throw, pose and its four native save values are
        // replicated generically (the same fields the vanilla save writes per item). Their Use stays
        // native on the host; guests only get the vetted shared uses (packs unpack; see SharedItems).
        // Tree cutting piles are general too: their count is IntValue1, a guest's prune drops a new
        // pile the host adopts, and a guest planting from a held pile reports it (see ForestPatches).
        private static readonly Type[] Vetted = { typeof(Item_MapleSyrup), typeof(Item_TomHoustonCup), typeof(Item_Lantern), typeof(Item_Poutine), typeof(Item_ElectricCoffeePercolator), typeof(Furniture_Decoration), typeof(Furniture_CarLift), typeof(Garden_OutdoorPlanter) };
        internal static bool General(Type type) => !Simple(type) && !Vetted.Contains(type) && typeof(InteractableItems).IsAssignableFrom(type);
        private static bool SimpleConsumable(InteractableItems item)
        {
            if (PersonalConsumables.Contains(item.GetType())) return true;
            bool baseUse = item.GetType().GetMethod(nameof(InteractableItems.Use), Type.EmptyTypes)!.DeclaringType == typeof(InteractableItems);
            return baseUse && (item.interactableItemType == InteractableItemType.Food || item.interactableItemType == InteractableItemType.Drink);
        }
        private static bool SimpleCarry(GameObject asset) => asset.GetComponentsInChildren<InteractableItems>(true).Length == 1 &&
            asset.GetComponentsInChildren<Rigidbody>(true).Length <= 1 && asset.GetComponent<Collider>() != null && asset.GetComponentsInChildren<MeshRenderer>(true).Length > 0;
        public static ItemPrefabCatalog Load(string diagnostics)
        {
            var catalog = cached ??= new ItemPrefabCatalog();

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "item-prefab-catalog.tsv"), new[] { "prefab-id\tresource\ttype\tfeatures\titem-components\trigidbodies\tshape-sha256" }
                .Concat(catalog.Entries.Values.OrderBy(e => e.Definition.Id).Select(e => e.Definition.Id + "\t" + e.Definition.Path + "\t" + e.Definition.TypeName + "\t" +
                    e.Definition.Features + "\t" + e.ItemComponents + "\t" + e.Rigidbodies + "\t" + e.Definition.ShapeHash)));


            File.WriteAllText(Path.Combine(diagnostics, "item-prefab-catalog.txt"), "MONMP_PREFAB_CATALOG_PASS\nEntries=" + catalog.Entries.Count + "\nCatalog=" + catalog.Definitions.Hash);
#endif

            return catalog;
        }
        private static string Shape(Transform root)
        {
            var lines = new List<string>();
            void Visit(Transform node, string path)
            {
                foreach (var component in node.GetComponents<Component>()) lines.Add(path + "/" + (component == null ? "<missing>" : component.GetType().FullName));
                for (int i = 0; i < node.childCount; i++)
                {
                    var child = node.GetChild(i); Visit(child, path + "/" + StaticId.Segment(child.name, i));
                }
            }
            Visit(root, "root"); lines.Sort(StringComparer.Ordinal);
            using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines)))).Replace("-", "");
        }
    }
}
