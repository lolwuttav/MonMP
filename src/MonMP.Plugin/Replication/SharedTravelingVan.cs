using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // The traveling van rolls its stock on every opening: one of its spray paint displays and the colours
    // of its three bolt packs. Each peer rolled its own, so a guest saw a spray can the host's van did not
    // sell (and could not buy it) and bolt packs whose colour differed from the one it received. The host's
    // roll is the stock; guests mirror it and the native raised/visible van presentation.
    internal sealed class SharedTravelingVan : IDisposable
    {
        public const ushort Adapter = 104;
        private const byte None = 255;
        private const byte Sold = 254;
        private static readonly ulong Id = StaticId.Hash("traveling-van-stock-v1");
        private static readonly AccessTools.FieldRef<Store_TravelingVan, GameObject[]> SprayField = AccessTools.FieldRefAccess<Store_TravelingVan, GameObject[]>("SprayPaintAvailable");
        private static readonly string[] BoltDisplayFields = { "BoltForSale", "BoltForSale2", "BoltForSale3" };
        private static readonly string[] BoltRendererFields = { "BoltsInPackForSale", "BoltsInPackForSale2", "BoltsInPackForSale3" };
        private readonly bool host;
        private readonly Store_TravelingVan? van;
        private readonly GameObject[] sprays = Array.Empty<GameObject>();
        private readonly ItemForSale_Display?[] boltDisplays = new ItemForSale_Display?[3];
        private readonly int[] originalBoltColors = { -1, -1, -1 };
        private readonly MeshRenderer[][] boltRenderers = { Array.Empty<MeshRenderer>(), Array.Empty<MeshRenderer>(), Array.Empty<MeshRenderer>() };
        private readonly byte[] original = { None, None, None, None };
        private readonly GameObject?[] presentation = new GameObject?[5];
        private readonly Dictionary<GameObject, bool> originalActivity = new Dictionary<GameObject, bool>();
        private readonly Dictionary<MeshRenderer, Material[]> originalMaterials = new Dictionary<MeshRenderer, Material[]>();
        private readonly Vector3 originalPosition;
        private readonly bool originalOpen;
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool appliedReplica;
        private float nextCapture;

        public SharedTravelingVan(bool host)
        {
            this.host = host;
            foreach (var candidate in UnityEngine.Object.FindObjectsOfType<Store_TravelingVan>(true))
                if (candidate.gameObject.scene.IsValid()) { van = candidate; break; }
            if (van == null) return;
            originalPosition = van.transform.position; originalOpen = van.isOpen;
            var presentationFields = new[] { "Van", "RallyShocks", "RallyShocksLocked", "DragTires", "DragTiresLocked" };
            for (int i = 0; i < presentation.Length; i++)
                presentation[i] = AccessTools.Field(typeof(Store_TravelingVan), presentationFields[i]).GetValue(van) as GameObject;
            sprays = SprayField(van) ?? Array.Empty<GameObject>();
            for (int i = 0; i < 3; i++)
            {
                boltDisplays[i] = AccessTools.Field(typeof(Store_TravelingVan), BoltDisplayFields[i])?.GetValue(van) as ItemForSale_Display;
                if (boltDisplays[i] != null) originalBoltColors[i] = boltDisplays[i]!.CustomIntValue1;
                boltRenderers[i] = AccessTools.Field(typeof(Store_TravelingVan), BoltRendererFields[i])?.GetValue(van) as MeshRenderer[] ?? Array.Empty<MeshRenderer>();
                foreach (var renderer in boltRenderers[i])
                    if (renderer != null && !originalMaterials.ContainsKey(renderer)) originalMaterials.Add(renderer, renderer.sharedMaterials);
            }
            foreach (var go in sprays.Concat(presentation))
                if (go != null && !originalActivity.ContainsKey(go)) originalActivity.Add(go, go.activeSelf);
            original = Capture();
        }


#if MONMP_DEVELOPMENT
        internal Store_TravelingVan? TestVan => van;


        internal byte[] TestNativeState => Capture();


        internal byte[] TestCanonicalState => channel != null && channel.Records.TryGetValue(Id, out var record) ? record.Payload : original;


        internal GameObject? TestVanObject => presentation[0];


        internal ItemForSale_Display? TestBolt(int index) => boltDisplays[index];


        internal GameObject[] TestSprays => sprays;


        internal bool TestMaterialsMatch => Enumerable.Range(0, boltDisplays.Length).All(i => boltDisplays[i] == null ||
            boltRenderers[i].All(r => r == null || r.sharedMaterial == Singleton<GameAssets>.i?.Hardware_BoltColor((BoltColor)boltDisplays[i]!.CustomIntValue1)));


        internal bool TestRestored => van != null && van.transform.position == originalPosition && van.isOpen == originalOpen &&
            originalActivity.All(p => p.Key == null || p.Key.activeSelf == p.Value) &&
            originalMaterials.All(p => p.Key == null || p.Key.sharedMaterials.SequenceEqual(p.Value)) &&
            Enumerable.Range(0, 3).All(i => boltDisplays[i] == null || boltDisplays[i]!.CustomIntValue1 == originalBoltColors[i]);
#endif


        internal IEnumerable<WorldRecord> InitialRecords()
        {
            if (van != null) yield return new WorldRecord(Id, 1, Adapter, original);
        }

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            if (van == null) return;
            this.session = session; this.channel = channel;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
        }

        private byte[] Capture()
        {
            var state = new byte[] { None, None, None, None, 0, 0, 0, 0, 0 };
            if (van == null) return state;
            int active = -1, count = 0;
            for (int i = 0; i < sprays.Length; i++)
                if (sprays[i] != null && sprays[i].activeSelf) { active = i; count++; }
            // Before the van's first opening every display is still in its scene state; leave those alone.
            if (count == 1 && active < Sold) state[0] = (byte)active;
            else if (count == 0) state[0] = Sold;
            for (int i = 0; i < 3; i++)
                if (boltDisplays[i] is { } display && display.CustomIntValue1 >= 0 && display.CustomIntValue1 < None) state[i + 1] = (byte)display.CustomIntValue1;
            if (van.isOpen) state[4] |= 1;
            for (int i = 0; i < presentation.Length; i++)
                if (presentation[i] != null && presentation[i]!.activeSelf) state[4] |= (byte)(1 << (i + 1));
            Array.Copy(BitConverter.GetBytes(van.transform.position.y), 0, state, 5, 4);
            return state;
        }

        private void Apply(byte[] state)
        {
            if (van == null) return;
            if (state.Length != 9 || state[4] > 63 ||
                (state[0] < Sold && state[0] >= sprays.Length)) throw new InvalidDataException("Invalid traveling van state.");
            float y = BitConverter.ToSingle(state, 5);
            if (float.IsNaN(y) || float.IsInfinity(y)) throw new InvalidDataException("Invalid traveling van height.");
            int colors = Enum.GetValues(typeof(BoltColor)).Length;
            for (int i = 1; i < 4; i++)
                if (state[i] != None && state[i] >= colors) throw new InvalidDataException("Invalid traveling van bolt colour.");
            if (state[0] != None)
                for (int i = 0; i < sprays.Length; i++)
                    if (sprays[i] != null && sprays[i].activeSelf != (i == state[0])) sprays[i].SetActive(i == state[0]);
            for (int i = 0; i < 3; i++)
            {
                if (state[i + 1] == None || !(boltDisplays[i] is { } display)) continue;
                display.CustomIntValue1 = state[i + 1];
                var material = Singleton<GameAssets>.i?.Hardware_BoltColor((BoltColor)state[i + 1]);
                if (material != null) foreach (var renderer in boltRenderers[i])
                    if (renderer != null && renderer.sharedMaterial != material) renderer.sharedMaterial = material;
            }
            van.isOpen = (state[4] & 1) != 0;
            for (int i = 0; i < presentation.Length; i++)
                if (presentation[i] != null && presentation[i]!.activeSelf != ((state[4] & (1 << (i + 1))) != 0))
                    presentation[i]!.SetActive((state[4] & (1 << (i + 1))) != 0);
            var position = van.transform.position; position.y = y; van.transform.position = position;
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host || !records.TryGetValue(Id, out var record)) return;
            Apply(record.Payload); appliedReplica = true;
        }

        private void Changed(WorldRecord record)
        {
            if (host || record.Id != Id) return;
            Apply(record.Payload); appliedReplica = true;
        }

        public void Update()
        {
            if (van == null || session?.IsRunning != true || channel?.Ready != true || Time.unscaledTime < nextCapture) return;
            nextCapture = Time.unscaledTime + 0.1f;
            if (host) channel.Publish(Id, Capture());
            // Native activation or local stock refreshes can happen after the host record arrived.
            else if (appliedReplica && channel.Records.TryGetValue(Id, out var record)) Apply(record.Payload);
        }

        public void Dispose()
        {
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; }
            if (!host && appliedReplica && van != null)
            {
                Apply(original);
                van.transform.position = originalPosition; van.isOpen = originalOpen;
                for (int i = 0; i < boltDisplays.Length; i++)
                    if (boltDisplays[i] != null) boltDisplays[i]!.CustomIntValue1 = originalBoltColors[i];
                foreach (var pair in originalActivity) if (pair.Key != null) pair.Key.SetActive(pair.Value);
                foreach (var pair in originalMaterials) if (pair.Key != null) pair.Key.sharedMaterials = pair.Value;
            }
            session = null; channel = null;
        }
    }
}
