using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Session;
using MonMP.Core.World;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // The route controller is authoritative on the host. Guests mirror its transform and
    // activity only; local route ticks and Actor side effects are held while the session runs.
    internal sealed class SharedActorRoutes : IDisposable
    {
        private static readonly FieldInfo ActorField = AccessTools.Field(typeof(AI_Actor_FollowRoute), "actor");
        private static readonly FieldInfo RoutesField = AccessTools.Field(typeof(AI_Actor_FollowRoute), "routes");
        private static readonly FieldInfo RoutePointsField = AccessTools.Field(typeof(AI_Actor_FollowRoute), "RoutePoints");
        private static readonly FieldInfo TargetWaypointField = AccessTools.Field(typeof(AI_Actor_FollowRoute), "targetWayPoint");
        private sealed class Binding
        {
            public ulong Id;
            public string Identity = "";
            public AI_Actor_FollowRoute Route = null!;
            public Actor Actor = null!;
            public List<Transform> Points = null!;
            public int WaypointCount;
            public ActorRouteState Original;
            public ActorRouteState Current;
            public Vector3 PoseFromPosition, PoseToPosition;
            public Quaternion PoseFromRotation, PoseToRotation;
            public float PoseReceivedAt;
            public bool HasPresentationPose;
            public Animator? Animator;
            public bool OriginalAnimatorEnabled, OriginalWalking, OriginalDriving, OriginalSitting, CaptureFailureLogged;
        }

        private readonly bool host;
        private readonly string diagnosticsDirectory;
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<AI_Actor_FollowRoute, Binding> byRoute = new Dictionary<AI_Actor_FollowRoute, Binding>();
        private readonly HashSet<Actor> routeActors = new HashSet<Actor>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private Actor? applyingActor;
        private bool disposed;
        private float nextCapture;
        private int interpolationFrames;
        private const float CaptureSeconds = 0.1f;
        private const float RemotePoseInterpolationSeconds = 0.1f;

        internal int BoundCount => bindings.Count;
        internal bool GuestBlocks => !host && !disposed && session?.IsRunning == true;
        internal bool BlocksGuestInteraction(Actor actor) => GuestBlocks && actor != null && routeActors.Contains(actor);
        internal bool HasBinding(Actor actor) => actor != null && bindings.Values.Any(binding => binding.Actor == actor);
        internal bool FreezesRouteAi(AI_Actor_FollowRoute route) => GuestBlocks && route != null && byRoute.ContainsKey(route);
        internal bool SuppressActorEnable(Actor actor) => !host && actor != null && ReferenceEquals(applyingActor, actor);

#if MONMP_DEVELOPMENT
        internal ulong TestEntity => bindings.Values.Where(binding => binding.Points.Count > 1)
            .OrderBy(binding => binding.Identity, StringComparer.Ordinal).Select(binding => binding.Id).FirstOrDefault();


        internal ActorRouteState? TestState(ulong entity) => bindings.TryGetValue(entity, out var binding) ? binding.Current : (ActorRouteState?)null;


        internal ulong TestEntityForActor(Actor actor) => bindings.Values
            .Where(binding => binding.Actor == actor).Select(binding => binding.Id).FirstOrDefault();


        internal Vector3? TestPosition(ulong entity) => bindings.TryGetValue(entity, out var binding) && binding.Actor != null ? binding.Actor.transform.position : (Vector3?)null;


        internal bool? TestActive(ulong entity) => bindings.TryGetValue(entity, out var binding) && binding.Actor != null ? binding.Actor.gameObject.activeSelf : (bool?)null;


        internal int TestInterpolationFrames => interpolationFrames;
#endif


        internal SharedActorRoutes(bool host, string diagnostics, NativeEntityRegistry registry)
        {
            this.host = host;
            diagnosticsDirectory = diagnostics;
            var candidates = UnityEngine.Object.FindObjectsOfType<AI_Actor_FollowRoute>(true)
                .Where(value => value != null && value.gameObject.scene.IsValid()).ToArray();
            var actorCandidates = new List<(AI_Actor_FollowRoute Route, Actor Actor)>();
            foreach (var route in candidates)
            {
                if (ActorField?.GetValue(route) is Actor actor && actor != null && actor.gameObject.scene.IsValid())
                {
                    routeActors.Add(actor);
                    actorCandidates.Add((route, actor));
                }
            }

            var duplicateActors = new HashSet<Actor>(actorCandidates.GroupBy(value => value.Actor)
                .Where(group => group.Count() != 1).Select(group => group.Key));
            var identities = new IdentityRegistry();
            var lines = new List<string>();
            foreach (var candidate in actorCandidates.OrderBy(value => SafePath(value.Route.transform), StringComparer.Ordinal))
            {
                string routePath = SafePath(candidate.Route.transform);
                string actorPath = SafePath(candidate.Actor.transform);
                string identity = routePath + "/actor-route-v1/" + candidate.Actor.actor_Type + "/" + actorPath;
                string disposition = "bound";
                try
                {
                    if (duplicateActors.Contains(candidate.Actor)) throw new InvalidOperationException("actor referenced by multiple route controllers");
                    if (candidate.Actor.GetComponentInParent<VehicleController>(true) != null) throw new InvalidOperationException("actor is nested in a vehicle");
                    if (!(RoutePointsField?.GetValue(candidate.Route) is List<Transform> points))
                        throw new InvalidOperationException("route point storage is unavailable");
                    // Generated points are populated by Start, which may not have run on
                    // an inactive guest controller. Catalog authored curves on both peers.
                    int waypointCount = RouteWaypointCount(RoutesField?.GetValue(candidate.Route) as Transform[]);
                    ulong id = identities.Register(identity);
                    var animator = candidate.Actor.animator;
                    var binding = new Binding
                    {
                        Id = id, Identity = identity, Route = candidate.Route, Actor = candidate.Actor, Points = points, WaypointCount = waypointCount,
                        Animator = animator, OriginalAnimatorEnabled = animator != null && animator.enabled,
                        OriginalWalking = AnimatorBool(animator, "isWalking"), OriginalDriving = AnimatorBool(animator, "isDriving"),
                        OriginalSitting = AnimatorBool(animator, "isSitting")
                    };
                    binding.Original = binding.Current = Capture(binding);
                    binding.PoseFromPosition = binding.PoseToPosition = Position(binding.Original);
                    binding.PoseFromRotation = binding.PoseToRotation = Rotation(binding.Original);
                    registry.Bind(id, ActorRouteState.Adapter, candidate.Actor.gameObject, identity, true, EntityPersistence.SessionOnly);
                    bindings.Add(id, binding); byRoute.Add(candidate.Route, binding);
                }
                catch (Exception ex) { disposition = "skipped: " + ex.Message; }
                lines.Add(disposition + "\t" + candidate.Actor.actor_Type + "\t" + routePath + "\t" + actorPath + "\t" + identity);
            }

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-actor-routes.tsv"),
                new[] { "status\tactor-type\tcontroller-path\tactor-path\tidentity", "summary\t-\t-\t-\trole=" + (host ? "host" : "guest") + ",controllers=" + candidates.Length + ",bound=" + bindings.Count }
                    .Concat(lines));
#endif

        }

        private static int RouteWaypointCount(Transform[]? routes)
        {
            if (routes == null || routes.Length == 0 || routes.Any(route => route == null || route.childCount < 4))
                throw new InvalidOperationException("authored route curves are unavailable");
            // Match native BuildPoints float accumulation, including its endpoint sample.
            int samples = 0;
            for (float progress = 0f; progress < 1f; progress += 0.05f) samples++;
            return checked(routes.Length * samples);
        }

        internal void NativeRouteStarted(AI_Actor_FollowRoute route)
        {
            if (host || disposed || !byRoute.TryGetValue(route, out var binding)) return;
            var original = binding.Original;
            // Before Start, the serialized actor may still be active. Disconnect must
            // restore the inactive native baseline, preserving its original local pose.
            binding.Original = new ActorRouteState(false, false, false, original.AnimatorEnabled, 0,
                original.X, original.Y, original.Z, original.EulerX, original.EulerY, original.EulerZ);
            if (!GuestBlocks || !binding.HasPresentationPose) return;
            // Native Start must still build its waypoints for later single-player use,
            // but its actor deactivation must not erase an already received host pose.
            Apply(new WorldRecord(binding.Id, 1, ActorRouteState.Adapter, binding.Current.Encode()), true);
        }

        private static string SafePath(Transform transform)
        {
            try { return SharedBuildingDoors.StablePath(transform); }
            catch { return "unavailable/" + transform.name; }
        }

        private static bool AnimatorBool(Animator? animator, string name)
        {
            if (animator == null) return false;
            int hash = Animator.StringToHash(name);
            return animator.parameters.Any(parameter => parameter.nameHash == hash && parameter.type == AnimatorControllerParameterType.Bool) && animator.GetBool(hash);
        }

        internal IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(binding =>
            new WorldRecord(binding.Id, 1, ActorRouteState.Adapter, Capture(binding).Encode()));

        internal void Attach(NetworkSession connected, WorldChannel world)
        {
            session = connected; channel = world;
            world.SnapshotApplied += Snapshot; world.Changed += Changed;
        }

        private static ActorRouteState Capture(Binding binding)
        {
            var position = binding.Actor.transform.position;
            var euler = binding.Actor.transform.eulerAngles;
            return new ActorRouteState(binding.Actor.gameObject.activeSelf, binding.Route.Get_IsWalking,
                binding.Route.Get_IsWalkingBack, binding.Animator != null && binding.Animator.enabled,
                binding.Route.Get_currentWayPoint, position.x, position.y, position.z, euler.x, euler.y, euler.z);
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            foreach (var record in records.Values.Where(record => record.Adapter == ActorRouteState.Adapter)) Apply(record, true);
        }

        private void Changed(WorldRecord record)
        {
            if (!host && record.Adapter == ActorRouteState.Adapter) Apply(record, false);
        }

        private void Apply(WorldRecord record, bool snap)
        {
            if (record.Adapter != ActorRouteState.Adapter || !bindings.TryGetValue(record.Id, out var binding))
                throw new InvalidDataException("Unknown actor route identity.");
            var state = ActorRouteState.Decode(record.Payload);
            if (state.CurrentWaypoint > binding.WaypointCount && state.Walking)
                throw new InvalidDataException("Actor route waypoint is outside the local route.");
            bool activityChanged = binding.Current.Active != state.Active || binding.Current.Walking != state.Walking;
            binding.Current = state;
            if (binding.Actor == null || binding.Route == null) return;
            applyingActor = binding.Actor;
            try
            {
                if (binding.Actor.gameObject.activeSelf != state.Active) binding.Actor.gameObject.SetActive(state.Active);
                if (binding.Animator != null)
                {
                    binding.Animator.enabled = state.AnimatorEnabled;
                    if (state.AnimatorEnabled)
                    {
                        SetAnimatorBool(binding.Animator, "isWalking", state.Walking);
                        SetAnimatorBool(binding.Animator, "isDriving", false);
                        SetAnimatorBool(binding.Animator, "isSitting", false);
                    }
                }
            }
            finally { applyingActor = null; }

            // Snapshot state is a baseline and must appear in the correct place at once.
            // Blend consecutive walking deltas so each 10 Hz host sample does not snap.
            // Lifecycle transitions snap to avoid showing an obsolete pose on activation.
            if (snap || !binding.HasPresentationPose || activityChanged || !state.Active || !state.Walking)
            {
                binding.PoseFromPosition = binding.PoseToPosition = Position(state);
                binding.PoseFromRotation = binding.PoseToRotation = Rotation(state);
                binding.PoseReceivedAt = Time.unscaledTime;
                binding.HasPresentationPose = true;
                Present(binding, 1f);
            }
            else
            {
                binding.PoseFromPosition = binding.Actor.transform.position;
                binding.PoseFromRotation = binding.Actor.transform.rotation;
                binding.PoseToPosition = Position(state);
                binding.PoseToRotation = Rotation(state);
                binding.PoseReceivedAt = Time.unscaledTime;
                binding.HasPresentationPose = true;
            }
        }

        private static void SetAnimatorBool(Animator animator, string name, bool value)
        {
            int hash = Animator.StringToHash(name);
            if (animator.parameters.Any(parameter => parameter.nameHash == hash && parameter.type == AnimatorControllerParameterType.Bool))
                animator.SetBool(hash, value);
        }

        internal void Update()
        {
            if (disposed) return;
            if (!host && GuestBlocks)
            {
                foreach (var binding in bindings.Values)
                {
                    if (!binding.HasPresentationPose || binding.Actor == null) continue;
                    float amount = Mathf.Clamp01((Time.unscaledTime - binding.PoseReceivedAt) / RemotePoseInterpolationSeconds);
                    if (amount > 0f && amount < 1f) interpolationFrames++;
                    Present(binding, amount);
                }
            }
            if (!host || session?.IsRunning != true || channel == null || Time.unscaledTime < nextCapture) return;
            nextCapture = Time.unscaledTime + CaptureSeconds;
            foreach (var binding in bindings.Values)
            {
                if (binding.Actor == null || binding.Route == null || !binding.Actor.gameObject.scene.IsValid()) continue;
                try
                {
                    binding.Current = Capture(binding);
                    channel.Publish(binding.Id, binding.Current.Encode());
                }
                catch (Exception ex)
                {
                    if (!binding.CaptureFailureLogged)
                    {

#if MONMP_DEVELOPMENT
                        File.AppendAllText(Path.Combine(diagnosticsDirectory, "shared-actor-routes-errors.txt"), binding.Identity + "\t" + ex.Message + Environment.NewLine);
#endif

                        Debug.LogWarning("MonMP native capture failed: " + ex.Message);
                        binding.CaptureFailureLogged = true;
                    }
                }
            }
        }

        private static Vector3 Position(ActorRouteState pose) => new Vector3(pose.X, pose.Y, pose.Z);
        private static Quaternion Rotation(ActorRouteState pose) => Quaternion.Euler(pose.EulerX, pose.EulerY, pose.EulerZ);

        private static void Present(Binding binding, float amount)
        {
            var position = Vector3.Lerp(binding.PoseFromPosition, binding.PoseToPosition, amount);
            var rotation = Quaternion.Slerp(binding.PoseFromRotation, binding.PoseToRotation, amount);
            binding.Actor.transform.SetPositionAndRotation(position, rotation);
        }


