using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MonMP.Core.World
{
    /// <summary>
    /// The host's topology and simulation owner for one registered vehicle or trailer.
    /// ParentId is zero for an independent body. Entity identity is supplied by the
    /// world record, so the payload contains only this link and its generation.
    /// </summary>
    public readonly struct TrailerLinkState
    {
        public readonly ulong ParentId;
        public readonly uint Owner;
        public readonly uint Generation;

        public TrailerLinkState(ulong parentId, uint owner, uint generation)
        {
            if (generation == 0) throw new ArgumentOutOfRangeException(nameof(generation));
            ParentId = parentId;
            Owner = owner;
            Generation = generation;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(16);
            using var writer = new BinaryWriter(stream);
            writer.Write(ParentId);
            writer.Write(Owner);
            writer.Write(Generation);
            return stream.ToArray();
        }

        public static TrailerLinkState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 16) throw new InvalidDataException("Invalid trailer link length.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            ulong parent = reader.ReadUInt64();
            uint owner = reader.ReadUInt32(), generation = reader.ReadUInt32();
            if (generation == 0) throw new InvalidDataException("Invalid trailer link generation.");
            return new TrailerLinkState(parent, owner, generation);
        }
    }

    /// <summary>
    /// Host simulation-thread registry for trailer/tow authority islands. Native
    /// proximity, hitch installation, joint creation, and body mode are validated
    /// and applied by the caller before publishing a successful mutation.
    /// </summary>
    public sealed class TrailerAuthorityGraph
    {
        private sealed class Node
        {
            public bool CanTow;
            public bool DriverAssignable;
            public TrailerLinkState Link = new TrailerLinkState(0, ItemLease.Unowned, 1);
        }

        private readonly Dictionary<ulong, Node> nodes = new Dictionary<ulong, Node>();

        public int Count => nodes.Count;

        public TrailerLinkState Register(ulong id, bool canTow, bool driverAssignable = false)
        {
            if (id == 0) throw new ArgumentOutOfRangeException(nameof(id));
            var node = new Node { CanTow = canTow, DriverAssignable = driverAssignable };
            nodes.Add(id, node);
            return node.Link;
        }

        public bool TryGet(ulong id, out TrailerLinkState link)
        {
            link = default;
            if (!nodes.TryGetValue(id, out var node)) return false;
            link = node.Link;
            return true;
        }

        /// <summary>Changes an independent root and all of its attached descendants atomically.</summary>
        public bool TrySetRootOwner(ulong id, uint expectedGeneration, uint owner)
        {
            if (!nodes.TryGetValue(id, out var root) || root.Link.ParentId != 0 ||
                (owner != ItemLease.Unowned && !root.DriverAssignable) ||
                root.Link.Generation != expectedGeneration || root.Link.Owner == owner) return false;
            SetOwnerForSubtree(id, owner);
            return true;
        }

        /// <summary>
        /// Attaches a separate island at a free hitch. The native caller must verify
        /// both bodies, range, and hitch/joint state before calling this method.
        /// </summary>
        public bool TryAttach(ulong towId, ulong childId, uint expectedTowGeneration,
            uint expectedChildGeneration, uint actor)
            => TryAttach(towId, childId, expectedTowGeneration, expectedChildGeneration, actor,
                hostValidatedInteraction: false);

        /// <summary>
        /// Host structural interaction after the caller has validated the live actor's walking state,
        /// proximity, hitch geometry and native attach conditions. The tow owner's simulation lease
        /// remains authoritative; this only permits another validated actor to request the topology change.
        /// </summary>
        public bool TryAttachFromValidatedInteraction(ulong towId, ulong childId, uint expectedTowGeneration,
            uint expectedChildGeneration, uint actor)
            => TryAttach(towId, childId, expectedTowGeneration, expectedChildGeneration, actor,
                hostValidatedInteraction: true);

        private bool TryAttach(ulong towId, ulong childId, uint expectedTowGeneration,
            uint expectedChildGeneration, uint actor, bool hostValidatedInteraction)
        {
            if (towId == childId || !nodes.TryGetValue(towId, out var tow) || !nodes.TryGetValue(childId, out var child) ||
                !tow.CanTow || tow.Link.Generation != expectedTowGeneration || child.Link.Generation != expectedChildGeneration ||
                child.Link.ParentId != 0 || nodes.Values.Any(n => n.Link.ParentId == towId) ||
                (tow.Link.Owner != ItemLease.Unowned && tow.Link.Owner != actor && !hostValidatedInteraction) ||
                child.Link.Owner != ItemLease.Unowned ||
                RootOf(towId) == childId) return false;

            tow.Link = new TrailerLinkState(tow.Link.ParentId, tow.Link.Owner, Next(tow.Link.Generation));
            child.Link = new TrailerLinkState(towId, tow.Link.Owner, Next(child.Link.Generation));
            foreach (ulong descendant in Descendants(childId).Skip(1))
            {
                var node = nodes[descendant];
                node.Link = new TrailerLinkState(node.Link.ParentId, tow.Link.Owner, Next(node.Link.Generation));
            }
            return true;
        }

        /// <summary>Detaches an island. The detached body and its children return to host simulation.</summary>
        public bool TryDetach(ulong towId, ulong childId, uint expectedTowGeneration,
            uint expectedChildGeneration, uint actor)
            => TryDetach(towId, childId, expectedTowGeneration, expectedChildGeneration, actor,
                hostValidatedInteraction: false);

        /// <summary>Host structural interaction after validating the live actor and native hitch state.</summary>
        public bool TryDetachFromValidatedInteraction(ulong towId, ulong childId, uint expectedTowGeneration,
            uint expectedChildGeneration, uint actor)
            => TryDetach(towId, childId, expectedTowGeneration, expectedChildGeneration, actor,
                hostValidatedInteraction: true);

        private bool TryDetach(ulong towId, ulong childId, uint expectedTowGeneration,
            uint expectedChildGeneration, uint actor, bool hostValidatedInteraction)
        {
            if (!nodes.TryGetValue(towId, out var tow) || !nodes.TryGetValue(childId, out var child) ||
                child.Link.ParentId != towId || tow.Link.Generation != expectedTowGeneration ||
                child.Link.Generation != expectedChildGeneration ||
                (tow.Link.Owner != ItemLease.Unowned && tow.Link.Owner != actor && !hostValidatedInteraction)) return false;

            tow.Link = new TrailerLinkState(tow.Link.ParentId, tow.Link.Owner, Next(tow.Link.Generation));
            child.Link = new TrailerLinkState(0, ItemLease.Unowned, Next(child.Link.Generation));
            foreach (ulong descendant in Descendants(childId).Skip(1))
            {
                var node = nodes[descendant];
                node.Link = new TrailerLinkState(node.Link.ParentId, ItemLease.Unowned, Next(node.Link.Generation));
            }
            return true;
        }

        /// <summary>Host takeover after a driver disconnects; the tow graph stays connected.</summary>
        public KeyValuePair<ulong, TrailerLinkState>[] RevokePeer(uint peer)
        {
            if (peer == ItemLease.Unowned) return Array.Empty<KeyValuePair<ulong, TrailerLinkState>>();
            var affected = new List<ulong>();
            foreach (ulong root in nodes.Where(pair => pair.Value.Link.ParentId == 0 && pair.Value.Link.Owner == peer)
                .Select(pair => pair.Key).ToArray())
            {
                affected.AddRange(Descendants(root));
                SetOwnerForSubtree(root, ItemLease.Unowned);
            }
            return affected.OrderBy(id => id).Select(id => new KeyValuePair<ulong, TrailerLinkState>(id, nodes[id].Link)).ToArray();
        }

        /// <summary>Complete, stable-order topology for a late joiner.</summary>
        public KeyValuePair<ulong, TrailerLinkState>[] Snapshot() => nodes
            .OrderBy(pair => pair.Key)
            .Select(pair => new KeyValuePair<ulong, TrailerLinkState>(pair.Key, pair.Value.Link))
            .ToArray();

        /// <summary>
        /// Replaces every link from a complete network snapshot after checking the
        /// topology as a whole. Partial link updates must use TryAttach/TryDetach.
        /// </summary>
        public void RestoreSnapshot(IEnumerable<KeyValuePair<ulong, TrailerLinkState>> snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            var staged = snapshot.ToDictionary(pair => pair.Key, pair => pair.Value);
            if (staged.Count != nodes.Count || staged.Keys.Any(id => !nodes.ContainsKey(id)))
                throw new InvalidDataException("Trailer snapshot does not match the registered catalog.");

            var childCounts = new Dictionary<ulong, int>();
            foreach (var pair in staged)
            {
                ulong id = pair.Key;
                TrailerLinkState link = pair.Value;
                if (id == 0 || link.Generation == 0) throw new InvalidDataException("Invalid trailer snapshot identity or generation.");
                if (link.ParentId != 0)
                {
                    if (link.ParentId == id || !nodes.TryGetValue(link.ParentId, out var parent) || !parent.CanTow)
                        throw new InvalidDataException("Trailer snapshot references an invalid tow parent.");
                    int children = childCounts.TryGetValue(link.ParentId, out int count) ? count + 1 : 1;
                    if (children > 1) throw new InvalidDataException("Trailer snapshot assigns more than one child to a hitch.");
                    childCounts[link.ParentId] = children;
                }
            }

            foreach (var pair in staged)
            {
                ulong root = pair.Key;
                var visited = new HashSet<ulong>();
                while (staged[root].ParentId != 0)
                {
                    if (!visited.Add(root)) throw new InvalidDataException("Trailer snapshot contains a cycle.");
                    root = staged[root].ParentId;
                }
                uint owner = staged[root].Owner;
                if (owner != ItemLease.Unowned && !nodes[root].DriverAssignable)
                    throw new InvalidDataException("Trailer snapshot assigns a driver to a non-drivable root.");
                if (pair.Value.Owner != owner) throw new InvalidDataException("Trailer snapshot has inconsistent island ownership.");
            }

            foreach (var pair in staged) nodes[pair.Key].Link = pair.Value;
        }

        private void SetOwnerForSubtree(ulong root, uint owner)
        {
            foreach (ulong id in Descendants(root))
            {
                var node = nodes[id];
                node.Link = new TrailerLinkState(node.Link.ParentId, owner, Next(node.Link.Generation));
            }
        }

        private ulong RootOf(ulong id)
        {
            while (nodes[id].Link.ParentId != 0) id = nodes[id].Link.ParentId;
            return id;
        }

        private IEnumerable<ulong> Descendants(ulong root)
        {
            yield return root;
            foreach (ulong child in nodes.Where(pair => pair.Value.Link.ParentId == root).Select(pair => pair.Key).ToArray())
                foreach (ulong descendant in Descendants(child)) yield return descendant;
        }

        private static uint Next(uint generation) => generation == uint.MaxValue ? 1 : generation + 1;
    }
}
