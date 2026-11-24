using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using MonMP.Plugin.Diagnostics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    internal sealed class SharedWindowTints : IDisposable
    {
        private sealed class Binding
        {
            public ulong Id;
            public string Path = "";
            public Vehicle_WindowTint Native = null!;
            public WindowTintState Original;
            public WindowTintState Current;
            public bool Purchased;
            public ulong PurchasedVehicleEntity;
            public Vehicle_Type PurchasedVehicleType;
            public bool OriginalCaptured = true;
            public bool HasCurrent = true;
            public WorldRecord? PendingRecord;
        }
        private readonly bool host;
        private readonly SharedItems items;
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<Vehicle_WindowTint, Binding> byNative = new Dictionary<Vehicle_WindowTint, Binding>();
        private readonly HashSet<Transform> purchasedRoots = new HashSet<Transform>();
        private readonly Dictionary<ulong, WorldRecord> deferredPurchasedRecords = new Dictionary<ulong, WorldRecord>();

#if MONMP_DEVELOPMENT
        private readonly List<string> diagnosticRows = new List<string> { "entity\tpath\ttype\tinitialTint" };
#endif


#if MONMP_DEVELOPMENT
        private readonly string diagnosticsPath;
#endif

        private readonly Dictionary<Guid, ulong> pending = new Dictionary<Guid, ulong>();
        private readonly HashSet<(Guid Profile, Guid Operation)> claims = new HashSet<(Guid, Guid)>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Dictionary<uint, (float Tokens, float Time)> budgets = new Dictionary<uint, (float, float)>();
        private static readonly System.Reflection.FieldInfo TintColor = AccessTools.Field(typeof(Tool_WindowTint), "tintColor");
        private static readonly System.Reflection.FieldInfo IsTint = AccessTools.Field(typeof(Vehicle_WindowTint), "isTint");
        private static readonly System.Reflection.FieldInfo Initialized = AccessTools.Field(typeof(Vehicle_WindowTint), "Initialized");
        private static readonly System.Reflection.FieldInfo? SecondWindowMeshField = AccessTools.Field(typeof(Vehicle_WindowTint_Second), "SecondWindowMesh");
        private static readonly System.Reflection.FieldInfo? SecondMatIndexField = AccessTools.Field(typeof(Vehicle_WindowTint_Second), "SecondMatIndex");
        private static readonly System.Reflection.FieldInfo? SecondMatInteriorIndexField = AccessTools.Field(typeof(Vehicle_WindowTint_Second), "SecondMatInteriorIndex");
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool applying, disposed, blocked;
        private float nextCapture;

#if MONMP_DEVELOPMENT
        internal int TestAccepted { get; private set; }


        internal int TestRejected { get; private set; }


        internal int TestDuplicate { get; private set; }


        internal ulong[] TestWindows => bindings.Keys.OrderBy(id => id).ToArray();


        internal Vehicle_WindowTint TestNative(ulong id) => bindings[id].Native;


        internal WindowTintState TestState(ulong id) => bindings[id].Current;


        internal WindowTintState TestOriginal(ulong id) => bindings[id].Original;


        internal WindowTintState TestNativeState(ulong id) => Capture(bindings[id].Native);


        internal uint TestRevision(ulong id) => channel != null && channel.Records.TryGetValue(id, out var record) ? record.Revision : 0;


        internal int TestPending => pending.Count;


        internal bool TestRestored => !host && bindings.Values.Where(b => b.OriginalCaptured && b.Native != null)
            .All(b => Same(Capture(b.Native), b.Original));
#endif

        internal bool TransactionsSettled => pending.Count == 0;
        internal IEnumerable<ulong> RequiredCatalogEntityIds => bindings.Values
            .Where(binding => !binding.Purchased && binding.Id > WorldRecord.DynamicIdStart)
            .Select(binding => binding.Id);
        internal bool IsPurchasedRecord(ulong id) => bindings.TryGetValue(id, out var binding) && binding.Purchased;

        internal SharedWindowTints(bool host, SharedItems items, string diagnostics)
        {
            this.host = host; this.items = items;

#if MONMP_DEVELOPMENT
            diagnosticsPath = Path.Combine(diagnostics, "shared-window-tints.tsv");
#endif

            if (TintColor == null || IsTint == null || Initialized == null) throw new InvalidOperationException("Unsupported native tint fields.");
            var ids = new IdentityRegistry();
            var activeSales = SharedVehicleSales.Active;
            var savedPurchases = activeSales?.PurchasedVehicles().ToArray() ?? Array.Empty<(ulong Entity, Vehicle_Type Type, GameObject Instance)>();
            foreach (var purchase in savedPurchases)
            {
                purchasedRoots.Add(purchase.Instance.transform);
                RegisterPurchasedVehicle(purchase.Entity, purchase.Type, purchase.Instance);
            }
            foreach (var native in Object.FindObjectsOfType<Vehicle_WindowTint>(true)
                .Where(t => t != null && t.gameObject.scene.IsValid() &&
                    activeSales?.OwnsSaleClone(t.transform) != true && !IsWithinPurchasedRoot(t.transform))
                .OrderBy(t => RuntimeProbe.CanonicalPath(t.transform), StringComparer.Ordinal))
            {
                string path = RuntimeProbe.CanonicalPath(native.transform);
                int componentIndex = Array.IndexOf(native.GetComponents<Vehicle_WindowTint>(), native);
                if (componentIndex < 0) throw new InvalidOperationException("Window tint component was not found on its own object.");
                ulong id = ids.Register(path + "/shared-window-tint-v1/" + componentIndex);
                var state = Capture(native);
                var binding = new Binding { Id = id, Path = path, Native = native, Original = state, Current = state };
                bindings.Add(id, binding); byNative.Add(native, binding);

#if MONMP_DEVELOPMENT
                diagnosticRows.Add(id.ToString("X16") + "\t" + path + "\t" + native.GetType().Name + "\t" + state.Tinted);
#endif

            }

#if MONMP_DEVELOPMENT
            WriteDiagnostics();
#endif

            if (bindings.Count == 0) throw new InvalidOperationException("No native window tint targets were found.");
        }

        internal IEnumerable<WorldRecord> InitialRecords()
        {
            RetryPurchasedBindings();
            return bindings.Values.Where(binding => binding.HasCurrent).Select(binding =>
                new WorldRecord(binding.Id, 1, WindowTintState.Adapter, binding.Current.Encode()));
        }

        internal bool RegisterPurchasedVehicle(ulong entity, Vehicle_Type type, GameObject instance)
        {
            if (disposed || entity <= WorldRecord.DynamicIdStart || instance == null) return false;
            purchasedRoots.Add(instance.transform);
            var candidates = instance.GetComponentsInChildren<Vehicle_WindowTint>(true)
                .Where(native => native != null)
                .Select(native =>
                {
                    int componentIndex = Array.IndexOf(native.GetComponents<Vehicle_WindowTint>(), native);
                    bool hasPath = TryPurchasedRelativePath(instance.transform, native.transform, out string relativePath);
                    return (Native: native, ComponentIndex: componentIndex, HasPath: hasPath, RelativePath: relativePath);
                })
                .Where(candidate => candidate.ComponentIndex >= 0 && candidate.HasPath)
                .OrderBy(candidate => candidate.RelativePath, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.ComponentIndex).ToArray();
            if (candidates.Length == 0) return false;

            foreach (var candidate in candidates)
            {
                string identity = PurchasedTintIdentity(entity, type, candidate.RelativePath, candidate.ComponentIndex);
                ulong id = PurchasedTintEntityId(identity);
                if (bindings.TryGetValue(id, out var existing))
                {
                    if (!ReferenceEquals(existing.Native, candidate.Native) || !existing.Purchased ||
                        existing.PurchasedVehicleEntity != entity || existing.PurchasedVehicleType != type)
                        throw new InvalidOperationException("Purchased window tint identity collision: " + identity);
                    HydrateRetainedRecord(existing);
                    TryApplyPendingRecord(existing);
                    continue;
                }
                if (byNative.TryGetValue(candidate.Native, out var other))
                {
                    if (!other.Purchased || other.PurchasedVehicleEntity != entity || other.Id != id)
                        throw new InvalidOperationException("Window tint component already has a different identity: " + identity);
                    continue;
                }

                var binding = new Binding
                {
                    Id = id,
                    Path = identity,
                    Native = candidate.Native,
                    Purchased = true,
                    PurchasedVehicleEntity = entity,
                    PurchasedVehicleType = type,
                    OriginalCaptured = false,
                    HasCurrent = false
                };
                bindings.Add(id, binding);
                byNative.Add(candidate.Native, binding);

#if MONMP_DEVELOPMENT
                diagnosticRows.Add(id.ToString("X16") + "\t" + identity + "\t" + candidate.Native.GetType().Name + "\tpurchased");
#endif

                HydrateRetainedRecord(binding);
                TryInitializePurchasedBinding(binding);
            }

#if MONMP_DEVELOPMENT
            WriteDiagnostics();
#endif

            return true;
        }

        private static bool TryPurchasedRelativePath(Transform vehicleRoot, Transform target, out string path)
        {
            if (target == vehicleRoot)
            {
                path = "$root";
                return true;
            }
            return PurchasedVehiclePartIdentityPolicy.TryRelativePath(vehicleRoot, target, out path);
        }

        private static string PurchasedTintIdentity(ulong entity, Vehicle_Type type, string relativePath, int componentIndex) =>
            "purchased-window-tint-v1/" + entity.ToString("X16") + "/" + (int)type + "/" + relativePath + "/component/" + componentIndex;

        private static ulong PurchasedTintEntityId(string identity)
        {
            ulong low = StaticId.Hash(identity) & 0x7FFFFFFFFFFFFFFFUL;
            return WorldRecord.DynamicIdStart | Math.Max(1UL, low);
        }

        private bool IsWithinPurchasedRoot(Transform transform)
        {
            for (var current = transform; current != null; current = current.parent)
                if (purchasedRoots.Contains(current)) return true;
            return false;
        }


#if MONMP_DEVELOPMENT
        private void WriteDiagnostics() => File.WriteAllLines(diagnosticsPath,
            diagnosticRows.Take(1).Concat(diagnosticRows.Skip(1).OrderBy(row => row, StringComparer.Ordinal)));
#endif

        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.CommandRequested += Command; channel.CommandRejected += Stale;
            channel.ReplyReceived += Reply; channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
            if (!host)
            {
                foreach (var binding in bindings.Values.Where(value => value.Purchased)) HydrateRetainedRecord(binding);
                RetryPurchasedBindings();
            }
        }
        internal void SetNewActionsBlocked(bool value) => blocked = value;
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer)
        {
            presence.Remove(peer); budgets.Remove(peer);
            if (!host)
            {
                pending.Clear();
                deferredPurchasedRecords.Clear();
                foreach (var binding in bindings.Values.Where(value => value.Purchased)) binding.PendingRecord = null;
            }
        }

        // Only the guest's equipped native tool handler calls this. The host runs
        // its own vanilla mouse path; its tint state is captured and published below.
        internal bool InterceptTool(InteractableTools tool, bool remove)
        {
            if (disposed || session?.IsRunning != true) return true;
            if (host) return !blocked;
            if (channel?.FullySynchronized != true || blocked || !tool.isEquiped ||
                !items.TryLocalTintTool(tool, out ulong toolId, out uint generation)) return false;
            if (!remove && tool.IntValue1 <= 0)
            {
                Singleton<UI_ErrorMessage>.i?.Show(TEXT.GetText("ERROR_WindowTint_TintRollIsEmpty"));
                return false;
            }
            var camera = Camera.main;
            if (camera == null || !Physics.Raycast(MonMP.Plugin.Integration.LocalInteraction.SelectionRay(camera),
                out var hit, 2.4f, LayerMask.GetMask("MainVehicleTrigger") | LayerMask.GetMask("VehiclePlayerCantGoThrough"))) return false;
            var target = hit.collider.GetComponent<Vehicle_WindowTint>();
            if (target == null || !byNative.TryGetValue(target, out var binding) || pending.Values.Contains(binding.Id)) return false;
            Request(binding.Id, toolId, generation, remove);
            return false;
        }
        internal bool AllowNativeMutation(Vehicle_WindowTint native) =>
            disposed || session?.IsRunning != true || applying || (host && !blocked);
        internal void OnNativeStart(Vehicle_WindowTint native)
        {
            if (disposed || !byNative.TryGetValue(native, out var binding)) return;
            if (binding.Purchased)
            {
                TryInitializePurchasedBinding(binding);
                return;
            }
            if (host || session?.IsRunning != true) return;
            Apply(native, binding.Current);
        }
        internal Guid Request(ulong targetId, ulong toolId, uint generation, bool remove)
        {
            if (host || blocked || channel?.FullySynchronized != true || !bindings.ContainsKey(targetId) || pending.Count >= 16) return Guid.Empty;
            Guid operation = Guid.NewGuid();
            pending.Add(operation, targetId);
            channel.Request(targetId, new WindowTintCommand(operation, remove, toolId, generation).Encode(), true);
            return operation;
        }

