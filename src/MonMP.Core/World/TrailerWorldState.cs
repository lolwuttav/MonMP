using System;
using System.IO;

namespace MonMP.Core.World
{
    /// <summary>Session state for one native vehicle or trailer body in a tow island.</summary>
    public sealed class TrailerWorldState
    {
        public const ushort Adapter = 39;
        public readonly TrailerLinkState Link;
        public readonly ItemPose Pose;
        public readonly VehicleVector Velocity;
        public readonly VehicleVector AngularVelocity;

        public TrailerWorldState(TrailerLinkState link, ItemPose pose,
            VehicleVector velocity = default, VehicleVector angularVelocity = default)
        {
            Link = link;
            Pose = pose;
            Velocity = velocity;
            AngularVelocity = angularVelocity;
        }

        public TrailerWorldState WithLink(TrailerLinkState link) => new TrailerWorldState(link, Pose, Velocity, AngularVelocity);
        /// <summary>Motion authority comes from the latest host record, including on guests.</summary>
        public bool CanPublishMotion(bool host, uint localPeer) => host
            ? Link.Owner == ItemLease.Unowned || Link.Owner == localPeer
            : Link.Owner == localPeer && Link.ParentId != 0;
        public TrailerWorldState WithMotion(ItemPose pose, VehicleVector velocity, VehicleVector angularVelocity)
            => new TrailerWorldState(Link, pose, velocity, angularVelocity);

        // Compare before encoding: stationary trailers need no new payload or world revision.
        public bool SameMotion(TrailerWorldState other) =>
            Link.ParentId == other.Link.ParentId && Link.Owner == other.Link.Owner && Link.Generation == other.Link.Generation &&
            Pose.X == other.Pose.X && Pose.Y == other.Pose.Y && Pose.Z == other.Pose.Z &&
            Pose.Qx == other.Pose.Qx && Pose.Qy == other.Pose.Qy && Pose.Qz == other.Pose.Qz && Pose.Qw == other.Pose.Qw &&
            Velocity.X == other.Velocity.X && Velocity.Y == other.Velocity.Y && Velocity.Z == other.Velocity.Z &&
            AngularVelocity.X == other.AngularVelocity.X && AngularVelocity.Y == other.AngularVelocity.Y && AngularVelocity.Z == other.AngularVelocity.Z;

        /// <summary>An undriven trailer still resting at this published sample (see RestDeadband).</summary>
        public bool RestsWith(TrailerWorldState other) =>
            Link.Owner == ItemLease.Unowned && Link.ParentId == other.Link.ParentId && Link.Owner == other.Link.Owner &&
            Link.Generation == other.Link.Generation &&
            RestDeadband.Holds(Pose, Velocity, AngularVelocity, other.Pose, other.Velocity, other.AngularVelocity);

        private void Validate()
        {
            Pose.Validate();
            Velocity.Validate(200);
            AngularVelocity.Validate(50);
            if (Link.Generation == 0) throw new InvalidDataException("Invalid trailer link generation.");
        }

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(68);
            using var writer = new BinaryWriter(stream);
            writer.Write(Link.ParentId);
            writer.Write(Link.Owner);
            writer.Write(Link.Generation);
            Pose.Write(writer);
            Velocity.Write(writer);
            AngularVelocity.Write(writer);
            return stream.ToArray();
        }

        public static TrailerWorldState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 68) throw new InvalidDataException("Invalid trailer world state length.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            var link = new TrailerLinkState(reader.ReadUInt64(), reader.ReadUInt32(), reader.ReadUInt32());
            var pose = ItemPose.Read(reader);
            var velocity = VehicleVector.Read(reader);
            var angularVelocity = VehicleVector.Read(reader);
            var result = new TrailerWorldState(link, pose, velocity, angularVelocity);
            result.Validate();
            return result;
        }
    }

    public enum TrailerAuthorityAction : byte { Attach = 1, Detach = 2, Move = 3 }

    public readonly struct TrailerAuthorityCommand
    {
        public readonly TrailerAuthorityAction Action;
        public readonly ulong TowId;
        public readonly uint ExpectedTowGeneration;
        public readonly uint ExpectedTrailerGeneration;
        public readonly TrailerWorldState? Motion;

        public TrailerAuthorityCommand(TrailerAuthorityAction action, ulong towId,
            uint expectedTowGeneration, uint expectedTrailerGeneration, TrailerWorldState? motion = null)
        {
            if (!Enum.IsDefined(typeof(TrailerAuthorityAction), action) || towId == 0 ||
                expectedTowGeneration == 0 || expectedTrailerGeneration == 0 ||
                (action == TrailerAuthorityAction.Move) != (motion != null))
                throw new ArgumentException("Invalid trailer authority command.");
            Action = action;
            TowId = towId;
            ExpectedTowGeneration = expectedTowGeneration;
            ExpectedTrailerGeneration = expectedTrailerGeneration;
            Motion = motion;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Motion == null ? 17 : 85);
            using var writer = new BinaryWriter(stream);
            writer.Write((byte)Action);
            writer.Write(TowId);
            writer.Write(ExpectedTowGeneration);
            writer.Write(ExpectedTrailerGeneration);
            if (Motion != null) writer.Write(Motion.Encode());
            return stream.ToArray();
        }

        public static TrailerAuthorityCommand Decode(byte[] payload)
        {
            if (payload == null || (payload.Length != 17 && payload.Length != 85))
                throw new InvalidDataException("Invalid trailer authority command length.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            byte rawAction = reader.ReadByte();
            if (!Enum.IsDefined(typeof(TrailerAuthorityAction), rawAction)) throw new InvalidDataException("Invalid trailer authority action.");
            var action = (TrailerAuthorityAction)rawAction;
            ulong towId = reader.ReadUInt64();
            uint towGeneration = reader.ReadUInt32(), trailerGeneration = reader.ReadUInt32();
            TrailerWorldState? motion = action == TrailerAuthorityAction.Move
                ? TrailerWorldState.Decode(reader.ReadBytes(68)) : (TrailerWorldState?)null;
            if (stream.Position != stream.Length || (action == TrailerAuthorityAction.Move) != (motion != null))
                throw new InvalidDataException("Invalid trailer authority command payload.");
            try { return new TrailerAuthorityCommand(action, towId, towGeneration, trailerGeneration, motion); }
            catch (ArgumentException ex) { throw new InvalidDataException("Invalid trailer authority command values.", ex); }
        }
    }
}
