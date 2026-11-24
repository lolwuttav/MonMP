using System;
using System.Collections.Generic;
using System.IO;
using MonMP.Core.Protocol;

namespace MonMP.Core.World
{
    public enum SprayPaintAction : byte { Begin = 1, Update = 2, End = 3 }

    // A mouse-held spray is a short host-authorized session. The host validates
    // the bounded camera ray against the world and resolves its own paint target;
    // color and consumption come from the host's native can replica.
    public readonly struct SprayPaintCommand
    {
        public const byte Marker = 0xE7;
        public const int Size = 55;
        public readonly bool IndependentAim;
        public readonly SprayPaintAction Action;
        public readonly Guid Session;
        public readonly uint Sequence;
        public readonly ulong Tool;
        public readonly uint Generation;
        public readonly float CameraX, CameraY, CameraZ;
        public readonly float CameraYaw, CameraPitch;

        public SprayPaintCommand(SprayPaintAction action, Guid session, uint sequence,
            ulong tool, uint generation, float cameraX, float cameraY, float cameraZ, float cameraYaw, float cameraPitch, bool independentAim = false)
        {
            Action = action; Session = session; Sequence = sequence;
            Tool = tool; Generation = generation;
            CameraX = cameraX; CameraY = cameraY; CameraZ = cameraZ;
            CameraYaw = cameraYaw; CameraPitch = cameraPitch;
            IndependentAim = independentAim; Validate();
        }

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(Size); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write((byte)Action); writer.Write(Session.ToByteArray());
            writer.Write(Sequence); writer.Write(Tool); writer.Write(Generation);
            writer.Write(CameraX); writer.Write(CameraY); writer.Write(CameraZ); writer.Write(CameraYaw); writer.Write(CameraPitch);
            writer.Write(IndependentAim);
            return stream.ToArray();
        }

        public static SprayPaintCommand Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size)
                throw new InvalidDataException("Invalid spray paint command length.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            if (reader.ReadByte() != Marker) throw new InvalidDataException("Invalid spray paint command marker.");
            var command = new SprayPaintCommand((SprayPaintAction)reader.ReadByte(), new Guid(reader.ReadBytes(16)),
                reader.ReadUInt32(), reader.ReadUInt64(), reader.ReadUInt32(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadBoolean());
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing spray paint command data.");
            return command;
        }

        public static bool Is(byte[]? payload) => payload != null && payload.Length > 0 && payload[0] == Marker;

        private void Validate()
        {
            if (!Enum.IsDefined(typeof(SprayPaintAction), Action) || Session == Guid.Empty || Sequence == 0 ||
                Tool <= WorldRecord.DynamicIdStart || Generation == 0)
                throw new InvalidDataException("Invalid spray paint session identity.");
            if ((Action == SprayPaintAction.Begin) != (Sequence == 1))
                throw new InvalidDataException("Invalid spray paint session sequence.");
            if (!Finite(CameraX) || !Finite(CameraY) || !Finite(CameraZ) ||
                Math.Abs(CameraX) > 100000 || Math.Abs(CameraY) > 100000 || Math.Abs(CameraZ) > 100000)
                throw new InvalidDataException("Invalid spray paint camera origin.");
            if (!Finite(CameraYaw) || CameraYaw < 0 || CameraYaw > 360 ||
                !Finite(CameraPitch) || CameraPitch < -90 || CameraPitch > 90)
                throw new InvalidDataException("Invalid spray paint camera direction.");
        }

        public static bool CameraOriginFits(PlayerPose player, float cameraX, float cameraY, float cameraZ, bool allowCreeper = false) =>
            ToolAimPolicy.CameraOriginFits(player, cameraX, cameraY, cameraZ, allowCreeper);

        public static bool CameraAimFits(PlayerPose player, float cameraYaw, float cameraPitch, bool allowCreeper = false, bool allowChair = false) =>
            ToolAimPolicy.CameraAimFits(player, cameraYaw, cameraPitch, allowCreeper, allowChair);

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public enum SprayPaintSequenceResult : byte { Accepted, Duplicate, OutOfOrder, Capacity }

    // Duplicate-safe sequence state, keyed by peer and an unpredictable session
    // id. Ended sessions stay claimed until that peer leaves, preventing replay.
    public sealed class SprayPaintSequences
    {
        public const int Capacity = 8192;
        private readonly Dictionary<(uint Peer, Guid Session), (ulong Tool, uint Generation, uint Sequence, bool Ended)> latest =
            new Dictionary<(uint, Guid), (ulong, uint, uint, bool)>();
        public int Count => latest.Count;

        public SprayPaintSequenceResult Claim(uint peer, SprayPaintCommand command)
        {
            if (peer == 0) throw new InvalidDataException("Invalid spray paint peer.");
            var key = (peer, command.Session);
            if (!latest.TryGetValue(key, out var current))
            {
                if (command.Action != SprayPaintAction.Begin || command.Sequence != 1)
                    return SprayPaintSequenceResult.OutOfOrder;
                if (latest.Count >= Capacity) return SprayPaintSequenceResult.Capacity;
                latest.Add(key, (command.Tool, command.Generation, 1, false));
                return SprayPaintSequenceResult.Accepted;
            }
            if (current.Tool != command.Tool || current.Generation != command.Generation || current.Ended ||
                command.Action == SprayPaintAction.Begin || command.Sequence <= current.Sequence)
                return SprayPaintSequenceResult.Duplicate;
            if (command.Sequence != current.Sequence + 1) return SprayPaintSequenceResult.OutOfOrder;
            latest[key] = (current.Tool, current.Generation, command.Sequence, command.Action == SprayPaintAction.End);
            return SprayPaintSequenceResult.Accepted;
        }

        public void RemovePeer(uint peer)
        {
            foreach (var key in new List<(uint Peer, Guid Session)>(latest.Keys))
                if (key.Peer == peer) latest.Remove(key);
        }

        public void Clear() => latest.Clear();
    }
}