#if MONMP_DEVELOPMENT
        internal void TestReplay(ulong targetId, ulong toolId, uint generation, bool remove, Guid operation) =>
            channel!.Request(targetId, new WindowTintCommand(operation, remove, toolId, generation).Encode(), true);
#endif

        [MonMP.Core.World.WorldCommandAdapters(WindowTintState.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || peer == 0 || !bindings.TryGetValue(id, out var binding) || session == null || channel == null) return;
            WindowTintCommand command;
            try { command = WindowTintCommand.Decode(payload); }
            catch (InvalidDataException) {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 return; }
            if (!session.Peers.TryGetValue(peer, out var remote)) return;
            var key = (remote.ProfileId, command.Operation);
            if (claims.Contains(key)) {
#if MONMP_DEVELOPMENT
TestDuplicate++;
#endif
 SendReply(peer, id, command.Operation, false); return; }
            if (claims.Count >= 16384) { SendReply(peer, id, command.Operation, false); return; }
            claims.Add(key);
            InteractableTools? tool = null;
            bool purchasedReady = !binding.Purchased ||
                TryInitializePurchasedBinding(binding) && binding.OriginalCaptured && binding.HasCurrent;
            if (blocked || !binding.Native.gameObject.activeInHierarchy || !purchasedReady || !InRange(peer, binding.Native.transform.position) ||
                !TakeBudget(peer) || !items.TryAuthorizeTintTool(peer, command.ToolId, command.ToolGeneration, command.Remove, out tool))
            {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 SendReply(peer, id, command.Operation, false, !command.Remove && tool != null && tool.IntValue1 <= 0
     ? TEXT.GetText("ERROR_WindowTint_TintRollIsEmpty") : null); return; }
            try
            {
                bool changed;
                if (command.Remove)
                {
                    changed = binding.Native.Get_IsTint;
                    if (changed) binding.Native.RemoveTint();
                }
                else
                {
                    var color = (Color)TintColor.GetValue(tool!);
                    _ = new WindowTintState(true, color.r, color.g, color.b, color.a);
                    changed = !binding.Native.Get_IsTint && binding.Native.ApplyTint(color);
                    if (changed) items.CommitTintUse(command.ToolId);
                }
                if (changed)
                {
                    var state = Capture(binding.Native);
                    binding.Current = state; channel.Publish(id, state.Encode());
#if MONMP_DEVELOPMENT
TestAccepted++;
#endif

                }
                else
#if MONMP_DEVELOPMENT
TestRejected++;
#else
{}
#endif

                SendReply(peer, id, command.Operation, changed, changed ? null : TEXT.GetText(command.Remove
                    ? "ERROR_WindowTint_NoTintToRemove" : "ERROR_WindowTint_AlreadyTint"));
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 SendReply(peer, id, command.Operation, false);
            }
        }
        private bool InRange(uint peer, Vector3 target)
        {
            if (!presence.TryGetValue(peer, out var seen) || Time.unscaledTime - seen.Time > 2 ||
                seen.Pose.State != (byte)PlayerState.Walking) return false;
            return (new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z) - target).sqrMagnitude <= 3.2f * 3.2f;
        }
        private bool TakeBudget(uint peer)
        {
            float now = Time.unscaledTime;
            var state = budgets.TryGetValue(peer, out var existing) ? existing : (Tokens: 4f, Time: now);
            state.Tokens = Math.Min(4, state.Tokens + Math.Max(0, now - state.Time) * 4);
            state.Time = now;
            if (state.Tokens < 1) { budgets[peer] = state; return false; }
            state.Tokens--; budgets[peer] = state; return true;
        }
        private void SendReply(uint peer, ulong id, Guid operation, bool success, string? error = null)
        {
            if (channel == null || session == null || !session.Peers.ContainsKey(peer)) return;
            // Reuse the existing native-error envelope; keep the transaction acknowledgement
            // unchanged and send feedback only to the actor while its operation is pending.
            if (!success) channel.Reply(peer, id, NativeErrorReply.Encode(error ?? "Window tint action could not be completed. Try again nearby with the equipped tool."));
            var payload = new byte[18]; payload[0] = 0xDD;
            Array.Copy(operation.ToByteArray(), 0, payload, 1, 16); payload[17] = (byte)(success ? 1 : 0);
            channel.Reply(peer, id, payload);
        }
        private void Stale(uint peer, ulong id, byte[] payload)
        {
            if (!host || !bindings.ContainsKey(id)) return;
            try { SendReply(peer, id, WindowTintCommand.Decode(payload).Operation, false); }
            catch (InvalidDataException) { }
        }
        private void Reply(ulong id, byte[] payload)
        {
            if (host || payload == null) return;
            if (pending.Values.Contains(id) && NativeErrorReply.TryDecode(payload, out string error))
            { Singleton<UI_ErrorMessage>.i?.Show(error); return; }
            if (payload.Length != 18 || payload[0] != 0xDD || payload[17] > 1) return;
            var operation = new Guid(payload.Skip(1).Take(16).ToArray());
            if (pending.TryGetValue(operation, out ulong target) && target == id) pending.Remove(operation);
        }
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            foreach (var record in records.Values.Where(r => r.Adapter == WindowTintState.Adapter)) ApplyRecord(record);
        }
        private void Changed(WorldRecord record) { if (!host && record.Adapter == WindowTintState.Adapter) ApplyRecord(record); }
        private void ApplyRecord(WorldRecord record)
        {
            if (!bindings.TryGetValue(record.Id, out var binding))
            {
                if (record.Id > WorldRecord.DynamicIdStart)
                {
                    RetainDeferredRecord(record);
                    return;
                }
                throw new InvalidDataException("Unknown window tint record.");
            }
            _ = WindowTintState.Decode(record.Payload);
            if (!binding.Purchased)
            {
                var state = WindowTintState.Decode(record.Payload);
                Apply(binding.Native, state); binding.Current = state;
                return;
            }
            RetainPendingRecord(binding, record);
            TryApplyPendingRecord(binding);
        }
        internal void Update()
        {
            if (disposed || Time.unscaledTime < nextCapture) return;
            nextCapture = Time.unscaledTime + 0.1f;
            RetryPurchasedBindings();
            if (!host || channel?.Ready != true) return;
            foreach (var binding in bindings.Values)
            {
                if (binding.Purchased && (!TryInitializePurchasedBinding(binding) || !binding.HasCurrent)) continue;
                var state = Capture(binding.Native);
                if (Same(state, binding.Current)) continue;
                binding.Current = state; channel.Publish(binding.Id, state.Encode());
            }
        }

        private void RetryPurchasedBindings()
        {
            foreach (var binding in bindings.Values.Where(value => value.Purchased).ToArray())
                TryInitializePurchasedBinding(binding);
        }

        private bool TryInitializePurchasedBinding(Binding binding)
        {
            if (!IsPurchasedNativeReady(binding)) return false;
            WindowTintState state;
            try { state = Capture(binding.Native); }
            catch (Exception) { return false; }
            if (!binding.OriginalCaptured)
            {
                binding.Original = state;
                binding.OriginalCaptured = true;
            }
            if (!binding.HasCurrent)
            {
                binding.Current = state;
                binding.HasCurrent = true;
            }
            TryApplyPendingRecord(binding);
            EnsureHostPurchasedRecord(binding);
            return true;
        }

        private static bool IsPurchasedNativeReady(Binding binding)
        {
            if (!binding.Purchased || binding.Native == null || !binding.Native.gameObject.activeInHierarchy ||
                Initialized?.GetValue(binding.Native) is not true) return false;
            if (binding.Native is not Vehicle_WindowTint_Second) return true;
            if (SecondWindowMeshField?.GetValue(binding.Native) is not MeshRenderer secondMesh ||
                SecondMatIndexField?.GetValue(binding.Native) is not int secondMatIndex ||
                SecondMatInteriorIndexField?.GetValue(binding.Native) is not int secondMatInteriorIndex) return false;
            try
            {
                var materials = secondMesh.materials;
                if (secondMatIndex < 0 || secondMatIndex >= materials.Length || materials[secondMatIndex] == null) return false;
                return secondMatInteriorIndex == -1 ||
                    secondMatInteriorIndex >= 0 && secondMatInteriorIndex < materials.Length && materials[secondMatInteriorIndex] != null;
            }
            catch (Exception) { return false; }
        }

        private void EnsureHostPurchasedRecord(Binding binding)
        {
            if (!host || channel == null || !binding.Purchased || !binding.HasCurrent || channel.Records.ContainsKey(binding.Id)) return;
            channel.Register(new WorldRecord(binding.Id, 1, WindowTintState.Adapter, binding.Current.Encode()));
        }

        private void HydrateRetainedRecord(Binding binding)
        {
            if (binding.Purchased && deferredPurchasedRecords.TryGetValue(binding.Id, out var deferred))
                RetainPendingRecord(binding, deferred);
            if (!host && channel?.Records.TryGetValue(binding.Id, out var retained) == true &&
                retained.Adapter == WindowTintState.Adapter)
                RetainPendingRecord(binding, retained);
        }

        private void RetainDeferredRecord(WorldRecord record)
        {
            if (!deferredPurchasedRecords.TryGetValue(record.Id, out var existing) || Wire.IsNewer(record.Revision, existing.Revision))
                deferredPurchasedRecords[record.Id] = record;
        }

        private static void RetainPendingRecord(Binding binding, WorldRecord record)
        {
            if (binding.PendingRecord == null || Wire.IsNewer(record.Revision, binding.PendingRecord.Revision))
                binding.PendingRecord = record;
        }

        private void TryApplyPendingRecord(Binding binding)
        {
            if (host || !binding.Purchased || binding.PendingRecord == null || !binding.OriginalCaptured || !IsPurchasedNativeReady(binding))
                return;
            var record = binding.PendingRecord;
            var state = WindowTintState.Decode(record.Payload);
            Apply(binding.Native, state);
            binding.Current = state; binding.HasCurrent = true; binding.PendingRecord = null;
            if (deferredPurchasedRecords.TryGetValue(binding.Id, out var deferred) &&
                deferred.Revision == record.Revision && deferred.Adapter == record.Adapter)
                deferredPurchasedRecords.Remove(binding.Id);
        }
        private static WindowTintState Capture(Vehicle_WindowTint native)
        {
            var c = native.Get_WindowTintColor;
            return new WindowTintState(native.Get_IsTint, c[0], c[1], c[2], c[3]);
        }
        private void Apply(Vehicle_WindowTint native, WindowTintState state)
        {
            applying = true;
            try
            {
                native.ApplyTint(new Color(state.Red, state.Green, state.Blue, state.Alpha), true);
                if (!state.Tinted) IsTint.SetValue(native, false);
            }
            finally { applying = false; }
        }
        private static bool Same(WindowTintState a, WindowTintState b) => a.SameValue(b);
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null)
            {
                channel.CommandRequested -= Command; channel.CommandRejected -= Stale;
                channel.ReplyReceived -= Reply; channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed;
            }
            RestoreGuestOriginals();
            presence.Clear(); budgets.Clear(); pending.Clear(); deferredPurchasedRecords.Clear(); session = null; channel = null;
        }

        private void RestoreGuestOriginals()
        {
            if (!host) foreach (var binding in bindings.Values)
                if (binding.Native != null && binding.OriginalCaptured) Apply(binding.Native, binding.Original);
        }
    }
}
