using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed class SharedSceneAirEquipment : IDisposable
    {
        private sealed class Binding
        {
            internal ulong Id;
            internal Tool_Air_TirePump Tool = null!;
            internal AirCompressor Compressor = null!;
            internal SceneAirEquipmentLease Lease = new SceneAirEquipmentLease();
            internal SceneAirEquipmentState State = new SceneAirEquipmentState(ItemLease.Unowned, 1);
            internal float Expires;
            internal Vector3 OriginalGunPosition;
            internal string Shape = "";
        }
        private readonly Gameplay gameplay;
        private readonly bool host;
        private readonly SharedTirePressure pressure;
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Dictionary<uint, RemoteSceneAirEquipmentVisual> visuals = new Dictionary<uint, RemoteSceneAirEquipmentVisual>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private float nextSend;
        private bool disposed, blocked;
        private float localClaimStarted;
        private readonly Vector3 equipPosition;
        private readonly Quaternion equipRotation, playerFromCameraRotation;
        private readonly Vector3 playerFromCameraPosition;
        private ulong localRequested;
        private readonly Func<uint, ulong, uint, bool> holderAllowsUse;
        private readonly Func<uint, ulong, uint> holderGeneration;
        private static readonly System.Reflection.FieldInfo Locations = AccessTools.Field(typeof(PlayerInteractable), "toolLocations") ??
            throw new InvalidOperationException("Unsupported native tool-location field.");
        private static readonly System.Reflection.MethodInfo UnEquip = AccessTools.Method(typeof(PlayerInteractable), "UnEquipTool", Type.EmptyTypes) ??
            throw new InvalidOperationException("Unsupported native tool unequip method.");

        internal SharedSceneAirEquipment(Gameplay gameplay, bool host, SharedTirePressure pressure, NativeEntityRegistry identities)
        {
            this.gameplay = gameplay; this.host = host; this.pressure = pressure;
            holderAllowsUse = HolderAllowsUse;
            holderGeneration = HolderGeneration;
            var player = gameplay.Player;
            var location = (Locations.GetValue(player) as InteractableToolLocation[])?
                .FirstOrDefault(value => value != null && value.toolsType == ToolsType.Air_TirePump && value.Location != null);
            if (location == null) throw new InvalidOperationException("Native air gun has no serialized equip location.");
            equipPosition = location.Location.localPosition; equipRotation = location.Location.localRotation;
            var camera = player.mainCamera.transform;
            playerFromCameraPosition = camera.InverseTransformPoint(player.transform.position);
            playerFromCameraRotation = Quaternion.Inverse(camera.rotation) * player.transform.rotation;
            foreach (ulong id in pressure.SceneAirToolIds)
            {
                if (!pressure.TryGetSceneAirTool(id, out var tool, out var compressor)) continue;
                HeldGripOverrides.RegisterSceneEquipment(tool, out _, out string shape);
                bindings.Add(id, new Binding { Id = id, Tool = tool, Compressor = compressor,
                    OriginalGunPosition = tool.transform.position, Shape = shape });
                identities.Bind(id, SceneAirEquipmentState.Adapter, tool.gameObject,
                    "monmp/scene-air-equipment/v1/" + id.ToString("X16"), true, EntityPersistence.SessionOnly);
            }
            pressure.SceneAirHolderAllowsUse = holderAllowsUse;
            pressure.SceneAirHolderGeneration = holderGeneration;
        }
        internal IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(binding =>
            new WorldRecord(binding.Id, 1, SceneAirEquipmentState.Adapter, binding.State.Encode()));
        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.CommandRequested += Command; channel.Changed += Changed; channel.SnapshotApplied += Snapshot;
        }
        internal void SetNewActionsBlocked(bool value) => blocked = value;
        internal uint[] Owners => bindings.Values.Select(binding => binding.State.Owner)
            .Where(owner => owner != ItemLease.Unowned).Distinct().ToArray();
        internal bool LocalFlushComplete => session != null && !Owners.Contains(session.LocalPeerId) &&
            !bindings.Values.Any(binding => binding.Tool != null && gameplay.Player.Get_ActiveTool == binding.Tool);
        internal void FlushLocalOwnership()
        {
            if (disposed || channel?.Ready != true || session == null) return;
            foreach (var binding in bindings.Values)
            {
                CancelLocal(binding);
                if (binding.State.Owner == session.LocalPeerId) Send(binding, false);
            }
            localRequested = 0;
        }
        internal void RevokeOwnership(uint peer)
        {
            if (!host) throw new InvalidOperationException("Only the host can revoke scene air equipment.");
            foreach (var binding in bindings.Values.Where(binding => binding.State.Owner == peer))
            {
                if (session?.LocalPeerId == peer) CancelLocal(binding);
                Revoke(binding);
            }
        }
        internal bool AllowNativeEquip(InteractableTools tool)
        {
            if (disposed || session?.IsRunning != true || tool is not Tool_Air_TirePump) return true;
            var binding = bindings.Values.FirstOrDefault(value => value.Tool == tool);
            return binding == null || (!blocked && (binding.State.Owner == ItemLease.Unowned || binding.State.Owner == session.LocalPeerId));
        }
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer)
        {
            presence.Remove(peer);
            if (visuals.TryGetValue(peer, out var visual)) { visual.Dispose(); visuals.Remove(peer); }
            if (host) foreach (var binding in bindings.Values.Where(value => value.State.Owner == peer)) Revoke(binding);
        }
        private bool ValidHolder(uint peer, Binding binding)
        {
            if (binding.Tool == null || binding.Compressor == null || !binding.Tool.gameObject.activeInHierarchy) return false;
            Vector3 position;
            if (session != null && peer == session.LocalPeerId)
            {
                if (gameplay.Get_IsDead || gameplay.Get_IsSleeping || MonMPPlugin.Instance?.Spectating == true ||
                    !ItemInteractionPosePolicy.AllowsState((byte)gameplay.Get_PlayerState) ||
                    gameplay.Player.Get_ActiveTool != binding.Tool || !binding.Tool.isEquiped) return false;
                position = gameplay.PlayerWalking.transform.position;
            }
            else
            {
                if (session?.Peers.ContainsKey(peer) != true || !presence.TryGetValue(peer, out var seen) ||
                    Time.unscaledTime - seen.Time > 2f || !ItemInteractionPosePolicy.Allows(seen.Pose) ||
                    (seen.Pose.Hands & PlayerHands.Tool) == 0) return false;
                position = new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z);
            }
            return (position - binding.Compressor.transform.position).sqrMagnitude <= 20f * 20f;
        }
        [MonMP.Core.World.WorldCommandAdapters(SceneAirEquipmentState.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || channel?.Ready != true || !bindings.TryGetValue(id, out var binding)) return;
            SceneAirEquipmentCommand command;
            try { command = SceneAirEquipmentCommand.Decode(payload); } catch (System.IO.InvalidDataException) { return; }
            if (blocked && command.Equipped && binding.State.Owner != peer) return;
            bool valid = !command.Equipped || ValidHolder(peer, binding);
            if (command.Equipped && peer != 0 && binding.State.Owner == ItemLease.Unowned)
            {
                if (!presence.TryGetValue(peer, out var seen)) return;
                var at = new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z);
                // Equipping begins at the catalogued hanger, not from the far end
                // of the 20m hose. A normal leased tool also excludes this claim.
                if ((at - binding.OriginalGunPosition).sqrMagnitude > 4f * 4f ||
                    MonMPPlugin.Instance?.Items?.HeldItemOf(peer) != null) return;
            }
            // A peer can equip one catalogued gun at a time; a competing owner cannot
            // displace another peer by selecting a different scene instance.
            if (command.Equipped && bindings.Values.Any(value => value != binding && value.State.Owner == peer)) return;
            uint before = binding.Lease.State.Generation;
            if (!binding.Lease.Apply(peer, command, valid, valid)) return;
            binding.Expires = command.Equipped ? Time.unscaledTime + 2f : 0;
            SetState(binding, binding.Lease.State);
            if (binding.State.Generation != before) channel.Publish(binding.Id, binding.State.Encode());
        }
        private void Revoke(Binding binding)
        {
            binding.Lease.Revoke(); SetState(binding, binding.Lease.State); binding.Expires = 0;
            if (channel?.Ready == true) channel.Publish(binding.Id, binding.State.Encode());
        }
        private void Changed(WorldRecord record)
        {
            if (host || record.Adapter != SceneAirEquipmentState.Adapter || !bindings.TryGetValue(record.Id, out var binding)) return;
            SetState(binding, SceneAirEquipmentState.Decode(record.Payload));
        }
        private void SetState(Binding binding, SceneAirEquipmentState state)
        {
            // Restore the old actor's source flags before the new actor can copy
            // them, including when network dispatch precedes RemotePlayers.Update.
            if (binding.State.Owner != state.Owner && binding.State.Owner != ItemLease.Unowned)
                ResetPresentation(binding.State.Owner);
            binding.State = state;
        }
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        { if (!host) foreach (var record in records.Values) Changed(record); }
        private uint HolderGeneration(uint peer, ulong toolId) => bindings.TryGetValue(toolId, out var binding) &&
            binding.State.Owner == peer && ValidHolder(peer, binding) ? binding.State.Generation : 0;
        private bool HolderAllowsUse(uint peer, ulong toolId, uint generation) => generation != 0 && HolderGeneration(peer, toolId) == generation;

        internal void Update()
        {
            if (disposed || channel?.FullySynchronized != true || session == null) return;
            if (host)
                foreach (var binding in bindings.Values)
                    if (binding.State.Owner != ItemLease.Unowned &&
                        (Time.unscaledTime > binding.Expires || !ValidHolder(binding.State.Owner, binding))) Revoke(binding);
            foreach (var pair in visuals)
                if (BindingForOwner(pair.Key) == null ||
                    !presence.TryGetValue(pair.Key, out var seen) || Time.unscaledTime - seen.Time > 2f ||
                    !ItemInteractionPosePolicy.Allows(seen.Pose) || (seen.Pose.Hands & PlayerHands.Tool) == 0) pair.Value.Reset();
            if (Time.unscaledTime < nextSend) return;
            nextSend = Time.unscaledTime + 0.5f;
            ulong equipped = bindings.Values.FirstOrDefault(value => value.Tool != null && value.Tool.isEquiped &&
                value.Tool == gameplay.Player.Get_ActiveTool)?.Id ?? 0;
            if (equipped != localRequested) localClaimStarted = Time.unscaledTime;
            if (equipped != 0)
            {
                var binding = bindings[equipped];
                bool conflict = binding.State.Owner != ItemLease.Unowned && binding.State.Owner != session.LocalPeerId;
                bool expiredClaim = binding.State.Owner != session.LocalPeerId && Time.unscaledTime - localClaimStarted > 2f;
                if (gameplay.Get_IsDead || gameplay.Get_IsSleeping || MonMPPlugin.Instance?.Spectating == true ||
                    !ItemInteractionPosePolicy.AllowsState((byte)gameplay.Get_PlayerState) ||
                    conflict || expiredClaim || blocked)
                { CancelLocal(binding); equipped = 0; }
            }
            if (localRequested != 0 && localRequested != equipped && bindings.TryGetValue(localRequested, out var old) && old.State.Owner == session.LocalPeerId)
                Send(old, false);
            localRequested = equipped;
            if (equipped != 0) Send(bindings[equipped], true);
        }
        private void Send(Binding binding, bool equipped)
        {
            var payload = new SceneAirEquipmentCommand(equipped, binding.State.Generation).Encode();
            if (host) Command(session!.LocalPeerId, binding.Id, payload);
            else channel!.Request(binding.Id, payload, true);
        }
        private void CancelLocal(Binding binding)
        {
            if (gameplay != null && gameplay.Player != null && binding.Tool != null && gameplay.Player.Get_ActiveTool == binding.Tool)
                UnEquip.Invoke(gameplay.Player, null);
        }

        internal bool TryPresent(uint peer, Vector3 walkerPosition, Quaternion cameraRotation, float crouch,
            out Vector3 palm, out Quaternion palmRotation, out Vector3? leftPalm, out Quaternion? leftPalmRotation,
            out float heldWidth, Vector3? eyePosition = null, Quaternion? bodyRotation = null)
        {
            palm = default; palmRotation = default;
            leftPalm = null; leftPalmRotation = null; heldWidth = 0;
            var binding = BindingForOwner(peer);
            if (disposed || channel?.FullySynchronized != true || session == null || peer == session.LocalPeerId || binding == null ||
                binding.Tool == null || gameplay.Player.Get_ActiveTool == binding.Tool || binding.Tool.isEquiped ||
                !presence.TryGetValue(peer, out var seen) || Time.unscaledTime - seen.Time > 2f ||
                !ItemInteractionPosePolicy.Allows(seen.Pose) || (seen.Pose.Hands & PlayerHands.Tool) == 0)
            { ResetPresentation(peer); return false; }
            if (!visuals.TryGetValue(peer, out var visual)) visuals[peer] = visual = new RemoteSceneAirEquipmentVisual();
            Vector3 eyes = eyePosition ?? walkerPosition + Vector3.up * (Mathf.Lerp(1.6f, 1.05f, crouch) -
                (seen.Pose.State == ItemInteractionPosePolicy.CreeperState ? 1.32f : 0));
            Quaternion playerRotation = cameraRotation * playerFromCameraRotation;
            Vector3 playerPosition = eyes + cameraRotation * playerFromCameraPosition;
            Vector3 at = playerPosition + playerRotation * equipPosition;
            Quaternion turn = playerRotation * equipRotation;
            var grips = MonMPPlugin.Instance?.HeldGrips;
            if (grips != null && grips.TryGetVisualOffset(binding.Tool, binding.Shape, out var offset, out var extraTurn))
            { at += (bodyRotation ?? cameraRotation) * offset; turn *= extraTurn; }
            if (!visual.Present(binding.Tool, at, turn)) return false;
            palm = at; palmRotation = turn;
            if (grips != null && grips.TryGetRuntime(binding.Tool, binding.Shape, out var localPalm, out var localTurn, out heldWidth,
                out var localLeft, out var localLeftTurn))
            {
                palm = at + turn * Vector3.Scale(localPalm, binding.Tool.transform.lossyScale); palmRotation = turn * localTurn;
                if (localLeft.HasValue && localLeftTurn.HasValue)
                { leftPalm = at + turn * Vector3.Scale(localLeft.Value, binding.Tool.transform.lossyScale); leftPalmRotation = turn * localLeftTurn.Value; }
            }
            return true;
        }
        internal bool TryPresent(uint peer, Vector3 walkerPosition, Quaternion cameraRotation, float crouch,
            out Vector3 palm, out Quaternion palmRotation, Vector3? eyePosition = null)
            => TryPresent(peer, walkerPosition, cameraRotation, crouch, out palm, out palmRotation, out _, out _, out _, eyePosition);
        private Binding? BindingForOwner(uint peer)
        {
            foreach (var binding in bindings.Values) if (binding.State.Owner == peer) return binding;
            return null;
        }
        internal void ResetPresentation(uint peer) { if (visuals.TryGetValue(peer, out var visual)) visual.Reset(); }
        internal Transform? PresentedTransform(uint peer) =>
            visuals.TryGetValue(peer, out var visual) ? visual.PresentedTransform : null;
        internal void TranslatePresentation(uint peer, Vector3 correction)
        { if (visuals.TryGetValue(peer, out var visual)) visual.Translate(correction); }

