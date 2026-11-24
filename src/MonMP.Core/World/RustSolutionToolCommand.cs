using System;
using System.IO;
using MonMP.Core.Protocol;

namespace MonMP.Core.World
{
    // A guest bottle click carries only its lease and camera pose. The host
    // resolves the target panel from its own raycast and applies ForceRust.
    public readonly struct RustSolutionToolCommand
    {
        public const byte Marker = 0xEA;
        public const int Size = 50;
        public readonly bool IndependentAim;

        public Guid Operation { get; }
        public ulong Tool { get; }
        public uint Generation { get; }
        public float CameraX { get; }
        public float CameraY { get; }
        public float CameraZ { get; }
        public float CameraYaw { get; }
        public float CameraPitch { get; }

        public RustSolutionToolCommand(Guid operation, ulong tool, uint generation,
            float cameraX, float cameraY, float cameraZ, float cameraYaw, float cameraPitch, bool independentAim = false)
        {
            Operation = operation;
            Tool = tool;
            Generation = generation;
            CameraX = cameraX;
            CameraY = cameraY;
            CameraZ = cameraZ;
            CameraYaw = cameraYaw;
            CameraPitch = cameraPitch;
            IndependentAim = independentAim;
            Validate();
        }

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker);
            writer.Write(Operation.ToByteArray());
            writer.Write(Tool);
            writer.Write(Generation);
            writer.Write(CameraX);
            writer.Write(CameraY);
            writer.Write(CameraZ);
            writer.Write(CameraYaw);
            writer.Write(CameraPitch);
            writer.Write(IndependentAim);
            return stream.ToArray();
        }

        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public static bool HasMarker(byte[]? payload) => payload != null && payload.Length > 0 && payload[0] == Marker;

        public static RustSolutionToolCommand Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid rust solution command size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            _ = reader.ReadByte();
            var command = new RustSolutionToolCommand(new Guid(reader.ReadBytes(16)), reader.ReadUInt64(), reader.ReadUInt32(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadBoolean());
            if (reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException("Trailing rust solution command data.");
            return command;
        }

        public bool CameraFits(PlayerPose player, AimSegmentTest? clear = null) =>
            ToolAimPolicy.Fits(player, IndependentAim, CameraX, CameraY, CameraZ, CameraYaw, CameraPitch, clear);

        private void Validate()
        {
            if (Operation == Guid.Empty || Tool <= WorldRecord.DynamicIdStart || Generation == 0)
                throw new InvalidDataException("Invalid rust solution tool identity.");
            if (!Finite(CameraX) || !Finite(CameraY) || !Finite(CameraZ) ||
                Math.Abs(CameraX) > 100000 || Math.Abs(CameraY) > 100000 || Math.Abs(CameraZ) > 100000 ||
                !Finite(CameraYaw) || CameraYaw < 0 || CameraYaw > 360 ||
                !Finite(CameraPitch) || CameraPitch < -90 || CameraPitch > 90)
                throw new InvalidDataException("Invalid rust solution camera pose.");
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public static class RustSolutionToolPolicy
    {
        public const float CooldownSeconds = 2f;

        public static bool CanExecuteHostRequest(bool worldSynchronized, bool actionsBlocked, bool requestBudget,
            bool cooldownReady, bool exactBottleLease, bool bottleInRange, bool freshWalkingPose, bool cameraFitsPose,
            bool targetRegisteredAndActive, bool panelInRange, bool rayHitsRequestedPanel, ulong requestedPanel) =>
            worldSynchronized && !actionsBlocked && requestBudget && cooldownReady && exactBottleLease && bottleInRange &&
            freshWalkingPose && cameraFitsPose && targetRegisteredAndActive && panelInRange &&
            rayHitsRequestedPanel && requestedPanel != 0;

        public static bool CooldownElapsed(float now, float lastUse) =>
            Finite(now) && Finite(lastUse) && now >= 0 && lastUse >= 0 && now >= lastUse + CooldownSeconds;

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    // Per-peer, per-lease server cooldown. Call RecordUse only after all host
    // authorization checks have passed; no-change panels still consume cooldown.
    public sealed class RustSolutionCooldowns
    {
        private const int MaxTrackedLeases = 4096;
        private readonly System.Collections.Generic.Dictionary<(uint Peer, ulong Tool), (uint Generation, float NextUse)> entries =
            new System.Collections.Generic.Dictionary<(uint, ulong), (uint, float)>();

        public int Count => entries.Count;

        public bool IsReady(uint peer, ulong tool, uint generation, float now)
        {
            if (!ValidIdentity(peer, tool, generation) || !Finite(now) || now < 0) return false;
            Prune(now);
            if (!entries.TryGetValue((peer, tool), out var entry))
                return now >= RustSolutionToolPolicy.CooldownSeconds && entries.Count < MaxTrackedLeases;
            return entry.Generation != generation || now >= entry.NextUse;
        }

        public bool RecordUse(uint peer, ulong tool, uint generation, float now)
        {
            if (!IsReady(peer, tool, generation, now)) return false;
            entries[(peer, tool)] = (generation, now + RustSolutionToolPolicy.CooldownSeconds);
            return true;
        }

        public void RemovePeer(uint peer)
        {
            foreach (var key in new System.Collections.Generic.List<(uint Peer, ulong Tool)>(entries.Keys))
                if (key.Peer == peer) entries.Remove(key);
        }

        private void Prune(float now)
        {
            foreach (var pair in entries)
                if (pair.Value.NextUse <= now)
                    expired.Add(pair.Key);
            foreach (var key in expired) entries.Remove(key);
            expired.Clear();
        }

        private readonly System.Collections.Generic.List<(uint Peer, ulong Tool)> expired =
            new System.Collections.Generic.List<(uint, ulong)>();

        private static bool ValidIdentity(uint peer, ulong tool, uint generation) =>
            peer != 0 && tool > WorldRecord.DynamicIdStart && generation != 0;

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
