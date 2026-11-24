using System;
using System.Collections.Generic;
using System.Linq;

namespace MonMP.Core.World
{
    /// <summary>
    /// Stable identity for one seat on a registered world entity. SeatId must be
    /// assigned from a vetted native role or hierarchy path, never enumeration order.
    /// </summary>
    public readonly struct SeatKey : IEquatable<SeatKey>
    {
        public readonly ulong EntityId;
        public readonly string SeatId;
        public bool IsValid => EntityId != 0 && !string.IsNullOrWhiteSpace(SeatId) && SeatId.Length <= 96;

        public SeatKey(ulong entityId, string seatId)
        {
            if (entityId == 0) throw new ArgumentOutOfRangeException(nameof(entityId));
            if (string.IsNullOrWhiteSpace(seatId) || seatId.Length > 96)
                throw new ArgumentException("A seat requires a stable ID of 1 to 96 characters.", nameof(seatId));
            EntityId = entityId;
            SeatId = seatId;
        }

        public bool Equals(SeatKey other) => EntityId == other.EntityId && string.Equals(SeatId, other.SeatId, StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is SeatKey other && Equals(other);
        public override int GetHashCode() => unchecked((EntityId.GetHashCode() * 397) ^ (SeatId == null ? 0 : StringComparer.Ordinal.GetHashCode(SeatId)));
        public override string ToString() => IsValid ? EntityId.ToString("x16") + "/" + SeatId : "<invalid-seat>";
        public static bool operator ==(SeatKey left, SeatKey right) => left.Equals(right);
        public static bool operator !=(SeatKey left, SeatKey right) => !left.Equals(right);
    }

    /// <summary>Host-owned occupancy state. Generation changes on each claim and release.</summary>
    public readonly struct SeatOccupancy : IEquatable<SeatOccupancy>
    {
        public const uint Unoccupied = uint.MaxValue;
        public readonly SeatKey Seat;
        public readonly uint Peer;
        public readonly uint Generation;
        public bool IsOccupied => Peer != Unoccupied;

        public SeatOccupancy(SeatKey seat, uint peer, uint generation)
        {
            if (!seat.IsValid) throw new ArgumentException("Invalid seat identity.", nameof(seat));
            if (generation == 0) throw new ArgumentOutOfRangeException(nameof(generation));
            Seat = seat;
            Peer = peer;
            Generation = generation;
        }

        public bool Equals(SeatOccupancy other) => Seat.Equals(other.Seat) && Peer == other.Peer && Generation == other.Generation;
        public override bool Equals(object? obj) => obj is SeatOccupancy other && Equals(other);
        public override int GetHashCode() => unchecked((Seat.GetHashCode() * 397 ^ (int)Peer) * 397 ^ (int)Generation);
    }

    /// <summary>
    /// Host-side reservation table for discrete seat claims. It intentionally does
    /// not grant or revoke continuous vehicle physics authority.
    /// The caller serializes access on the host simulation thread.
    /// </summary>
    public sealed class SeatRegistry
    {
        private readonly Dictionary<SeatKey, SeatOccupancy> seats = new Dictionary<SeatKey, SeatOccupancy>();
        private readonly Dictionary<uint, SeatKey> seatsByPeer = new Dictionary<uint, SeatKey>();

        public int Count => seats.Count;

        public SeatOccupancy Register(SeatKey seat)
        {
            if (!seat.IsValid) throw new ArgumentException("Invalid seat identity.", nameof(seat));
            var occupancy = new SeatOccupancy(seat, SeatOccupancy.Unoccupied, 1);
            seats.Add(seat, occupancy);
            return occupancy;
        }

        /// <summary>Removes a registered empty seat. Occupied seats must be released first.</summary>
        public bool Unregister(SeatKey seat)
        {
            if (!seat.IsValid) return false;
            if (!seats.TryGetValue(seat, out var occupancy)) return false;
            if (occupancy.IsOccupied) throw new InvalidOperationException("Cannot unregister an occupied seat.");
            return seats.Remove(seat);
        }

        public bool TryGet(SeatKey seat, out SeatOccupancy occupancy)
        {
            occupancy = default;
            return seat.IsValid && seats.TryGetValue(seat, out occupancy);
        }

        public bool TryGetSeat(uint peer, out SeatKey seat) => seatsByPeer.TryGetValue(peer, out seat);

        /// <summary>Claims a vacant seat if its current generation still matches the request.</summary>
        public bool TryClaim(SeatKey seat, uint peer, uint expectedGeneration, out SeatOccupancy occupancy)
        {
            occupancy = default;
            if (!seat.IsValid || peer == SeatOccupancy.Unoccupied || expectedGeneration == 0 || !seats.TryGetValue(seat, out var current) ||
                current.IsOccupied || current.Generation != expectedGeneration || seatsByPeer.ContainsKey(peer)) return false;

            occupancy = new SeatOccupancy(seat, peer, Next(current.Generation));
            seats[seat] = occupancy;
            seatsByPeer.Add(peer, seat);
            return true;
        }

        /// <summary>Releases only the matching peer's current claim generation.</summary>
        public bool TryRelease(SeatKey seat, uint peer, uint generation, out SeatOccupancy occupancy)
        {
            occupancy = default;
            if (!seat.IsValid || peer == SeatOccupancy.Unoccupied || generation == 0 || !seats.TryGetValue(seat, out var current) ||
                !current.IsOccupied || current.Peer != peer || current.Generation != generation) return false;

            occupancy = new SeatOccupancy(seat, SeatOccupancy.Unoccupied, Next(current.Generation));
            seats[seat] = occupancy;
            seatsByPeer.Remove(peer);
            return true;
        }

        /// <summary>Vacates a disconnected peer's seat and returns the new empty state for replication.</summary>
        public bool TryReleasePeer(uint peer, out SeatOccupancy occupancy)
        {
            occupancy = default;
            if (peer == SeatOccupancy.Unoccupied || !seatsByPeer.TryGetValue(peer, out var seat)) return false;
            var current = seats[seat];
            occupancy = new SeatOccupancy(seat, SeatOccupancy.Unoccupied, Next(current.Generation));
            seats[seat] = occupancy;
            seatsByPeer.Remove(peer);
            return true;
        }

        /// <summary>Exports every registered seat, including vacant seats, in stable key order.</summary>
        public SeatOccupancy[] Snapshot() => seats.Values
            .OrderBy(value => value.Seat.EntityId)
            .ThenBy(value => value.Seat.SeatId, StringComparer.Ordinal)
            .ToArray();

        private static uint Next(uint generation) => generation == uint.MaxValue ? 1 : generation + 1;
    }
}