#if MONMP_DEVELOPMENT
        internal bool TestStart(ulong id)
        {
            if (!host || !bindings.TryGetValue(id, out var binding) || binding.Points.Count < 2) return false;
            if (binding.Route.Get_IsWalking || binding.Actor.gameObject.activeSelf) binding.Route.StopWalking();
            TargetWaypointField?.SetValue(binding.Route, null);
            binding.Route.StartWalking(1, false);
            binding.Current = Capture(binding);
            return binding.Current.Active && binding.Current.Walking;
        }


        internal bool TestStop(ulong id)
        {
            if (!host || !bindings.TryGetValue(id, out var binding) || !binding.Current.Walking) return false;
            binding.Route.StopWalking(); binding.Current = Capture(binding); return !binding.Current.Active && !binding.Current.Walking;
        }
#endif


        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; }
            if (!host)
            {
                foreach (var binding in bindings.Values)
                {
                    if (binding.Actor == null) continue;
                    applyingActor = binding.Actor;
                    try
                    {
                        if (binding.Actor.gameObject.activeSelf != binding.Original.Active) binding.Actor.gameObject.SetActive(binding.Original.Active);
                        binding.Actor.transform.SetPositionAndRotation(new Vector3(binding.Original.X, binding.Original.Y, binding.Original.Z),
                            Quaternion.Euler(binding.Original.EulerX, binding.Original.EulerY, binding.Original.EulerZ));
                        if (binding.Animator != null)
                        {
                            binding.Animator.enabled = binding.OriginalAnimatorEnabled;
                            SetAnimatorBool(binding.Animator, "isWalking", binding.OriginalWalking);
                            SetAnimatorBool(binding.Animator, "isDriving", binding.OriginalDriving);
                            SetAnimatorBool(binding.Animator, "isSitting", binding.OriginalSitting);
                        }
                    }
                    finally { applyingActor = null; }
                }
            }
            session = null; channel = null;
        }
    }
}
