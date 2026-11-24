using System;
using System.IO;
using System.Linq;
using MonMP.Core.Protocol;

namespace MonMP.Core.Testing
{
    // Development-only Director bridge. These are live native anchors, not authority:
    // clients must still claim the referenced ordinary world record before sitting.
    public sealed class ProtocolActorTargets
    {
        public const int CurrentVersion = 1;
        public int SchemaVersion { get; set; } = CurrentVersion;
        public long UtcUnixMs { get; set; }
        public ProtocolActorTarget[] Targets { get; set; } = Array.Empty<ProtocolActorTarget>();
        public ProtocolActorCommand[] Commands { get; set; } = Array.Empty<ProtocolActorCommand>();

        public void Validate()
        {
            if (SchemaVersion != CurrentVersion || UtcUnixMs <= 0 || Targets == null || Targets.Length > 256)
                throw new InvalidDataException("Invalid protocol actor target feed.");
            foreach (var target in Targets)
            {
                if (target == null) throw new InvalidDataException("Missing protocol actor target.");
                target.Validate();
            }
            if (Commands == null || Commands.Length > 256 || Commands.Any(command => command == null ||
                string.IsNullOrWhiteSpace(command.Name) || command.Name.Length > 128 || command.EntityId == 0 ||
                command.Payload == null || command.Payload.Length > 128) ||
                Commands.Select(command => command.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Commands.Length)
                throw new InvalidDataException("Invalid protocol actor command aliases.");
            if (Targets.Select(target => target.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Targets.Length)
                throw new InvalidDataException("Protocol actor target names must be unique.");
        }

        public ProtocolActorTarget? Find(string name) => Targets.FirstOrDefault(target =>
            string.Equals(target.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public sealed class ProtocolActorCommand
    {
        public string Name { get; set; } = "";
        public ulong EntityId { get; set; }
        public byte[] Payload { get; set; } = Array.Empty<byte>();
        public bool UseLease { get; set; } = true;
        public string Interaction { get; set; } = "";
    }

    public sealed class ProtocolActorTarget
    {
        public string Name { get; set; } = "";
        // point = location only; passenger = adapter 36; driver = adapter 4; furniture = adapter 143.
        public string Kind { get; set; } = "point";
        public ulong EntityId { get; set; }
        public byte State { get; set; }
        public ProtocolActorSpawn Position { get; set; } = new ProtocolActorSpawn();
        public ulong CarrierId { get; set; }
        public float CarrierX { get; set; }
        public float CarrierY { get; set; }
        public float CarrierZ { get; set; }
        public bool NpcOccupied { get; set; }
        public ProtocolActorSpawn? YieldPosition { get; set; }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Name) || Name.Length > 128 || Position == null ||
                (Kind != "point" && Kind != "passenger" && Kind != "driver" && Kind != "furniture") ||
                (Kind == "point" ? EntityId != 0 : EntityId == 0))
                throw new InvalidDataException("Invalid protocol actor target descriptor.");
            new PlayerPose(Position.X, Position.Y, Position.Z, Position.Yaw, Position.Pitch, State).Validate();
            if (CarrierId != 0) Position.ToPose().WithCarrier(CarrierId, CarrierX, CarrierY, CarrierZ).Validate();
            YieldPosition?.ToPose().Validate();
        }
    }
}
