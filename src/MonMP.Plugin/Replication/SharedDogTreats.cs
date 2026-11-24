using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Leo;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Dog treats are host transactions: guests submit a leased-tool request and a
    // camera ray; only the host may execute Leo's native friendship effect.
    internal sealed class SharedDogTreats : IDisposable
    {
        private readonly bool host;
        private readonly Gameplay gameplay;
        private readonly SharedItems items;
        private readonly Leo_Manager? leo;
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Dictionary<Guid, (ulong Tool, uint Generation, float Time)> pending = new Dictionary<Guid, (ulong, uint, float)>();
        private readonly LeoTreatClaims claims = new LeoTreatClaims();
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool disposed;

        internal const float FreshPoseSeconds = 1f;

#if MONMP_DEVELOPMENT
        internal int TestAccepted { get; private set; }


        internal int TestRejected { get; private set; }


        internal int TestDuplicates { get; private set; }


        internal LeoTreatResult? TestLastResult { get; private set; }
#endif

        internal int PendingCount => pending.Count;
        internal bool NewActionsBlocked { get; set; }
        internal bool TransactionsSettled => pending.Count == 0;

        private readonly AimSegmentTest checkSegment;

        internal SharedDogTreats(AimSegmentTest checkSegment, Gameplay gameplay, bool host, SharedItems items)
        {
            this.checkSegment = checkSegment;
            this.gameplay = gameplay ?? throw new ArgumentNullException(nameof(gameplay));
            this.host = host;
            this.items = items ?? throw new ArgumentNullException(nameof(items));
            var candidates = UnityEngine.Object.FindObjectsOfType<Leo_Manager>(true)
                .Where(candidate => candidate != null && candidate.gameObject.scene.IsValid()).ToArray();
            if (candidates.Length == 1 && gameplay.Get_Leo_Manager == candidates[0]) leo = candidates[0];

            if (AccessTools.Method(typeof(Leo_Manager), nameof(Leo_Manager.TreatGiven), new[] { typeof(Tool_DogTreats) }) == null ||
                AccessTools.Method(typeof(Tool_DogTreats), nameof(Tool_DogTreats.MouseDown)) == null ||
                AccessTools.Property(typeof(Leo_Manager), nameof(Leo_Manager.Get_IsTamed))?.PropertyType != typeof(bool) ||
                AccessTools.Property(typeof(Leo_Manager), nameof(Leo_Manager.Get_Leo_State))?.PropertyType != typeof(Leo_State))
                throw new InvalidOperationException("Unsupported native Leo dog-treat layout.");
        }

        internal IEnumerable<WorldRecord> InitialRecords()
        {
            // Empty payload: this record exists only as an immutable command route.
            yield return new WorldRecord(LeoTreatCommand.Entity, 1, LeoTreatCommand.Adapter, Array.Empty<byte>());
        }

        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += PoseReceived; session.PeerLeft += PeerLeft;
            channel.CommandRequested += Command; channel.CommandRejected += Rejected;
            channel.ReplyReceived += Reply;
        }

        private bool GuestRunning => !host && !disposed && session?.IsRunning == true;
        private bool Ready => !disposed && session?.IsRunning == true && channel?.FullySynchronized == true;

        internal bool Intercept(Tool_DogTreats tool)
        {
            if (!GuestRunning) return false;
            if (tool == null || !Ready || NewActionsBlocked || MonMPPlugin.Instance?.World?.SaveGate.Active == true || pending.Count >= 64 ||
                !items.TryGetLocalToolLease(tool, out ulong toolId, out uint generation))
            {
                ShowFailure(LeoTreatResult.InvalidToolLease);
                return true;
            }
            if (tool.Value1 <= 0)
            {
                ShowFailure(LeoTreatResult.InsufficientFood);
                return true;
            }
            var camera = gameplay.Player.mainCamera;
            var localLeo = gameplay.Get_Leo_Manager;
            var aim = camera != null ? Integration.LocalInteraction.SampleTool(camera) : default;
            if (camera == null || localLeo == null || !TraceHitsLeo(aim.Ray, localLeo))
            {
                ShowFailure(LeoTreatResult.TargetNotLeo);
                return true;
            }

            var command = new LeoTreatCommand(Guid.NewGuid(), toolId, generation,
                aim.Origin.x, aim.Origin.y, aim.Origin.z, aim.Yaw, aim.Pitch, aim.Independent);
            pending.Add(command.Operation, (toolId, generation, Time.unscaledTime));
            try { channel!.Request(LeoTreatCommand.Entity, command.Encode(), useLease: true); }
            catch
            {
                pending.Remove(command.Operation);
                throw;
            }
            return true;
        }

        private static bool TraceHitsLeo(Ray ray, Leo_Manager target)
        {
            return Physics.Raycast(ray, out var hit, LeoTreatCommand.Reach) &&
                ReferenceEquals(hit.collider.GetComponent<Leo_Manager>(), target);
        }

        private void PoseReceived(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);

        private void PeerLeft(uint peer)
        {
            // Keep replay claims for this entire session, even if a peer leaves and
            // rejoins under the same id. They reset when the adapter is disposed.
            presence.Remove(peer);
        }

        [MonMP.Core.World.WorldCommandAdapters(LeoTreatCommand.Adapter)]

        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (!host || entity != LeoTreatCommand.Entity || peer == 0 || channel == null || session?.Peers.ContainsKey(peer) != true ||
                payload == null || payload.Length == 0) return;
            if (!LeoTreatCommand.HasMarker(payload)) return;
            if (!LeoTreatCommand.Is(payload))
            {
                Debug.LogWarning("MonMP malformed Leo treat command.");
                return;
            }

            LeoTreatCommand command;
            try { command = LeoTreatCommand.Decode(payload); }
            catch (InvalidDataException ex) { Debug.LogWarning("MonMP invalid Leo treat request: " + ex.Message); return; }

            var claim = claims.Claim(peer, command.Operation);
            if (claim != LeoTreatClaimResult.Claimed)
            {
                var duplicate = claim == LeoTreatClaimResult.Duplicate ? LeoTreatResult.Duplicate : LeoTreatResult.Capacity;
                if (duplicate == LeoTreatResult.Duplicate)
#if MONMP_DEVELOPMENT
TestDuplicates++;
#else
{}
#endif

                ReplyTo(peer, command.Operation, duplicate);
                return;
            }

            LeoTreatResult result = ApplyHostRequest(peer, command);

