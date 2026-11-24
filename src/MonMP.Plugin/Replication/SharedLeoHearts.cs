using System;
using System.Collections.Generic;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Leo's heart pops over him when anyone pets him or gives him a treat. Natively it only shows on
    // the machine that did it; here the host counts every heart its Leo shows and guests pop theirs
    // when the count moves. A guest's pet asks the host to apply the native shared pet effect too.
    internal sealed class SharedLeoHearts : IDisposable
    {
        public const ushort Adapter = 98;
        internal static readonly ulong Entity = StaticId.Hash("leo-heart/v1");
        private const float PetRange = 6f;
        private static readonly System.Reflection.FieldInfo? Running = AccessTools.Field(typeof(PetHearth), "CoroutineRunning");

        // Read live: the world is built before the session settles its role.
        private bool host => session?.IsHost == true;
        private readonly Dictionary<uint, (Vector3 At, float Time)> presence = new Dictionary<uint, (Vector3, float)>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private byte count;
        private bool shown, known, disposed;
        private float nextRequest, activeSince;


#if MONMP_DEVELOPMENT
        internal int TestHearts { get; private set; }


        internal int TestGuestShown { get; private set; }
#endif


        internal SharedLeoHearts() { }

        internal static IEnumerable<WorldRecord> InitialRecords() => new[] { new WorldRecord(Entity, 1, Adapter, new byte[] { 0 }) };

        internal void Attach(NetworkSession connected, WorldChannel world)
        {
            session = connected; channel = world;
            connected.PoseReceived += OnPose; connected.PeerLeft += OnLeft;
            world.SnapshotApplied += Snapshot; world.Changed += Changed; world.CommandRequested += Command;
            world.CommandRejected += (peer, entity, _) => { if (entity == Entity) Debug.Log("MonMP Leo heart request from " + peer + " rejected as stale."); };
        }

        private static GameObject? Heart()
        {
            var leo = Singleton<Gameplay>.i?.Get_Leo_Manager;
            return leo != null && leo.petHearth != null ? leo.petHearth.gameObject : null;
        }

        private static void Show(GameObject heart)
        {
            if (heart.activeSelf || !heart.transform.parent.gameObject.activeInHierarchy) return;
            Pop(heart);
        }

        // The heart's animation runs from OnEnable, guarded by a flag that stays set if the heart (or
        // Leo) is disabled mid-animation; after that every later OnEnable skipped it. Clear it first.
        internal static void Pop(GameObject heart)
        {
            var native = heart.GetComponent<PetHearth>();
            if (native != null) Running?.SetValue(native, false);
            heart.SetActive(true);
        }

        // A heart left on without its animation (the stuck case above) is reset so it can pop again.
        private void Watchdog(GameObject? heart)
        {
            if (heart == null || !heart.activeSelf) { activeSince = 0; return; }
            if (activeSince == 0) { activeSince = Time.unscaledTime; return; }
            if (Time.unscaledTime - activeSince < 4f) return;
            activeSince = 0;
            var native = heart.GetComponent<PetHearth>();
            if (native != null) { native.StopAllCoroutines(); Running?.SetValue(native, false); }
            heart.SetActive(false);
        }

        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (new Vector3(pose.X, pose.Y, pose.Z), Time.unscaledTime);
        private void OnLeft(uint peer) => presence.Remove(peer);

        // Host: every heart its Leo shows, from its own player or a guest's request.
        internal void Update()
        {
            if (disposed || channel?.Ready != true) return;
            var heart = Heart();
            Watchdog(heart);
            if (!host) return;
            bool active = heart != null && heart.activeSelf;
            if (active && !shown)
            {
                count++;
                channel.Publish(Entity, new[] { count });
            }
            shown = active;
        }

        // Guest: its own pet showed the heart locally; the host and the other players see it too.
        internal void Petted()
        {
            if (host || disposed || channel?.Ready != true || Time.unscaledTime < nextRequest) return;
            nextRequest = Time.unscaledTime + 1f;
            channel.Request(Entity, new byte[] { 1 });
        }

        [MonMP.Core.World.WorldCommandAdapters(SharedLeoHearts.Adapter)]
        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (!host || entity != Entity || peer == 0 || payload.Length != 1 || payload[0] != 1) return;
            var heart = Heart();
            if (heart == null || !presence.TryGetValue(peer, out var seen) || Time.unscaledTime - seen.Time > 2f ||
                Vector3.Distance(seen.At, heart.transform.position) > PetRange)
            {
                Debug.Log("MonMP Leo heart request from " + peer + " refused: " + (heart == null ? "no heart" :
                    !presence.TryGetValue(peer, out seen) ? "no pose" : "pose age " + (Time.unscaledTime - seen.Time).ToString("F1") +
                    " s, distance " + Vector3.Distance(seen.At, heart.transform.position).ToString("F1") + " m"));
                return;
            }
            if (MonMPPlugin.Instance?.World?.Leo.PetFromGuest(peer) != true) return;

#if MONMP_DEVELOPMENT
            TestGuestShown++;
#endif

        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            // The count at join is history; only later hearts pop.
            if (!host && records.TryGetValue(Entity, out var record) && record.Payload.Length == 1) { count = record.Payload[0]; known = true; }
        }

        private void Changed(WorldRecord record)
        {
            if (host || record.Id != Entity || record.Payload.Length != 1) return;
            bool fresh = known && record.Payload[0] != count;
            count = record.Payload[0]; known = true;
            if (!fresh) return;
            var heart = Heart();
            if (heart == null) return;

#if MONMP_DEVELOPMENT
            TestHearts++;
#endif

            Show(heart);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; }
            session = null; channel = null;
        }
    }
}