#if MONMP_DEVELOPMENT
        internal uint TestHolder(ulong id) => bindings[id].State.Owner;


        internal bool TestPresented(uint peer) => visuals.TryGetValue(peer, out var visual) && visual.Active;


        internal bool TestPresentationOnly(uint peer) => visuals.TryGetValue(peer, out var visual) && visual.PresentationOnly;


        internal bool TestNativeHidden(ulong id)
        {
            var visible = bindings[id].Tool.GetComponentsInChildren<Renderer>(true)
                .Where(renderer => renderer.enabled && renderer.gameObject.activeInHierarchy).ToArray();
            return visible.Length != 0 && visible.All(renderer => renderer.forceRenderingOff);
        }


        internal bool TestNativeFlagsRestored => visuals.Values.All(visual => visual.NativeFlagsRestored);
#endif

        public void Dispose()
        {
            if (disposed) return; disposed = true;
            var failures = new List<Exception>();
            void Attempt(string action, Action run)
            {
                try { run(); }
                catch (Exception ex) { failures.Add(new InvalidOperationException("Scene air equipment cleanup: " + action, ex)); }
            }
            try
            {
                foreach (var binding in bindings.Values)
                    Attempt("native unequip " + binding.Id.ToString("X16"), () => CancelLocal(binding));
            }
            finally
            {
                var endingSession = session;
                var endingChannel = channel;
                if (endingSession != null)
                {
                    Attempt("pose subscription", () => endingSession.PoseReceived -= OnPose);
                    Attempt("departure subscription", () => endingSession.PeerLeft -= OnLeft);
                }
                if (endingChannel != null)
                {
                    Attempt("command subscription", () => endingChannel.CommandRequested -= Command);
                    Attempt("delta subscription", () => endingChannel.Changed -= Changed);
                    Attempt("snapshot subscription", () => endingChannel.SnapshotApplied -= Snapshot);
                }
                Attempt("holder callback", () => { if (pressure.SceneAirHolderAllowsUse == holderAllowsUse) pressure.SceneAirHolderAllowsUse = null; });
                Attempt("generation callback", () => { if (pressure.SceneAirHolderGeneration == holderGeneration) pressure.SceneAirHolderGeneration = null; });
                foreach (var pair in visuals)
                    Attempt("visual " + pair.Key, () => pair.Value.Dispose());
                visuals.Clear(); presence.Clear(); bindings.Clear();
                channel = null; session = null; localRequested = 0;
            }
            if (failures.Count != 0) throw new AggregateException("Scene air equipment cleanup encountered failures.", failures);
        }
    }
}
