using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Static, scene-bound plates on the shared native vehicle fleet. The native manager
    // property remains the save source; guests restore their own saved label when the session ends.
    internal sealed class SharedVehiclePlates : IDisposable
    {
        private sealed class Binding
        {
            public ulong Id;
            public string Identity = "";
            public Vehicle_Type Type;
            public ulong PurchasedVehicleEntity;
            public Vehicle_Plate Plate = null!;
            public Vehicle_PartsManager Manager = null!;
            public VehiclePlateState State, Original;
            public int WaitThroughFrame;
            public bool OriginalCaptured = true;
        }

        private sealed class PurchasedVehicle
        {
            public ulong Entity;
            public Vehicle_Type Type;
            public GameObject Instance = null!;
            public int WaitThroughFrame;
            public bool Catalogued;
        }

        private const float InteractionRangeSquared = 16f;
        private const float PurchasedRetryInterval = 0.1f;
        private const string PurchasedIdentityPrefix = "purchased-vehicle-plate-v1/";
        private readonly Gameplay gameplay;
        private readonly bool host;
        private readonly NativeEntityRegistry identities;
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<Vehicle_Plate, Binding> byPlate = new Dictionary<Vehicle_Plate, Binding>();
        private readonly Dictionary<ulong, string> paths = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, PurchasedVehicle> purchasedVehicles = new Dictionary<ulong, PurchasedVehicle>();
        private readonly Dictionary<ulong, VehiclePlateState> retainedPurchasedStates = new Dictionary<ulong, VehiclePlateState>();
        private readonly Dictionary<ulong, (Binding Binding, VehiclePlateState State)> deferredPurchasedApply =
            new Dictionary<ulong, (Binding, VehiclePlateState)>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool applying, disposed;
        private float nextPurchasedRetry;

        internal int Count => bindings.Count;

#if MONMP_DEVELOPMENT
        internal bool TestRestored => bindings.Values.All(binding => binding.OriginalCaptured && binding.Manager != null &&
            string.Equals(binding.Manager.Get_QuebecPlateText, binding.Original.Text, StringComparison.Ordinal));
#endif

        internal IEnumerable<ulong> RequiredCatalogEntityIds => bindings.Values
            .Where(binding => binding.PurchasedVehicleEntity == 0 && binding.Id > WorldRecord.DynamicIdStart)
            .Select(binding => binding.Id);
        internal bool IsPurchasedRecord(ulong id) => bindings.TryGetValue(id, out var binding) && binding.PurchasedVehicleEntity != 0;
        internal bool Ready => !disposed && session?.IsRunning == true && channel?.FullySynchronized == true &&
            MonMPPlugin.Instance?.World?.PersonalProfileReady == true;

        internal SharedVehiclePlates(Gameplay gameplay, bool host, string diagnostics, NativeEntityRegistry identities)
        {
            this.gameplay = gameplay; this.host = host; this.identities = identities;
            var lines = new List<string>();
            var managers = SharedVehicleParts.FleetManagers().ToArray();
            foreach (var (type, manager) in managers)
            {
                var vehicle = manager.GetComponent<VehicleController>();
                if (vehicle == null || Array.IndexOf(SharedVehicleParts.Fleet, type) < 0 ||
                    !vehicle.gameObject.scene.IsValid() || !vehicle.gameObject.scene.isLoaded ||
                    SharedVehicleSales.Active?.OwnsSaleClone(manager.transform) == true) continue;
                var plates = manager.GetComponentsInChildren<Vehicle_Plate>(true)
                    .Where(plate => plate != null && plate.transform.IsChildOf(manager.transform) &&
                        plate.gameObject.scene.IsValid() && plate.gameObject.scene == vehicle.gameObject.scene)
                    .OrderBy(plate => SharedBuildingDoors.StablePath(plate.transform), StringComparer.Ordinal).ToArray();
                if (plates.Length != 1)
                {
                    if (plates.Length > 1) lines.Add("# skipped ambiguous plate set on " + SharedBuildingDoors.StablePath(manager.transform));
                    continue;
                }
                var plate = plates[0];
                string identity = SharedBuildingDoors.StablePath(plate.transform) + "/vehicle-plate-text-v1";
                ulong id = StaticId.Hash(identity);
                string previousPath = "";
                if (id == 0 || paths.TryGetValue(id, out previousPath) || identities.Resolve(id) != null || byPlate.ContainsKey(plate))
                {
                    lines.Add("# skipped duplicate or colliding identity: " + identity + (previousPath.Length == 0 ? "" : " collides with " + previousPath));
                    continue;
                }
                string? text = manager.Get_QuebecPlateText;
                if (string.IsNullOrEmpty(text) || text.Length > VehiclePlateState.MaxTextLength)
                {
                    lines.Add("# skipped unsupported native plate text: " + identity);
                    continue;
                }
                var state = new VehiclePlateState(text);
                var binding = new Binding { Id = id, Identity = identity, Type = type, Plate = plate, Manager = manager, State = state, Original = state };
                identities.Bind(id, VehiclePlateState.Adapter, plate.gameObject, identity, true, EntityPersistence.NativeSave);
                paths.Add(id, identity); bindings.Add(id, binding); byPlate.Add(plate, binding);
                lines.Add(id.ToString("X16") + "\t" + type + "\t" + identity + "\t" + state.Text);
            }
            var sales = SharedVehicleSales.Active;
            if (sales != null)
                foreach (var purchased in sales.PurchasedVehicles())
                    RegisterPurchasedVehicle(purchased.Entity, purchased.Type, purchased.Instance);

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-vehicle-plates.tsv"),
                new[] { "id\tvehicle\tplate-identity\tnative-text" }.Concat(lines.OrderBy(line => line, StringComparer.Ordinal)));
#endif

        }

        internal IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Where(binding => binding.OriginalCaptured).Select(binding =>
            new WorldRecord(binding.Id, 1, VehiclePlateState.Adapter, binding.State.Encode()));

        // Sale-root identity is stable across saved and runtime clones; native scene paths remain
        // the historic identity for the canonical fleet. Incomplete native clones are retained and
        // retried because the manager/plate text is resolved by native initialization.
        internal bool RegisterPurchasedVehicle(ulong entity, Vehicle_Type type, GameObject instance)
        {
            if (disposed) return false;
            if (type != Vehicle_Type.OlTruck && type != Vehicle_Type.SmollATV) return false;
            if (entity <= WorldRecord.DynamicIdStart) throw new InvalidOperationException("Purchased vehicle plate root is not dynamic.");
            if (instance == null || !instance.scene.IsValid()) throw new InvalidOperationException("Purchased vehicle plate root is outside the active scene.");
            if (purchasedVehicles.TryGetValue(entity, out var existing))
            {
                if (existing.Type != type || existing.Instance != instance)
                    throw new InvalidOperationException("Conflicting purchased vehicle plate root for entity " + entity.ToString("X16") + ".");
                if (existing.Catalogued) return true;
            }
            else
            {
                if (purchasedVehicles.Values.Any(value => value.Instance == instance && value.Entity != entity))
                    throw new InvalidOperationException("Purchased vehicle already has a different plate identity.");
                existing = new PurchasedVehicle { Entity = entity, Type = type, Instance = instance, WaitThroughFrame = Time.frameCount };
                purchasedVehicles.Add(entity, existing);
            }
            if (!TryRegisterPurchasedPlate(existing)) return true;
            existing.Catalogued = true;
            return true;
        }

        private bool TryRegisterPurchasedPlate(PurchasedVehicle purchase)
        {
            if (purchase.Instance == null || !purchase.Instance.scene.IsValid() || !purchase.Instance.scene.isLoaded ||
                Time.frameCount <= purchase.WaitThroughFrame) return false;
            var vehicles = purchase.Instance.GetComponentsInChildren<VehicleController>(true);
            if (vehicles.Length != 1 || vehicles[0] == null || vehicles[0].vehicle_type != purchase.Type) return false;
            var manager = vehicles[0].GetComponent<Vehicle_PartsManager>();
            if (manager == null || (manager.transform != purchase.Instance.transform &&
                !manager.transform.IsChildOf(purchase.Instance.transform)) ||
                manager.gameObject.scene != purchase.Instance.scene || !manager.isActiveAndEnabled || !manager.gameObject.activeInHierarchy)
                return false;
            var plates = manager.GetComponentsInChildren<Vehicle_Plate>(true)
                .Where(plate => plate != null && plate.transform.IsChildOf(manager.transform) &&
                    plate.gameObject.scene.IsValid() && plate.gameObject.scene == purchase.Instance.scene)
                .OrderBy(plate => PurchasedPlateRelativePath(purchase.Instance.transform, plate.transform), StringComparer.Ordinal).ToArray();
            if (plates.Length != 1) return false;
            var plate = plates[0];
            if (!plate.isActiveAndEnabled || !plate.gameObject.activeInHierarchy ||
                !PurchasedVehiclePartIdentityPolicy.TryRelativePath(purchase.Instance.transform, plate.transform, out string relativePath))
                return false;
            string? text = manager.Get_QuebecPlateText;
            if (string.IsNullOrEmpty(text) || text.Length > VehiclePlateState.MaxTextLength) return false;
            string identity = PurchasedPlateIdentity(purchase.Entity, purchase.Type, relativePath);
            ulong id = PurchasedPlateEntityId(identity);
            if (bindings.TryGetValue(id, out var existingBinding))
            {
                if (!ReferenceEquals(existingBinding.Plate, plate) || existingBinding.PurchasedVehicleEntity != purchase.Entity ||
                    existingBinding.Identity != identity)
                    throw new InvalidOperationException("Purchased vehicle plate identity collision: " + identity);
                return true;
            }
            if (byPlate.TryGetValue(plate, out existingBinding))
            {
                if (existingBinding.Id != id || existingBinding.PurchasedVehicleEntity != purchase.Entity)
                    throw new InvalidOperationException("Purchased vehicle plate already has a different identity.");
                return true;
            }
            if (id <= WorldRecord.DynamicIdStart || identities.Resolve(id) != null)
                throw new InvalidOperationException("Purchased vehicle plate identity collides with an existing world entity: " + identity);

            var state = new VehiclePlateState(text);
            var binding = new Binding
            {
                Id = id, Identity = identity, Type = purchase.Type, PurchasedVehicleEntity = purchase.Entity,
                Plate = plate, Manager = manager, State = state, Original = state, WaitThroughFrame = purchase.WaitThroughFrame,
                OriginalCaptured = true
            };
            identities.Bind(id, VehiclePlateState.Adapter, plate.gameObject, identity, false, EntityPersistence.NativeSave);
            paths.Add(id, identity); bindings.Add(id, binding); byPlate.Add(plate, binding);
            if (!host && retainedPurchasedStates.TryGetValue(id, out var retained))
                deferredPurchasedApply[id] = (binding, retained);
            if (host && channel != null && !channel.Records.ContainsKey(id))
                channel.Register(new WorldRecord(id, 1, VehiclePlateState.Adapter, state.Encode()));
            return true;
        }

        private static string PurchasedPlateRelativePath(Transform vehicleRoot, Transform plate) =>
            PurchasedVehiclePartIdentityPolicy.TryRelativePath(vehicleRoot, plate, out string path) ? path : "";

        private static string PurchasedPlateIdentity(ulong vehicleEntity, Vehicle_Type type, string relativePath) =>
            PurchasedIdentityPrefix + vehicleEntity.ToString("X16") + "/" + (int)type + "/" + relativePath;

        private static ulong PurchasedPlateEntityId(string identity)
        {
            ulong low = StaticId.Hash(identity) & 0x7FFFFFFFFFFFFFFFUL;
            return WorldRecord.DynamicIdStart | Math.Max(1UL, low);
        }

        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += Pose; session.PeerLeft += Left;
            channel.CommandRequested += Command; channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
            RetryPurchasedCatalogs();
        }

        internal void Update() => RetryPurchasedCatalogs();

        private void RetryPurchasedCatalogs()
        {
            if (disposed) return;
            if (Time.unscaledTime >= nextPurchasedRetry)
            {
                nextPurchasedRetry = Time.unscaledTime + PurchasedRetryInterval;
                foreach (var purchase in purchasedVehicles.Values.Where(value => !value.Catalogued).ToArray())
                    if (TryRegisterPurchasedPlate(purchase)) purchase.Catalogued = true;
            }
            if (host || channel == null || deferredPurchasedApply.Count == 0) return;
            foreach (var pair in deferredPurchasedApply.Values.ToArray())
            {
                var binding = pair.Binding;
                if (!binding.OriginalCaptured || binding.Manager == null || binding.Plate == null ||
                    Time.frameCount <= binding.WaitThroughFrame || !binding.Plate.isActiveAndEnabled ||
                    !binding.Plate.gameObject.activeInHierarchy) continue;
                if (!channel.Records.TryGetValue(binding.Id, out var record)) continue;
                var state = VehiclePlateState.Decode(record.Payload);
                Apply(binding, state);
                retainedPurchasedStates.Remove(binding.Id);
                deferredPurchasedApply.Remove(binding.Id);
            }
        }

        private void Pose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void Left(uint peer) => presence.Remove(peer);

        // Prefix for the actual native Vehicle_Plate.MouseRightDown path. Only registered plates
        // are intercepted; outside a session, native single-player behavior is untouched.
        internal bool MouseRightDown(Vehicle_Plate plate)
        {
            if (applying || disposed) return true;
            if (!byPlate.TryGetValue(plate, out var binding)) return true;
            if ((session ?? MonMPPlugin.Instance?.Session)?.IsRunning != true) return true;
            if (!Ready || MonMPPlugin.Instance?.World?.SaveGate.Active == true || !Usable(binding) || !Near(gameplay.PlayerWalking.transform.position, plate.transform.position))
                return false;
            if (host) return true;
            string next = VehiclePlateCycle.Next(binding.State.Text);
            channel!.Request(binding.Id, new VehiclePlateState(next).Encode());
            return false;
        }

        // Host-side postfix for native clicks. Guest clicks are suppressed in the prefix and
        // sent as revision-checked commands, so the host is the only process that cycles locally.
        internal void NativeRightDown(Vehicle_Plate plate)
        {
            if (applying || disposed || !host || !byPlate.TryGetValue(plate, out var binding) || !Ready ||
                MonMPPlugin.Instance?.World?.SaveGate.Active == true || !Usable(binding) ||
                !Near(gameplay.PlayerWalking.transform.position, plate.transform.position)) return;
            PublishNative(binding);
        }

        private bool Usable(Binding binding)
        {
            var identity = identities.Resolve(binding.Id);
            var vehicle = binding.Manager == null ? null : binding.Manager.GetComponent<VehicleController>();
            return binding.OriginalCaptured && binding.Plate != null && binding.Manager != null && binding.Plate.isActiveAndEnabled &&
                binding.Plate.gameObject.activeInHierarchy && binding.Plate.gameObject.scene.IsValid() &&
                binding.Manager.gameObject.scene == binding.Plate.gameObject.scene &&
                vehicle != null && vehicle.vehicle_type == binding.Type && Array.IndexOf(SharedVehicleParts.Fleet, binding.Type) >= 0 &&
                binding.Plate.transform.IsChildOf(binding.Manager.transform) &&
                identity != null && identity.IsStaticSceneEntity == (binding.PurchasedVehicleEntity == 0) && identity.AdapterType == VehiclePlateState.Adapter &&
                identity.SpawnKey == binding.Identity && identity.Persistence == EntityPersistence.NativeSave &&
                channel?.Records.TryGetValue(binding.Id, out var record) == true && record.Adapter == VehiclePlateState.Adapter;
        }

        private static bool Near(Vector3 player, Vector3 plate) => (player - plate).sqrMagnitude <= InteractionRangeSquared;

        [MonMP.Core.World.WorldCommandAdapters(VehiclePlateState.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || !Ready || MonMPPlugin.Instance?.World?.SaveGate.Active == true || !bindings.TryGetValue(id, out var binding) || !Usable(binding)) return;
            VehiclePlateState proposed;
            try { proposed = VehiclePlateState.Decode(payload); }
            catch (InvalidDataException) { return; }
            if (!VehiclePlateCycle.IsNext(binding.State.Text, proposed.Text) ||
                !string.Equals(binding.Manager.Get_QuebecPlateText, binding.State.Text, StringComparison.Ordinal)) return;
            Vector3 location;
            if (peer == 0) location = gameplay.PlayerWalking.transform.position;
            else
            {
                if (!presence.TryGetValue(peer, out var sample) || Time.unscaledTime - sample.Time > 2f) return;
                location = new Vector3(sample.Pose.X, sample.Pose.Y, sample.Pose.Z);
            }
            if (!Near(location, binding.Plate.transform.position)) return;

            applying = true;
            try { binding.Plate.MouseRightDown(); }
            finally { applying = false; }
            PublishNative(binding, proposed.Text);
        }

        private void PublishNative(Binding binding, string? expected = null)
        {
            string? native = binding.Manager.Get_QuebecPlateText;
            if (native == null || native.Length > VehiclePlateState.MaxTextLength) { Restore(binding, binding.State); return; }
            var next = new VehiclePlateState(native);
            if (!VehiclePlateCycle.IsNext(binding.State.Text, next.Text) ||
                (expected != null && !string.Equals(expected, next.Text, StringComparison.Ordinal)))
            {
                Restore(binding, binding.State);
                return;
            }
            binding.State = next;
            channel!.Publish(binding.Id, next.Encode());
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            var staged = new List<(Binding Binding, VehiclePlateState State)>();
            foreach (var record in records.Values.Where(record => record.Adapter == VehiclePlateState.Adapter))
            {
                var state = VehiclePlateState.Decode(record.Payload);
                if (bindings.TryGetValue(record.Id, out var binding)) staged.Add((binding, state));
                else if (record.Id > WorldRecord.DynamicIdStart) retainedPurchasedStates[record.Id] = state;
                else throw new InvalidDataException("Unknown static vehicle plate.");
            }
            foreach (var item in staged) Apply(item.Binding, item.State);
        }

        private void Changed(WorldRecord record)
        {
            if (!host && record.Adapter == VehiclePlateState.Adapter)
            {
                var state = VehiclePlateState.Decode(record.Payload);
                if (bindings.TryGetValue(record.Id, out var binding))
                {
                    if (binding.PurchasedVehicleEntity != 0 && !binding.OriginalCaptured)
                    {
                        retainedPurchasedStates[record.Id] = state;
                        deferredPurchasedApply[record.Id] = (binding, state);
                    }
                    else Apply(binding, state);
                }
                else if (record.Id > WorldRecord.DynamicIdStart) retainedPurchasedStates[record.Id] = state;
                else throw new InvalidDataException("Unknown static vehicle plate.");
            }
        }

        private static void Restore(Binding binding, VehiclePlateState state) => Apply(binding, state);

        private static void Apply(Binding binding, VehiclePlateState state)
        {
            binding.State = state;
            if (binding.OriginalCaptured && binding.Manager != null &&
                !string.Equals(binding.Manager.Get_QuebecPlateText, state.Text, StringComparison.Ordinal))
                binding.Manager.Get_QuebecPlateText = state.Text;
        }