#if MONMP_DEVELOPMENT
            TestLastResult = result;
#endif

            if (result == LeoTreatResult.Applied)
#if MONMP_DEVELOPMENT
TestAccepted++;
#else
{}
#endif
 else
#if MONMP_DEVELOPMENT
TestRejected++;
#else
{}
#endif

            ReplyTo(peer, command.Operation, result);
        }

        private LeoTreatResult ApplyHostRequest(uint peer, LeoTreatCommand command)
        {
            if (NewActionsBlocked || MonMPPlugin.Instance?.World?.SaveGate.Active == true || !Ready)
                return LeoTreatResult.LeoUnavailable;
            var hostLeo = leo;
            if (hostLeo == null || !hostLeo.gameObject.activeInHierarchy || !hostLeo.isActiveAndEnabled)
                return LeoTreatResult.LeoUnavailable;

            bool lease = items.HostHasLeasedTool(peer, command.Tool, command.Generation, typeof(Tool_DogTreats));
            var tool = lease ? items.ItemOf(command.Tool) as Tool_DogTreats : null;
            if (!lease || tool == null || !tool.gameObject.activeInHierarchy)
                return LeoTreatResult.InvalidToolLease;

            bool fresh = presence.TryGetValue(peer, out var seen) &&
                Time.unscaledTime - seen.Time >= 0 && Time.unscaledTime - seen.Time <= FreshPoseSeconds;
            PlayerPose pose = fresh ? seen.Pose : default;
            pose = pose.WithIndependentAim(command.IndependentAim);
            bool originFits = fresh && ToolAimPolicy.CameraOriginFits(pose, command.CameraX, command.CameraY, command.CameraZ, clear: checkSegment);
            bool aimFits = fresh && ToolAimPolicy.CameraAimFits(pose, command.CameraYaw, command.CameraPitch);
            bool rayHits = originFits && aimFits && HostRayHitsLeo(command);

            bool[]? giftedToday = GameManager.Get_Friendship_SyrupGiftedToday;
            if (giftedToday == null || giftedToday.Length <= LeoTreatPolicy.LeoActorIndex)
                return LeoTreatResult.LeoUnavailable;
            bool isTamed = hostLeo.Get_IsTamed;
            byte state = isTamed ? (byte)hostLeo.Get_Leo_State : byte.MaxValue;
            LeoTreatResult decision = LeoTreatPolicy.ValidateHostRequest(lease, fresh,
                fresh ? pose.State : byte.MaxValue, originFits, aimFits, rayHits,
                hostLeo.isActiveAndEnabled && hostLeo.gameObject.activeInHierarchy, isTamed,
                state, giftedToday[LeoTreatPolicy.LeoActorIndex], tool.Value1);
            if (decision != LeoTreatResult.Applied) return decision;

            bool nativeApplied = false;
            bool committed = items.HostMutateLeasedTool(peer, command.Tool, command.Generation, typeof(Tool_DogTreats), nativeTool =>
            {
                if (nativeTool is not Tool_DogTreats hostTool || !ReferenceEquals(hostTool, tool) || hostTool.Value1 <= 0 ||
                    giftedToday[LeoTreatPolicy.LeoActorIndex] || !hostLeo.Get_IsTamed || !hostLeo.isActiveAndEnabled ||
                    !hostLeo.gameObject.activeInHierarchy ||
                    !LeoTreatPolicy.IsSupportedTamedState((byte)hostLeo.Get_Leo_State)) return false;

                int previousFriendship = GameManager.Get_Friendship[LeoTreatPolicy.LeoActorIndex];
                bool previousGiftFlag = giftedToday[LeoTreatPolicy.LeoActorIndex];
                SharedLeo.TreatGiver = peer;
                try { hostLeo.TreatGiven(hostTool); }
                catch (Exception ex)
                {
                    // Roll back the persistent effect if an unrelated native UI/audio
                    // dependency throws partway through the state-machine method.
                    GameManager.Get_Friendship[LeoTreatPolicy.LeoActorIndex] = previousFriendship;
                    giftedToday[LeoTreatPolicy.LeoActorIndex] = previousGiftFlag;
                    Debug.LogError("MonMP native Leo treat effect failed: " + ex);
                    return false;
                }
                finally { SharedLeo.TreatGiver = 0; }
                if (!giftedToday[LeoTreatPolicy.LeoActorIndex]) return false;
                hostTool.Value1 = LeoTreatPolicy.FoodAfterUse(hostTool.Value1);
                nativeApplied = true;
                return true;
            }, destroyWhenEmpty: true);

            return committed && nativeApplied ? LeoTreatResult.Applied : LeoTreatResult.NativeEffectFailed;
        }

        private bool HostRayHitsLeo(LeoTreatCommand command)
        {
            var origin = new Vector3(command.CameraX, command.CameraY, command.CameraZ);
            var direction = Quaternion.Euler(command.CameraPitch, command.CameraYaw, 0f) * Vector3.forward;
            var ray = new Ray(origin, direction);
            return Physics.Raycast(ray, out var hit, LeoTreatCommand.Reach) &&
                ReferenceEquals(hit.collider.GetComponent<Leo_Manager>(), leo);
        }

        private void ReplyTo(uint peer, Guid operation, LeoTreatResult result)
        {
            if (session?.Peers.ContainsKey(peer) == true)
                channel?.Reply(peer, LeoTreatCommand.Entity, new LeoTreatReply(operation, result).Encode());
        }

        private void Reply(ulong entity, byte[] payload)
        {
            if (host || entity != LeoTreatCommand.Entity) return;
            LeoTreatReply reply;
            try { reply = LeoTreatReply.Decode(payload); }
            catch (InvalidDataException ex) { Debug.LogWarning("MonMP invalid Leo treat reply: " + ex.Message); return; }
            if (!pending.Remove(reply.Operation)) return;

#if MONMP_DEVELOPMENT
            TestLastResult = reply.Result;
#endif

            if (reply.Result == LeoTreatResult.Applied)
            {
                // The giver's half of the native effect; the heart itself is shared (SharedLeoHearts).
                try
                {
                    var local = gameplay.Get_Leo_Manager;
                    local?.SOUND_Leo.Play_LeoEatTreat();
                    local?.uI_FrontLeftPopup.Friendship(Actor_Type.Leo);
                }
                catch (Exception ex) { Debug.LogWarning("MonMP Leo treat feedback failed: " + ex.Message); }
            }
            else if (reply.Result != LeoTreatResult.Duplicate)
                ShowFailure(reply.Result);
        }

        private void Rejected(uint peer, ulong entity, byte[] payload)
        {
            if (host || peer != session?.LocalPeerId || entity != LeoTreatCommand.Entity || !LeoTreatCommand.Is(payload)) return;
            try
            {
                var request = LeoTreatCommand.Decode(payload);
                if (pending.Remove(request.Operation)) ShowFailure(LeoTreatResult.StalePlayerPose);
            }
            catch (InvalidDataException) { }
        }

        internal void Update()
        {
            if (disposed || host) return;
            float now = Time.unscaledTime;
            if (pending.Count > 0) foreach (var request in pending.Where(pair => now - pair.Value.Time > 5f).ToArray())
            {
                pending.Remove(request.Key);
                ShowFailure(LeoTreatResult.StalePlayerPose);
            }
        }

        private static void ShowFailure(LeoTreatResult result)
        {
            if (result != LeoTreatResult.AlreadyGivenToday) return;
            try { Singleton<UI_ErrorMessage>.i.Show(TEXT.GetText("Leo_TreatAlreadyGivenToday")); }
            catch { /* Keep network authority independent of transient UI availability. */ }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (channel != null)
            {
                channel.CommandRequested -= Command; channel.CommandRejected -= Rejected; channel.ReplyReceived -= Reply;
            }
            if (session != null)
            {
                session.PoseReceived -= PoseReceived; session.PeerLeft -= PeerLeft;
            }
            pending.Clear(); presence.Clear(); claims.Clear();
            session = null; channel = null;
        }
    }
}
