using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal enum EntityPersistence { NativeSave, SessionOnly }
    internal enum EntityInterest { Global, Spatial }
    internal sealed class NetworkIdentity : MonoBehaviour
    {
        internal NativeEntityRegistry? Registry;
        public ulong EntityId { get; internal set; }
        public ushort AdapterType { get; internal set; }
        public string SpawnKey { get; internal set; } = "";
        public bool IsStaticSceneEntity { get; internal set; }
        public EntityPersistence Persistence { get; internal set; }
        public EntityInterest Interest { get; internal set; }
        private void OnDestroy() => Registry?.Detached(this);
    }
    // One registry per SharedWorld. It owns only identity components, never native objects.
    internal sealed class NativeEntityRegistry : IDisposable
    {
        private readonly Dictionary<ulong, NetworkIdentity> entries = new Dictionary<ulong, NetworkIdentity>();
        private bool disposed;
        public int Count => entries.Count;
        public IReadOnlyList<NetworkIdentity> Snapshot() => entries.Values.Where(v => v != null).OrderBy(v => v.EntityId).ToArray();
        public NetworkIdentity? Resolve(ulong id) => entries.TryGetValue(id, out var value) && value != null ? value : null;
        public NetworkIdentity Bind(ulong id, ushort adapter, GameObject target, string spawnKey, bool sceneEntity,
            EntityPersistence persistence = EntityPersistence.NativeSave, EntityInterest interest = EntityInterest.Global)
        {
            if (disposed || id == 0 || adapter == 0 || target == null || !target.scene.IsValid() ||
                string.IsNullOrEmpty(spawnKey) || spawnKey.Length > 512 || !Enum.IsDefined(typeof(EntityPersistence), persistence) || !Enum.IsDefined(typeof(EntityInterest), interest))
                throw new InvalidOperationException("Invalid native entity registration.");
            // A destroyed native object leaves a Unity-null identity until its OnDestroy runs; it is stale.
            if (entries.TryGetValue(id, out var stale) && stale == null) entries.Remove(id);
            if (entries.TryGetValue(id, out var existing))
            {
                if (existing == null || existing.gameObject != target || existing.AdapterType != adapter || existing.SpawnKey != spawnKey ||
                    existing.IsStaticSceneEntity != sceneEntity || existing.Persistence != persistence || existing.Interest != interest)
                    throw new InvalidOperationException("Conflicting native entity identity: " + id);
                return existing;
            }
            if (target.GetComponents<NetworkIdentity>().Any(v => v.Registry != null &&
                (v.Registry != this || (v.AdapterType == adapter && v.SpawnKey == spawnKey))))
                throw new InvalidOperationException("Native object already belongs to an entity registry.");
            var identity = target.AddComponent<NetworkIdentity>();
            identity.EntityId = id; identity.AdapterType = adapter; identity.SpawnKey = spawnKey;
            identity.IsStaticSceneEntity = sceneEntity; identity.Persistence = persistence; identity.Interest = interest;
            identity.Registry = this; entries.Add(id, identity); return identity;
        }
        internal void Detached(NetworkIdentity identity)
        {
            if (entries.TryGetValue(identity.EntityId, out var found) && ReferenceEquals(found, identity)) entries.Remove(identity.EntityId);
            identity.Registry = null;
        }
        public void Unbind(ulong id)
        {
            if (!entries.TryGetValue(id, out var identity)) return;
            entries.Remove(id);
            if (identity == null) return;
            identity.Registry = null; UnityEngine.Object.Destroy(identity);
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            foreach (var id in entries.Keys.ToArray()) Unbind(id);
        }
    }
}
