using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using MonMP.Plugin.Diagnostics;
using NWH.VehiclePhysics2;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // Host-owned pressure pulses for shared-fleet sockets and registered loose/tire-changer
    // wheel items. Loose wheel results use their existing authoritative item records.
    internal sealed class SharedTirePressure : IDisposable
    {
        private sealed class WheelBinding
        {
            public ulong Id;
            public ulong SocketId;
            public bool Purchased;
            public Vehicle_PartSocket Socket = null!;
            public Vehicle_Type VehicleType;
            public Scene OriginalScene;
            public Part_Driveline_Wheel? Wheel;
            public TirePressureState Current;
            public TirePressureState Original;
            public bool OriginalCaptured = true;
            public TirePressureState? RemotePending;
            public bool RemoteStateApplied;
            public Part_Driveline_Wheel? AppliedRemoteWheel;
        }

        private sealed class CompressorBinding
        {
            public ulong Id;
            public AirCompressor Native = null!;
            public AirCompressorState Current;
            public AirCompressorState Original;
            public bool PresentedMotor;
            public Quaternion OriginalFlywheel;
            public Scene OriginalScene;
        }

        private sealed class AirToolBinding
        {
            public ulong Id;
            public Tool_Air_TirePump Native = null!;
            public AirCompressor Compressor = null!;
            public ulong CompressorId;
        }

        private sealed class LocalInput
        {
            public bool Inflate, Deflate;
            public bool InflateSoundStarted, DeflateSoundStarted;
            public float LastPulse;
            public TirePumpKind LastKind;
            public TirePumpAction LastAction;
        }

        private readonly bool host;
        private readonly Gameplay gameplay;
        private readonly SharedItems items;
        private readonly SharedVehicleParts parts;
        private readonly NativeEntityRegistry identities;
        private readonly Dictionary<ulong, WheelBinding> wheels = new Dictionary<ulong, WheelBinding>();
        private readonly Dictionary<ulong, WheelBinding> wheelsBySocket = new Dictionary<ulong, WheelBinding>();
        private readonly Dictionary<Part_Driveline_Wheel, WheelBinding> byWheel = new Dictionary<Part_Driveline_Wheel, WheelBinding>();
        private readonly Dictionary<ulong, CompressorBinding> compressors = new Dictionary<ulong, CompressorBinding>();
        private readonly Dictionary<AirCompressor, CompressorBinding> compressorByNative = new Dictionary<AirCompressor, CompressorBinding>();
        private readonly Dictionary<ulong, AirToolBinding> airTools = new Dictionary<ulong, AirToolBinding>();
        private readonly Dictionary<Tool_Air_TirePump, AirToolBinding> airToolByNative = new Dictionary<Tool_Air_TirePump, AirToolBinding>();
        private readonly Dictionary<InteractableTools, LocalInput> localInputs = new Dictionary<InteractableTools, LocalInput>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Dictionary<uint, (float Tokens, float Time)> budgets = new Dictionary<uint, (float, float)>();
        private readonly Dictionary<ulong, (uint Owner, uint Generation, float Expires)> airUseLeases =
            new Dictionary<ulong, (uint, uint, float)>();
        private readonly HashSet<(Guid Profile, Guid Operation)> claims = new HashSet<(Guid, Guid)>();
        private readonly TirePumpPulseClock pulseClock = new TirePumpPulseClock();
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool applying, disposed, blocked;
        private float nextCapture, nextPurchasedDiscovery;
        private InteractableTools? sampledHostTool;
        private float sampledHostPressure, nextHostCue;
        private bool hostCueActive;

        internal const float CaptureInterval = 0.1f;
        internal const float PurchasedSocketDiscoveryInterval = 1f;
        internal static SharedTirePressure? Current { get; private set; }

#if MONMP_DEVELOPMENT
        internal int TestAccepted { get; private set; }


        internal int TestRejected { get; private set; }


        internal int TestWheelCount => wheels.Count;


        internal int TestCompressorCount => compressors.Count;


        internal ulong TestFirstAirTool => airTools.Values.Where(t => t.Native != null && t.Native.gameObject.activeInHierarchy).OrderBy(t => t.Id).Select(t => t.Id).FirstOrDefault();
#endif

        internal IEnumerable<ulong> SceneAirToolIds => airTools.Keys;
        internal Func<uint, ulong, uint, bool>? SceneAirHolderAllowsUse;
        internal Func<uint, ulong, uint>? SceneAirHolderGeneration;

        internal uint AirGeneration(uint peer, ulong id) => SceneAirHolderGeneration?.Invoke(peer, id) ?? 0;


        internal bool TryGetSceneAirTool(ulong id, out Tool_Air_TirePump tool, out AirCompressor compressor)
        {
            tool = null!; compressor = null!;
            if (!airTools.TryGetValue(id, out var binding) || binding.Native == null || binding.Compressor == null) return false;
            tool = binding.Native; compressor = binding.Compressor; return true;
        }

#if MONMP_DEVELOPMENT
        internal Tool_Air_TirePump TestAirTool(ulong id) => airTools[id].Native;


        internal AirCompressor TestAirCompressor(ulong id) => airTools[id].Compressor;


        internal Part_Driveline_Wheel TestWheel(ulong id) => wheels.TryGetValue(id, out var binding) ? binding.Wheel! : items.ItemOf(id) as Part_Driveline_Wheel ?? throw new InvalidOperationException("No shared inflation wheel.");


        internal bool TestCameraHits(Camera camera, ulong id) => TryRayWheel(camera, out var binding, out _) && binding.Id == id;


        internal bool TestRejectWrongAirLease(uint peer, ulong toolId, ulong wheelId, uint rejectedGeneration = 0)
        {
            if (!host || !presence.TryGetValue(peer, out var seen) || !airTools.TryGetValue(toolId, out var tool)) return false;
            var wheel = TestWheel(wheelId);
            float before = wheel.Value1, airBefore = tool.Compressor.Get_AirPressure;
            int rejected = TestRejected;
            uint current = AirGeneration(peer, toolId);
            uint wrong = rejectedGeneration != 0 ? rejectedGeneration : current == uint.MaxValue ? 1u : current + 1;
            var pose = seen.Pose;
            var command = new TirePumpCommand(Guid.NewGuid(), toolId, wrong, TirePumpKind.Compressor,
                TirePumpAction.Inflate, wheelId, pose.X, pose.Y + 1.5f, pose.Z, pose.Yaw, pose.Pitch);
            Command(peer, wheelId, command.Encode());
            return TestRejected == rejected + 1 && wheel.Value1 == before && tool.Compressor.Get_AirPressure == airBefore;
        }
#endif


        private readonly AimSegmentTest checkSegment;

        internal SharedTirePressure(AimSegmentTest checkSegment, Gameplay gameplay, bool host, SharedItems items,
            SharedVehicleParts parts, NativeEntityRegistry identities)
        {
            this.checkSegment = checkSegment;
            this.gameplay = gameplay; this.host = host; this.items = items; this.parts = parts; this.identities = identities;
            if (Current != null) throw new InvalidOperationException("A tire pressure adapter is already active.");

            foreach (var (socketId, socket, type) in parts.TireSockets.OrderBy(value => value.SocketId))
                RegisterWheelSocket(socketId, socket, type);

            var candidates = Object.FindObjectsOfType<Tool_Air_TirePump>(true)
                .Where(tool => tool != null && tool.gameObject.scene.IsValid() &&
                    FieldInfoCache.AirPumpCompressor.GetValue(tool) is AirCompressor compressor && compressor != null)
                .Select(tool => (Tool: tool, Path: RuntimeProbe.CanonicalPath(tool.transform),
                    Index: Array.IndexOf(tool.GetComponents<Tool_Air_TirePump>(), tool),
                    Compressor: (AirCompressor)FieldInfoCache.AirPumpCompressor.GetValue(tool)!))
                .Where(value => value.Index >= 0)
                .OrderBy(value => value.Path, StringComparer.Ordinal).ThenBy(value => value.Index).ToArray();

            foreach (var compressor in candidates.Select(value => value.Compressor).Distinct())
            {
                string path = RuntimeProbe.CanonicalPath(compressor.transform);
                int index = Array.IndexOf(compressor.GetComponents<AirCompressor>(), compressor);
                if (index < 0) continue;
                ulong id = StaticId.Hash(path + "/shared-air-compressor-v1/" + index);
                var state = Capture(compressor);
                var binding = new CompressorBinding { Id = id, Native = compressor, Current = state, Original = state, OriginalScene = compressor.gameObject.scene,
                    OriginalFlywheel = (FieldInfoCache.CompressorFlywheel.GetValue(compressor) as Transform)?.localRotation ?? Quaternion.identity };
                if (compressors.TryGetValue(id, out var existing) && existing.Native != compressor)
                    throw new InvalidOperationException("Conflicting air compressor identity: " + path);
                compressors[id] = binding; compressorByNative[compressor] = binding;
                identities.Bind(id, AirCompressorState.Adapter, compressor.gameObject,
                    "monmp/air-compressor/v1/" + path + "/" + index, true, EntityPersistence.SessionOnly);
            }

            foreach (var candidate in candidates)
            {
                var compressor = compressorByNative[candidate.Compressor];
                ulong id = StaticId.Hash(candidate.Path + "/air-tire-pump-use-v1/" + candidate.Index);
                var binding = new AirToolBinding { Id = id, Native = candidate.Tool, Compressor = candidate.Compressor,
                    CompressorId = compressor.Id };
                if (airTools.TryGetValue(id, out var existing) && existing.Native != candidate.Tool)
                    throw new InvalidOperationException("Conflicting static air pump identity: " + candidate.Path);
                airTools[id] = binding; airToolByNative[candidate.Tool] = binding;
            }

            FieldInfoCache.Validate();
            parts.PurchasedBaselineCapturing += CapturePurchasedWheelBaseline;
            Current = this;
        }

        internal bool Ready => channel?.FullySynchronized == true && !blocked && !disposed;
        internal bool TransactionsSettled => true;
        internal IEnumerable<ulong> RequiredCatalogEntityIds => wheels.Values
            .Where(binding => !binding.Purchased && binding.Id > WorldRecord.DynamicIdStart)
            .Select(binding => binding.Id);
        internal bool IsPurchasedRecord(ulong id) => wheels.TryGetValue(id, out var binding) && binding.Purchased;

        internal IEnumerable<WorldRecord> InitialRecords()
        {
            DiscoverPurchasedWheelSockets();
            return wheels.Values.Select(b => new WorldRecord(b.Id, 1, TirePressureState.Adapter, b.Current.Encode()))
                .Concat(compressors.Values.Select(b => new WorldRecord(b.Id, 1, AirCompressorState.Adapter, b.Current.Encode())));
        }

        private void RegisterWheelSocket(ulong socketId, Vehicle_PartSocket socket, Vehicle_Type type)
        {
            bool purchased = parts.IsPurchasedRecord(socketId);
            if (wheelsBySocket.TryGetValue(socketId, out var existing))
            {
                if (existing.Socket != socket || existing.Purchased != purchased)
                    throw new InvalidOperationException("Conflicting shared tire socket identity: " + socketId.ToString("X16"));
                if (!host) HydrateRetainedPressure(existing);
                return;
            }

            string key = "monmp/tire-pressure/v1/" + socketId.ToString("X16");
            ulong id = purchased
                ? ScrapyardKonigSupportPolicy.DynamicEntityId("monmp/tire-pressure/purchased/v1/" + socketId.ToString("X16"))
                : type == Vehicle_Type.ScrapyardKonig
                    ? ScrapyardKonigSupportPolicy.DynamicEntityId(key)
                    : StaticId.Hash(key);
            if (wheels.ContainsKey(id)) throw new InvalidOperationException("Duplicate shared tire socket identity: " + key);
            var binding = new WheelBinding { Id = id, SocketId = socketId, Socket = socket, VehicleType = type,
                Purchased = purchased, OriginalCaptured = !purchased || host, OriginalScene = socket.gameobject.scene };
            binding.Wheel = ResolveWheel(socket);
            binding.Current = Capture(binding.Wheel);
            binding.Original = binding.Current;
            wheels.Add(id, binding);
            wheelsBySocket.Add(socketId, binding);
            if (binding.Wheel != null) byWheel[binding.Wheel] = binding;
            identities.Bind(id, TirePressureState.Adapter, socket.gameobject, key, true, EntityPersistence.SessionOnly);

            if (host && channel != null)
                channel.Register(new WorldRecord(id, 1, TirePressureState.Adapter, binding.Current.Encode()));
            else if (!host)
                HydrateRetainedPressure(binding);
        }

        private void CapturePurchasedWheelBaseline(ulong socketId)
        {
            if (host || disposed) return;
            if (!wheelsBySocket.TryGetValue(socketId, out var binding))
            {
                foreach (var (candidateId, socket, type) in parts.TireSockets)
                    if (candidateId == socketId && parts.IsPurchasedRecord(candidateId))
                    {
                        RegisterWheelSocket(candidateId, socket, type);
                        break;
                    }
                if (!wheelsBySocket.TryGetValue(socketId, out binding)) return;
            }
            if (!binding.Purchased || binding.OriginalCaptured) return;
            binding.Original = Capture(ResolveWheel(binding.Socket));
            binding.OriginalCaptured = true;
        }

        private void DiscoverPurchasedWheelSockets()
        {
            // TireSockets is the small shared fleet socket ledger; avoid scene-wide searches here.
            foreach (var (socketId, socket, type) in parts.TireSockets)
                if (parts.IsPurchasedRecord(socketId)) RegisterWheelSocket(socketId, socket, type);
        }

        private void HydrateRetainedPressure(WheelBinding binding)
        {
            if (channel == null || !channel.Records.TryGetValue(binding.Id, out var retained) ||
                retained.Adapter != TirePressureState.Adapter) return;
            binding.RemotePending = TirePressureState.Decode(retained.Payload);
        }

        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            DiscoverPurchasedWheelSockets();
            if (!host)
                foreach (var binding in compressors.Values)
                {
                    // MotorRunning is the compressor's only coroutine. Stop an offline motor
                    // before mirroring the host, so it cannot charge or rotate a second time.
                    binding.Native.StopAllCoroutines();
                    StopCompressorSound(binding.Native);
                    FieldInfoCache.CompressorMotor.SetValue(binding.Native, false);
                }
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.CommandRequested += Command; channel.Changed += Changed; channel.SnapshotApplied += Snapshot;
        }

        internal void SetNewActionsBlocked(bool value) => blocked = value;

        // Harmony guards the guest's native coroutine writes and pop method. Host player input
        // remains native, then Update publishes the host's resulting state.
        internal bool AllowNativePressureMutation(ref float value)
        {
            if (disposed || session?.IsRunning != true) return true;
            value = Mathf.Clamp(value, -100000f, 100f);
            return host || applying || items.Applying;
        }

        internal bool AllowNativeTirePop() => disposed || session?.IsRunning != true || host || applying;
        internal bool AllowNativeCompressorUse() => disposed || session?.IsRunning != true || host || applying;
        internal bool AllowNativeCompressorMotor(AirCompressor compressor) => disposed || session?.IsRunning != true || host || applying || !compressorByNative.ContainsKey(compressor);

        internal bool InterceptMouseDown(InteractableTools tool, TirePumpAction action)
        {
            if (disposed || session?.IsRunning != true || host) return true;
            // Taking the speedway or garage hose off its compressor clicks the unequipped pump too; its
            // native MouseDown ignores that, and the lease message it raised read as a failure (retest).
            if (!tool.isEquiped || gameplay.Player.Get_ActiveTool != tool) return true;
            if (!Ready || !LocalToolIdentity(tool, out _, out _, out _))
            {
                Debug.Log("MonMP tire pump input refused: ready=" + Ready + " tool=" + tool.GetType().Name + " identity=" + LocalToolIdentity(tool, out _, out _, out _));
                MonMPPlugin.Instance?.ReportItemUseUnavailable("Wait for a synchronized tire pump lease before pumping a shared-fleet wheel.");
                return false;
            }
            var input = GetInput(tool);
            if (action == TirePumpAction.Inflate) input.Inflate = true; else input.Deflate = true;
            return false;
        }

        internal bool InterceptMouseUp(InteractableTools tool, TirePumpAction action)
        {
            if (disposed || session?.IsRunning != true || host) return true;
            if (localInputs.TryGetValue(tool, out var input))
            {
                if (action == TirePumpAction.Inflate) input.Inflate = false; else input.Deflate = false;
                if (action == TirePumpAction.Inflate && tool is Tool_Air_TirePump && input.InflateSoundStarted)
                {
                    try
                    {
                        Singleton<SOUND_Manager>.i.PlaySound_AirTirePump_AirIn_Stop();
                    }
                    catch (Exception ex) { Debug.LogWarning("MonMP tire pump stop feedback failed: " + ex.Message); }
                    input.InflateSoundStarted = false;
                }
                if (action == TirePumpAction.Deflate && input.DeflateSoundStarted)
                {
                    try { Singleton<SOUND_Manager>.i.PlaySound_TirePump_AirOut_Stop(); }
                    catch (Exception ex) { Debug.LogWarning("MonMP tire pump stop feedback failed: " + ex.Message); }
                    input.DeflateSoundStarted = false;
                }
                if (!input.Inflate && !input.Deflate) localInputs.Remove(tool);
            }
            return false;
        }

        internal void Update()
        {
            if (disposed || session?.IsRunning != true || channel == null) return;
            float now = Time.unscaledTime;
            if (now >= nextPurchasedDiscovery)
            {
                nextPurchasedDiscovery = now + PurchasedSocketDiscoveryInterval;
                DiscoverPurchasedWheelSockets();
            }
            if (!host && channel.FullySynchronized) TickGuestInput();
            if (host) TickHostPresentation();
            else foreach (var compressor in compressors.Values) PresentCompressor(compressor);
            if (now < nextCapture) return;
            nextCapture = now + CaptureInterval;
            if (host) CaptureAndPublishHost();
            else ApplyPendingGuestState();
        }

        private void TickGuestInput()
        {
            float now = Time.unscaledTime;
            foreach (var pair in localInputs.ToArray())
            {
                var tool = pair.Key; var input = pair.Value;
                if (!tool || !tool.isEquiped || gameplay.Player.Get_ActiveTool != tool)
                { StopLocalSounds(tool, input); localInputs.Remove(tool); continue; }
                if (!input.Inflate && !input.Deflate) { localInputs.Remove(tool); continue; }
                if (!LocalToolIdentity(tool, out ulong toolId, out uint generation, out TirePumpKind kind))
                { StopLocalSounds(tool, input); localInputs.Remove(tool); continue; }
                TirePumpAction action = input.Inflate ? TirePumpAction.Inflate : TirePumpAction.Deflate;
                if (input.LastPulse > 0 && now < input.LastPulse + TirePumpPolicy.PulseInterval(input.LastKind, input.LastAction)) continue;
                var camera = ToolCamera(tool);
                if (camera == null || !TryRayWheel(camera, out var target, out var aim) || !target.Current.HasTire) continue;
                if (action == TirePumpAction.Inflate && target.Current.Pressure >= 100 ||
                    action == TirePumpAction.Deflate && target.Current.Pressure <= -100000f) continue;

                TirePumpCommand command;
                try
                {
                    command = new TirePumpCommand(Guid.NewGuid(), toolId, generation, kind, action, target.Id,
                        aim.Origin.x, aim.Origin.y, aim.Origin.z, aim.Yaw, aim.Pitch, aim.Independent);
                }
                catch (InvalidDataException) { continue; }
                channel!.Request(target.Id, command.Encode(), true);
                input.LastPulse = now; input.LastKind = kind; input.LastAction = action;
                LocalFeedback(tool, action, kind, input, target.Current.Pressure);
            }
        }

        private bool LocalToolIdentity(InteractableTools tool, out ulong id, out uint generation, out TirePumpKind kind)
        {
            id = 0; generation = 0; kind = default;
            if (tool is Tool_TirePump manual)
            {
                if (!items.TryGetLocalToolLease(manual, out id, out generation)) return false;
                kind = TirePumpKind.Manual; return true;
            }
            if (tool is Tool_Air_TirePump air && airToolByNative.TryGetValue(air, out var binding))
            {
                id = binding.Id; generation = session == null ? 0 : AirGeneration(session.LocalPeerId, id);
                kind = TirePumpKind.Compressor; return generation != 0;
            }
            return false;
        }

        private static Camera? ToolCamera(InteractableTools tool) => tool switch
        {
            Tool_TirePump manual => FieldInfoCache.ManualCamera.GetValue(manual) as Camera,
            Tool_Air_TirePump air => FieldInfoCache.AirCamera.GetValue(air) as Camera,
            _ => null
        };

        private bool TryRayWheel(Camera camera, out WheelBinding binding, out Integration.ToolAimSample aim)
        {
            binding = null!; aim = default;
            if (camera == null) return false;
            aim = Integration.LocalInteraction.SampleTool(camera);
            var wheel = ResolveRayWheel(aim.Ray, out _);
            if (wheel == null) return false;
            if (parts.TryGetTireSocket(wheel, out ulong socketId, out _, out _) && wheelsBySocket.TryGetValue(socketId, out binding)) { }
            else
            {
                ulong id = items.EntityOf(wheel);
                if (id == 0 || !items.TryGetSharedWheel(id, out var registered, out _) || registered != wheel) return false;
                binding = new WheelBinding { Id = id, Wheel = wheel, Current = Capture(wheel) };
            }
            binding.Wheel = wheel;
            if (binding.SocketId != 0 && !byWheel.ContainsKey(wheel)) byWheel[wheel] = binding;
            binding.Current = Capture(wheel);
            return true;
        }


        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (!TirePumpCommand.HasMarker(payload)) return;
            if (!host || session?.IsHost != true || channel == null || peer == 0) return;
            if (!wheels.TryGetValue(entity, out var binding))
            {
                if (!items.TryGetSharedWheel(entity, out var loose, out var lease) ||
                    lease.Owner != ItemLease.Unowned && lease.Owner != peer) {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 return; }
                binding = new WheelBinding { Id = entity, Wheel = loose, Current = Capture(loose) };
            }
            TirePumpCommand command;
            try { command = TirePumpCommand.Decode(payload); }
            catch (InvalidDataException) {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 return; }
            if (command.Wheel != entity) {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 return; }
            if (!session.Peers.TryGetValue(peer, out var remote)) return;
            var claim = (remote.ProfileId, command.Operation);
            if (claims.Contains(claim)) return;
            if (claims.Count >= 16384) {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 return; }
            claims.Add(claim);

            float now = Time.unscaledTime;
            if (blocked || !channel.FullySynchronized || !TakeBudget(peer, now) ||
                !presence.TryGetValue(peer, out var pose) || now - pose.Time > TirePumpPolicy.FreshPoseSeconds ||
                !ItemInteractionPosePolicy.Allows(pose.Pose) || !command.CameraFits(pose.Pose, checkSegment) ||
                !TargetIsActiveSharedWheel(binding))
            {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 return; }
            if (!HostRayHits(command, binding, out bool rayUsedGeneratedPart)) {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 return; }

            bool exactLease;
            float compressorPressure = float.NaN;
            AirToolBinding? airTool = null;
            if (command.Kind == TirePumpKind.Manual)
            {
                exactLease = items.HostHasLeasedTool(peer, command.Tool, command.Generation, typeof(Tool_TirePump)) &&
                    items.HostItemInRange(peer, command.Tool, 4.5f);
            }
            else
            {
                exactLease = TryUseAirToolLease(peer, command.Tool, command.Generation, now, pose.Pose, out airTool);
                if (exactLease && airTool != null && compressorByNative.TryGetValue(airTool.Compressor, out var compressor))
                    compressorPressure = airTool.Compressor.Get_AirPressure;
                else exactLease = false;
            }

            var wheel = binding.Wheel;
            float pressure = wheel == null ? 0 : wheel.Value1;
            bool canPressureChange = command.Action == TirePumpAction.Inflate
                ? TirePumpPolicy.CanInflate(pressure, wheel != null && wheel.IntValue1 > 0, command.Kind, compressorPressure)
                : TirePumpPolicy.CanDeflate(pressure, wheel != null && wheel.IntValue1 > 0);
            bool requestBudget = exactLease && canPressureChange && CanTakePulse(peer, now);
            bool authorized = TirePumpPolicy.CanExecuteHostRequest(channel.FullySynchronized, blocked, requestBudget,
                exactLease, true, true, TargetIsActiveSharedWheel(binding), true, true, command.Kind,
                command.Action, wheel != null && wheel.IntValue1 > 0, pressure, compressorPressure);
            if (!authorized || wheel == null || !pulseClock.TryRecord(peer, command.Tool, command.Generation,
                command.Kind, command.Action, now))
            {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 return; }

            try
            {
                if (command.Action == TirePumpAction.Inflate) wheel.Value1 = Mathf.Min(100f, pressure + 1f);
                else wheel.Value1 = pressure - 1f;
                bool popped = command.Action == TirePumpAction.Inflate && wheel.Value1 > 50f && UnityEngine.Random.Range(0f, 1f) > 0.6f;
                if (popped)
                    wheel.PopTheTire();
                if ((rayUsedGeneratedPart || popped) && binding.Socket != null)
                    binding.Socket.vehicleAttached?.Update_Wheels();
                if (command.Kind == TirePumpKind.Compressor && command.Action == TirePumpAction.Inflate)
                    airTool!.Compressor.UsingAir();
                binding.Current = Capture(wheel);
                if (binding.SocketId != 0) channel.Publish(binding.Id, binding.Current.Encode());
                else items.HostPublishItem(binding.Id);
                if (command.Kind == TirePumpKind.Compressor && airTool != null && compressorByNative.TryGetValue(airTool.Compressor, out var compressorBinding))
                {
                    compressorBinding.Current = Capture(airTool.Compressor);
                    channel.Publish(compressorBinding.Id, compressorBinding.Current.Encode());
                }

#if MONMP_DEVELOPMENT
                TestAccepted++;
#endif

                byte cue = popped ? TirePumpPresentation.Pop : command.Action == TirePumpAction.Deflate ?
                    (pressure > 1 ? TirePumpPresentation.Deflate : TirePumpPresentation.Stop) :
                    command.Kind == TirePumpKind.Manual ? TirePumpPresentation.Stroke : TirePumpPresentation.Inflate;
                PresentationReplays.Events?.HostAnnounce(PresentationKind.TirePump, cue, command.Tool, wheel.transform.position, 0, peer, true);
            }
            catch (Exception ex)
            {
                Debug.LogError("MonMP host tire pressure pulse failed: " + ex);

#if MONMP_DEVELOPMENT
                TestRejected++;
#endif

            }
        }

        private bool TryUseAirToolLease(uint peer, ulong toolId, uint generation, float now, PlayerPose pose,
            out AirToolBinding? binding)
        {
            binding = null;
            if (!airTools.TryGetValue(toolId, out var found) || generation == 0 ||
                found.Native == null || !found.Native.gameObject.activeInHierarchy) return false;
            if (found.Native.isEquiped && gameplay.Player.Get_ActiveTool == found.Native) return false;
            if (SceneAirHolderAllowsUse?.Invoke(peer, toolId, generation) != true) return false;
            Vector3 player = new Vector3(pose.X, pose.Y, pose.Z);
            if ((player - found.Compressor.transform.position).sqrMagnitude > 20.5f * 20.5f) return false;
            // The stock compressor gun is scene-mounted, `IgnoreSaveSystem`, and cannot be grabbed,
            // so it has no SharedItems lease. Give that exact catalogued scene tool a short exclusive
            // use lease on the first valid pulse, constrained to the native 20 m hose reach.
            if (airUseLeases.TryGetValue(toolId, out var lease) && lease.Expires > now && lease.Owner != peer) return false;
            airUseLeases[toolId] = (peer, generation, now + 1f);
            binding = found;
            return true;
        }

        private bool TargetIsActiveSharedWheel(WheelBinding binding)
        {
            if (binding.SocketId == 0)
                return items.TryGetSharedWheel(binding.Id, out var looseWheel, out _) && looseWheel == binding.Wheel &&
                    looseWheel.gameObject.activeInHierarchy && looseWheel.gameObject.scene.IsValid() && !looseWheel.isPlaced;
            if (binding.Socket == null || binding.Socket.gameobject == null || !binding.Socket.gameobject.activeInHierarchy ||
                !binding.Socket.gameobject.scene.IsValid() || Array.IndexOf(SharedVehicleParts.Fleet, binding.VehicleType) < 0)
                return false;
            var wheel = ResolveWheel(binding.Socket);
            if (wheel == null) { binding.Wheel = null; return false; }
            if (binding.Wheel != wheel)
            {
                if (binding.Wheel != null) byWheel.Remove(binding.Wheel);
                binding.Wheel = wheel; byWheel[wheel] = binding;
            }
            return true;
        }

        private bool HostRayHits(TirePumpCommand command, WheelBinding binding, out bool generatedPartHit)
        {
            generatedPartHit = false;
            Vector3 origin = new Vector3(command.CameraX, command.CameraY, command.CameraZ);
            Vector3 direction = Quaternion.Euler(command.CameraPitch, command.CameraYaw, 0f) * Vector3.forward;
            var wheel = ResolveRayWheel(new Ray(origin, direction), out generatedPartHit);
            if (binding.SocketId == 0) return wheel != null && wheel == binding.Wheel && items.EntityOf(wheel) == binding.Id;
            return wheel != null && parts.TryGetTireSocket(wheel, out ulong socketId, out _, out _) &&
                socketId == binding.SocketId;
        }

        private static Part_Driveline_Wheel? ResolveRayWheel(Ray ray, out bool generatedPartHit)
        {
            generatedPartHit = false;
            int mask = LayerMask.GetMask("InteractableItem") | LayerMask.GetMask("PartSocket");
            Part_Driveline_Wheel? wheel = null;
            if (Physics.Raycast(ray, out var hit, TirePumpPolicy.MaxTargetRange, mask))
            {
                wheel = hit.collider.GetComponent<Part_Driveline_Wheel>();
                var generated = wheel == null ? hit.collider.GetComponent<Generated_Part>() : null;
                if (generated?.enginePartsType == Car_PartsType.Wheel && generated.carPartSocket?.partInstalled != null)
                {
                    wheel = generated.carPartSocket.partInstalled.GetComponent<Part_Driveline_Wheel>();
                    generatedPartHit = wheel != null;
                }
            }
            // Native air/manual pumps use this second ray only when the ordinary ray did
            // not resolve a wheel; preserve that ordering and exact tire-changer detector.
            if (wheel == null && Physics.Raycast(ray, out hit, TirePumpPolicy.MaxTargetRange, LayerMask.GetMask("Default")))
                wheel = hit.collider.GetComponent<Furniture_TireChanger_WheelDetection>()?.Get_Wheel;
            return wheel;
        }

        private bool CanTakePulse(uint peer, float now)
        {
            if (!presence.TryGetValue(peer, out var seen) || now - seen.Time > TirePumpPolicy.FreshPoseSeconds ||
                !ItemInteractionPosePolicy.Allows(seen.Pose)) return false;
            return true;
        }

        private bool TakeBudget(uint peer, float now)
        {
            var state = budgets.TryGetValue(peer, out var old) ? old : (Tokens: 16f, Time: now);
            state.Tokens = Mathf.Min(20f, state.Tokens + Mathf.Max(0f, now - state.Time) * 20f);
            state.Time = now;
            if (state.Tokens < 1f) { budgets[peer] = state; return false; }
            state.Tokens -= 1f; budgets[peer] = state; return true;
        }

        private void CaptureAndPublishHost()
        {
            foreach (var binding in wheels.Values)
            {
                if (!TargetIsActiveSharedWheel(binding))
                {
                    if (binding.Current.HasTire)
                    {
                        binding.Current = new TirePressureState(0, 0);
                        channel!.Publish(binding.Id, binding.Current.Encode());
                    }
                    continue;
                }
                var state = Capture(binding.Wheel);
                if (!state.Equals(binding.Current)) { binding.Current = state; channel!.Publish(binding.Id, state.Encode()); }
            }
            foreach (var binding in compressors.Values)
            {
                if (binding.Native == null) continue;
                var state = Capture(binding.Native);
                if (!state.Equals(binding.Current)) { binding.Current = state; channel!.Publish(binding.Id, state.Encode()); }
            }
        }

        private void ApplyPendingGuestState()
        {
            foreach (var binding in wheels.Values)
            {
                // Structural part records can recreate a wheel without changing its pressure record.
                // Only replay a received state, and remember the applied native independently of
                // Wheel: ray/target resolution can already have rebound that lookup to the clone.
                if (!binding.RemotePending.HasValue && !binding.RemoteStateApplied) continue;
                if (binding.Purchased && (!binding.OriginalCaptured || !parts.IsPurchasedNativeReady(binding.SocketId))) continue;
                if (!TargetIsActiveSharedWheel(binding)) continue;
                if (!binding.RemotePending.HasValue && binding.AppliedRemoteWheel == binding.Wheel) continue;
                var state = binding.RemotePending ?? binding.Current;
                Apply(binding, state);
                binding.Current = state; binding.RemotePending = null;
                binding.RemoteStateApplied = true; binding.AppliedRemoteWheel = binding.Wheel;
            }
        }

        private void Apply(WheelBinding binding, TirePressureState state)
        {
            var wheel = ResolveWheel(binding.Socket);
            if (wheel == null) return;
            // Only a native blowout: the tire we last showed was inflated past the 50 psi pop threshold.
            // A newly mounted wheel (its tire arrives with the part before this record catches up) or a
            // removed tire is not a pop, and must not play the explosion for everyone (#106).
            bool popped = wheel.IntValue1 > 0 && !state.HasTire && binding.Current.HasTire && binding.Current.Pressure > 50f;
            bool wasApplying = applying; applying = true;
            try
            {
                if (wheel.IntValue1 != state.TireType) wheel.IntValue1 = state.TireType;
                if (!Mathf.Approximately(wheel.Value1, state.Pressure)) wheel.Value1 = state.Pressure;
                binding.Socket.vehicleAttached?.Update_Wheels();
                binding.Wheel = wheel; byWheel[wheel] = binding;
                if (popped) using (SpatialSounds.At(wheel.transform.position)) Singleton<SOUND_Manager>.i.PlaySound_TirePump_Explosion();
            }
            finally { applying = wasApplying; }
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            foreach (var record in records.Values)
            {
                if (record.Adapter == TirePressureState.Adapter && wheels.TryGetValue(record.Id, out var wheel))
                    wheel.RemotePending = TirePressureState.Decode(record.Payload);
                else if (record.Adapter == AirCompressorState.Adapter && compressors.TryGetValue(record.Id, out var compressor))
                {
                    compressor.Current = AirCompressorState.Decode(record.Payload);
                    Apply(compressor, compressor.Current);
                }
            }
        }

        private void Changed(WorldRecord record)
        {
            if (host) return;
            if (record.Adapter == TirePressureState.Adapter && wheels.TryGetValue(record.Id, out var binding))
                binding.RemotePending = TirePressureState.Decode(record.Payload);
            else if (record.Adapter == AirCompressorState.Adapter && compressors.TryGetValue(record.Id, out var compressor))
            {
                compressor.Current = AirCompressorState.Decode(record.Payload);
                Apply(compressor, compressor.Current);
            }
        }

        private void Apply(CompressorBinding binding, AirCompressorState state)
        {
            bool wasApplying = applying; applying = true;
            try { SetCompressorPressure(binding.Native, state.Pressure); }
            finally { applying = wasApplying; }
            PresentCompressor(binding, false);
        }

        private static bool CompressorSoundReady(AirCompressor compressor, out SOUND_AirCompressor? sound)
        {
            sound = compressor == null ? null : compressor.GetComponent<SOUND_AirCompressor>();
            return sound != null && FieldInfoCache.CompressorMotorSource.GetValue(sound) is AudioSource source && source != null;
        }
        private static void StopCompressorSound(AirCompressor compressor)
        {
            if (CompressorSoundReady(compressor, out var sound)) sound!.StopCompressor();
        }

        private void PresentCompressor(CompressorBinding binding, bool rotate = true)
        {
            if (binding.Native == null) return;
            if (CompressorSoundReady(binding.Native, out var sound))
            {
                if (binding.Current.MotorRunning != binding.PresentedMotor)
                {
                    if (binding.Current.MotorRunning) sound!.StartCompressor(); else sound!.StopCompressor();
                    binding.PresentedMotor = binding.Current.MotorRunning;
                }
            }
            FieldInfoCache.CompressorMotor.SetValue(binding.Native, binding.Current.MotorRunning);
            if (rotate && binding.Current.MotorRunning && FieldInfoCache.CompressorFlywheel.GetValue(binding.Native) is Transform flywheel && flywheel != null)
                flywheel.Rotate(Vector3.up * (1000f * Time.deltaTime), Space.Self);
        }

        private void TickHostPresentation()
        {
            var tool = gameplay.Player.Get_ActiveTool;
            var camera = tool == null ? null : ToolCamera(tool);
            if (!(tool is Tool_TirePump || tool is Tool_Air_TirePump) || !tool.isEquiped || camera == null ||
                !TryRayWheel(camera, out var target, out _) || !target.Current.HasTire)
            {
                StopHostCue(); sampledHostTool = null; return;
            }
            float pressure = target.Wheel!.Value1;
            if (sampledHostTool != tool) { StopHostCue(); sampledHostTool = tool; sampledHostPressure = pressure; return; }
            var down = tool is Tool_Air_TirePump ? FieldInfoCache.AirInflate : FieldInfoCache.ManualInflate;
            var right = tool is Tool_Air_TirePump ? FieldInfoCache.AirDeflate : FieldInfoCache.ManualDeflate;
            bool inflate = (bool)down.GetValue(tool), deflate = (bool)right.GetValue(tool);
            bool changed = pressure != sampledHostPressure;
            sampledHostPressure = pressure;
            if (!inflate && !deflate || deflate && pressure <= 1) { StopHostCue(); return; }
            if (!changed || Time.unscaledTime < nextHostCue) return;
            byte cue = deflate ? TirePumpPresentation.Deflate : tool is Tool_TirePump ? TirePumpPresentation.Stroke : TirePumpPresentation.Inflate;
            nextHostCue = Time.unscaledTime + .2f;
            hostCueActive = cue != TirePumpPresentation.Stroke;
            ulong id = items.EntityOf(tool);
            if (tool is Tool_Air_TirePump air && airToolByNative.TryGetValue(air, out var binding)) id = binding.Id;
            PresentationReplays.Events?.HostAnnounce(PresentationKind.TirePump, cue, id, target.Wheel.transform.position, 0, 0, false);
        }
        private void StopHostCue()
        {
            if (!hostCueActive) return;
            hostCueActive = false;
            PresentationReplays.Events?.HostAnnounce(PresentationKind.TirePump, TirePumpPresentation.Stop, 0,
                gameplay.PlayerWalking.transform.position, 0, 0, false);
        }

        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer)
        {
            presence.Remove(peer); budgets.Remove(peer); pulseClock.RemovePeer(peer);
            foreach (var id in airUseLeases.Where(pair => pair.Value.Owner == peer).Select(pair => pair.Key).ToArray()) airUseLeases.Remove(id);
        }

        private void LocalFeedback(InteractableTools tool, TirePumpAction action, TirePumpKind kind,
            LocalInput input, float pressure)
        {
            try
            {
                if (action == TirePumpAction.Deflate && pressure <= 1 && input.DeflateSoundStarted)
                { Singleton<SOUND_Manager>.i.PlaySound_TirePump_AirOut_Stop(); input.DeflateSoundStarted = false; }
                if (kind == TirePumpKind.Manual)
                {
                    if (action == TirePumpAction.Inflate)
                    {
                        var trigger = HarmonyLib.AccessTools.Method(typeof(Tool_TirePump), "TriggerPumpAnimation");
                        trigger?.Invoke(tool, null);
                        Singleton<SOUND_Manager>.i.PlaySound_TirePump();
                    }
                    else if (pressure > 1f && !input.DeflateSoundStarted)
                    { Singleton<SOUND_Manager>.i.PlaySound_TirePump_AirOut_Start(); input.DeflateSoundStarted = true; }
                }
                else if (action == TirePumpAction.Inflate && !input.InflateSoundStarted)
                { Singleton<SOUND_Manager>.i.PlaySound_AirTirePump_AirIn_Start(); input.InflateSoundStarted = true; }
                else if (action == TirePumpAction.Deflate && pressure > 1f && !input.DeflateSoundStarted)
                { Singleton<SOUND_Manager>.i.PlaySound_TirePump_AirOut_Start(); input.DeflateSoundStarted = true; }
            }
            catch (Exception ex) { Debug.LogWarning("MonMP tire pump feedback failed: " + ex.Message); }
        }

        private LocalInput GetInput(InteractableTools tool)
        {
            if (!localInputs.TryGetValue(tool, out var input)) localInputs.Add(tool, input = new LocalInput());
            return input;
        }

        private static void StopLocalSounds(InteractableTools tool, LocalInput input)
        {
            try
            {
                if (tool is Tool_Air_TirePump && input.InflateSoundStarted) Singleton<SOUND_Manager>.i.PlaySound_AirTirePump_AirIn_Stop();
                if (input.DeflateSoundStarted) Singleton<SOUND_Manager>.i.PlaySound_TirePump_AirOut_Stop();
            }
            catch { }
            input.InflateSoundStarted = input.DeflateSoundStarted = false;
        }

        private static Part_Driveline_Wheel? ResolveWheel(Vehicle_PartSocket socket) =>
            socket != null && socket.isInstalled && socket.partInstalled != null
                ? socket.partInstalled.GetComponent<Part_Driveline_Wheel>() : null;

        private static TirePressureState Capture(Part_Driveline_Wheel? wheel) =>
            wheel == null || wheel.IntValue1 == 0 ? new TirePressureState(0, 0) :
                new TirePressureState((byte)wheel.IntValue1, Mathf.Clamp(wheel.Value1, -100000f, 100f));

        private static AirCompressorState Capture(AirCompressor compressor) => new AirCompressorState(compressor.Get_AirPressure, compressor.Get_isMotorRunning);

        private static void SetCompressorPressure(AirCompressor compressor, float pressure)
        {
            FieldInfoCache.CompressorPressure.SetValue(compressor, pressure);
            var gauge = FieldInfoCache.CompressorGauge.GetValue(compressor) as NWH.VehiclePhysics2.VehicleGUI.AnalogGauge;
            if (gauge != null) gauge.Value = pressure;
        }

        // Update_Wheels touches every wheel/suspension socket and, for OlTruck, the
        // axle/driveshaft children. A surviving manager is insufficient during scene unload.
        private static bool CanRestoreWheel(WheelBinding binding)
        {
            if (!binding.OriginalScene.IsValid() || !binding.OriginalScene.isLoaded || binding.Socket == null ||
                binding.Socket.gameobject == null || binding.Socket.gameobject.scene != binding.OriginalScene) return false;
            var wheel = ResolveWheel(binding.Socket);
            if (wheel == null) return false;
            var manager = binding.Socket.vehicleAttached;
            if (manager == null) return false;
            var vehicle = manager.GetComponent<VehicleController>();
            if (vehicle == null || vehicle.powertrain.wheels.Any(value => value.wheelController == null)) return false;
            var sockets = new[] { manager.WheelFL, manager.WheelFR, manager.WheelRL, manager.WheelRR,
                manager.Shock_FL, manager.Shock_FR, manager.Shock_RL, manager.Shock_RR };
            if (sockets.Any(socket => socket == null || socket.gameobject == null ||
                socket.boltHolesDetails != null && socket.boltHolesDetails.Any(hole => hole != null && hole.HoleLocation == null))) return false;
            if (sockets.Take(4).Any(socket => socket.boltHolesDetails != null &&
                socket.boltHolesDetails.Any(hole => hole != null && hole.generated_BoltHole == null))) return false;
            if (binding.VehicleType == Vehicle_Type.OlTruck)
            {
                foreach (string name in new[] { "OlTruck_Axle_Front", "OlTruck_Axle_Rear", "OlTruck_Driveshaft_Front",
                    "OlTruck_Driveshaft_Rear", "OlTruck_TransfertCase_FrontConnection", "OlTruck_TransfertCase_RearConnection" })
                    if (!(AccessTools.Field(typeof(Vehicle_PartsManager), name).GetValue(manager) is Transform child) || child == null || child.parent == null) return false;
                foreach (var socket in sockets.Skip(4))
                    if (socket.isInstalled && socket.partInstalled != null)
                    {
                        var shock = socket.partInstalled.GetComponent<Part_Shock>();
                        if (shock != null && (shock.AxleLocation == null || shock.Axle20mmBoltLocation == null)) return false;
                    }
            }
            if (wheel.IntValue1 != binding.Original.TireType)
                for (int i = 1; i <= 6; i++)
                    if (!(AccessTools.Field(typeof(Part_Driveline_Wheel), "Tires" + i).GetValue(wheel) is GameObject tire) || tire == null) return false;
            return true;
        }

        private static bool CanRestoreOriginalWheel(WheelBinding binding) =>
            binding.OriginalCaptured && CanRestoreWheel(binding);

        private static bool OriginalCompressorSceneAlive(CompressorBinding binding) => binding.Native != null &&
            binding.OriginalScene.IsValid() && binding.OriginalScene.isLoaded && binding.Native.gameObject.scene == binding.OriginalScene;

        private static bool CanRestartOriginalCompressor(CompressorBinding binding)
        {
            if (!OriginalCompressorSceneAlive(binding) || !CompressorSoundReady(binding.Native, out var sound) ||
                FieldInfoCache.CompressorNativeSound.GetValue(binding.Native) as SOUND_AirCompressor != sound ||
                !(FieldInfoCache.CompressorFlywheel.GetValue(binding.Native) is Transform flywheel) || flywheel == null ||
                !(FieldInfoCache.CompressorGauge.GetValue(binding.Native) is UnityEngine.Object gauge) || gauge == null) return false;
            return !(bool)FieldInfoCache.CompressorNeedsPower.GetValue(binding.Native) || binding.Native.Get_HomeGenerator != null;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            var failures = new List<Exception>();
            void Attempt(Action action) { try { action(); } catch (Exception ex) { failures.Add(ex); } }
            try
            {
                if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
                if (channel != null) { channel.CommandRequested -= Command; channel.Changed -= Changed; channel.SnapshotApplied -= Snapshot; }
                parts.PurchasedBaselineCapturing -= CapturePurchasedWheelBaseline;
                if (!host)
                {
                    foreach (var binding in wheels.Values)
                        Attempt(() => { if (CanRestoreOriginalWheel(binding)) Apply(binding, binding.Original); });
                    foreach (var binding in compressors.Values)
                    {
                        if (!OriginalCompressorSceneAlive(binding)) continue;
                        Attempt(() =>
                        {
                            binding.Current = binding.Original; Apply(binding, binding.Original);
                            if (FieldInfoCache.CompressorFlywheel.GetValue(binding.Native) is Transform flywheel && flywheel != null)
                                flywheel.localRotation = binding.OriginalFlywheel;
                        });
                    }
                }
            }
            finally
            {
                foreach (var pair in localInputs) Attempt(() => StopLocalSounds(pair.Key, pair.Value));
                Attempt(TirePumpPresentation.Reset);
                if (!host)
                    foreach (var binding in compressors.Values)
                        if (binding.Native != null) Attempt(() =>
                        {
                            StopCompressorSound(binding.Native);
                            FieldInfoCache.CompressorMotor.SetValue(binding.Native, false);
                            if (binding.Original.MotorRunning && CanRestartOriginalCompressor(binding)) AccessTools.Method(typeof(AirCompressor), "StartMotor").Invoke(binding.Native, null);
                        });
                localInputs.Clear(); presence.Clear(); budgets.Clear(); airUseLeases.Clear(); claims.Clear();
                if (Current == this) Current = null;
                SceneAirHolderAllowsUse = null; SceneAirHolderGeneration = null;
                session = null; channel = null;
            }
            if (failures.Count != 0) throw new AggregateException("Tire pressure native restore failed.", failures);
        }

        private static class FieldInfoCache
        {
            internal static readonly System.Reflection.FieldInfo ManualCamera = AccessTools.Field(typeof(Tool_TirePump), "mainCamera") ??
                throw new InvalidOperationException("Unsupported native Tool_TirePump camera field.");
            internal static readonly System.Reflection.FieldInfo AirCamera = AccessTools.Field(typeof(Tool_Air_TirePump), "mainCamera") ??
                throw new InvalidOperationException("Unsupported native Tool_Air_TirePump camera field.");
            internal static readonly System.Reflection.FieldInfo AirPumpCompressor = AccessTools.Field(typeof(Tool_Air_TirePump), "airCompressor") ??
                throw new InvalidOperationException("Unsupported native Tool_Air_TirePump compressor field.");
            internal static readonly System.Reflection.FieldInfo CompressorPressure = AccessTools.Field(typeof(AirCompressor), "_AirPressure") ??
                throw new InvalidOperationException("Unsupported native AirCompressor pressure field.");
            internal static readonly System.Reflection.FieldInfo CompressorGauge = AccessTools.Field(typeof(AirCompressor), "pressureGauge") ??
                throw new InvalidOperationException("Unsupported native AirCompressor gauge field.");
            internal static readonly System.Reflection.FieldInfo CompressorNativeSound = AccessTools.Field(typeof(AirCompressor), "SOUND_AirCompressor");
            internal static readonly System.Reflection.FieldInfo CompressorNeedsPower = AccessTools.Field(typeof(AirCompressor), "NeedElectricity");
            internal static readonly System.Reflection.FieldInfo CompressorMotor = AccessTools.Field(typeof(AirCompressor), "isMotorRunning");
            internal static readonly System.Reflection.FieldInfo CompressorFlywheel = AccessTools.Field(typeof(AirCompressor), "EngineFlywheel");
            internal static readonly System.Reflection.FieldInfo CompressorMotorSource = AccessTools.Field(typeof(SOUND_AirCompressor), "AirCompressorMotor");
            internal static readonly System.Reflection.FieldInfo ManualInflate = AccessTools.Field(typeof(Tool_TirePump), "isMouseLeftClickDown");
            internal static readonly System.Reflection.FieldInfo ManualDeflate = AccessTools.Field(typeof(Tool_TirePump), "isMouseRightClickDown");
            internal static readonly System.Reflection.FieldInfo AirInflate = AccessTools.Field(typeof(Tool_Air_TirePump), "isMouseLeftClickDown");
            internal static readonly System.Reflection.FieldInfo AirDeflate = AccessTools.Field(typeof(Tool_Air_TirePump), "isMouseRightClickDown");
            internal static void Validate()
            {
                _ = ManualCamera; _ = AirCamera; _ = AirPumpCompressor; _ = CompressorPressure; _ = CompressorGauge;
                void Require(System.Reflection.FieldInfo field, Type type)
                { if (field == null || field.IsStatic || field.FieldType != type) throw new InvalidOperationException("Unsupported native tire presentation field: " + field?.Name); }
                Require(CompressorNativeSound, typeof(SOUND_AirCompressor)); Require(CompressorNeedsPower, typeof(bool));
                Require(CompressorMotor, typeof(bool)); Require(CompressorFlywheel, typeof(Transform));
                Require(CompressorMotorSource, typeof(AudioSource));
                Require(ManualInflate, typeof(bool)); Require(ManualDeflate, typeof(bool));
                Require(AirInflate, typeof(bool)); Require(AirDeflate, typeof(bool));
            }
        }

#if MONMP_DEVELOPMENT
        internal uint TestAirGeneration(uint peer, ulong id) => AirGeneration(peer, id);
#endif
    }
}