#if MONMP_DEVELOPMENT
        internal void TestClick(ulong id) => bindings[id].Plate.MouseRightDown();


        internal VehiclePlateState TestState(ulong id) => bindings[id].State;


        internal VehiclePlateState TestNative(ulong id) => new VehiclePlateState(bindings[id].Manager.Get_QuebecPlateText ?? "");


        internal VehiclePlateState TestRecord(ulong id) => VehiclePlateState.Decode(channel!.Records[id].Payload);


        internal VehiclePlateState TestOriginal(ulong id) => bindings[id].Original;


        internal ulong TestPlate(Vehicle_Type type) => bindings.Values.Where(binding => binding.Type == type).OrderBy(binding => binding.Id).Select(binding => binding.Id).First();


        internal Vector3 TestPosition(ulong id) => bindings[id].Plate.transform.position;


        internal void TestRequest(ulong id, string text) => channel!.Request(id, new VehiclePlateState(text).Encode());


        internal void TestRestoreHost(ulong id, VehiclePlateState text)
        {
            if (!host) throw new InvalidOperationException("Only host test fixtures may restore a shared plate.");
            var binding = bindings[id]; Apply(binding, text); channel!.Publish(id, text.Encode());
        }
#endif


        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (session != null) { session.PoseReceived -= Pose; session.PeerLeft -= Left; }
            if (channel != null) { channel.CommandRequested -= Command; channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; }
            if (!host)
                foreach (var binding in bindings.Values)
                    if (binding.OriginalCaptured && binding.Manager != null) Apply(binding, binding.Original);
            presence.Clear();
            deferredPurchasedApply.Clear(); retainedPurchasedStates.Clear(); purchasedVehicles.Clear();
        }
    }
}
