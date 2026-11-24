using System;
using System.IO;
using MonMP.Core.Protocol;

namespace MonMP.Core.World
{
    // A single guest MouseDown. The server derives repair-versus-damage from
    // its own click history and native panel result; the client cannot choose.
    public readonly struct DentRepairToolCommand
    {
        public const byte Marker = 0xE9;
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

        public DentRepairToolCommand(Guid operation, ulong tool, uint generation,
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

        public static DentRepairToolCommand Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid dent repair command size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            _ = reader.ReadByte();
            var command = new DentRepairToolCommand(new Guid(reader.ReadBytes(16)), reader.ReadUInt64(), reader.ReadUInt32(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadBoolean());
            if (reader.BaseStream.Position != reader.BaseStream.Length)
                throw new InvalidDataException("Trailing dent repair command data.");
            return command;
        }

        public bool CameraFits(PlayerPose player, AimSegmentTest? clear = null) =>
            ToolAimPolicy.Fits(player, IndependentAim, CameraX, CameraY, CameraZ, CameraYaw, CameraPitch, clear);

        private void Validate()
        {
            if (Operation == Guid.Empty || Tool <= WorldRecord.DynamicIdStart || Generation == 0)
                throw new InvalidDataException("Invalid dent repair tool identity.");
            if (!Finite(CameraX) || !Finite(CameraY) || !Finite(CameraZ) ||
                Math.Abs(CameraX) > 100000 || Math.Abs(CameraY) > 100000 || Math.Abs(CameraZ) > 100000 ||
                !Finite(CameraYaw) || CameraYaw < 0 || CameraYaw > 360 ||
                !Finite(CameraPitch) || CameraPitch < -90 || CameraPitch > 90)
                throw new InvalidDataException("Invalid dent repair camera pose.");
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public static class DentRepairToolPolicy
    {
        public static bool CanExecuteHostRequest(bool worldSynchronized, bool actionsBlocked, bool requestBudget,
            bool exactKitLease, bool kitInRange, bool freshWalkingPose, bool cameraFitsPose,
            bool targetRegisteredAndActive, bool panelInRange, bool rayHitsRequestedPanel, ulong requestedPanel) =>
            worldSynchronized && !actionsBlocked && requestBudget && exactKitLease && kitInRange &&
            freshWalkingPose && cameraFitsPose && targetRegisteredAndActive && panelInRange &&
            rayHitsRequestedPanel && requestedPanel != 0;
    }

    public enum DentRepairClickResult : byte
    {
        Rejected = 0,
        NoChange = 1,
        Repaired = 2,
        Damaged = 3,
        DamageNoChange = 4
    }

    // Mirrors the source-reviewed native tool progression for one leased kit.
    // Repair and damage callbacks are invoked only after a server-authorized click.
    public sealed class DentRepairClickProgress
    {
        public const int ClicksBeforeDamage = 10;
        public const float SuccessfulRepairInterval = 1.5f;
        public const float DamageClickWindow = 3f;

        private ulong targetPanel;
        private int clicksUntilDamage = ClicksBeforeDamage;
        private float lastSuccessfulRepair;
        private float lastDamageClick;

        public ulong TargetPanel => targetPanel;
        public int ClicksUntilDamage => clicksUntilDamage;

        public DentRepairClickResult Click(ulong panel, float now, bool canDent,
            Func<bool> repairDent, Func<bool> damageDent)
        {
            if (panel == 0 || !Finite(now) || now < 0 || repairDent == null || damageDent == null)
                return DentRepairClickResult.Rejected;
            if (now < lastSuccessfulRepair + SuccessfulRepairInterval)
                return DentRepairClickResult.Rejected;

            if (targetPanel != panel)
            {
                targetPanel = panel;
                clicksUntilDamage = ClicksBeforeDamage;
            }
            if (!canDent) return DentRepairClickResult.NoChange;

            if (clicksUntilDamage > 0 && repairDent())
            {
                lastSuccessfulRepair = now;
                return DentRepairClickResult.Repaired;
            }

            if (lastDamageClick + DamageClickWindow > now)
            {
                clicksUntilDamage--;
                if (clicksUntilDamage <= 0)
                {
                    bool changed = damageDent();
                    lastDamageClick = now;
                    return changed ? DentRepairClickResult.Damaged : DentRepairClickResult.DamageNoChange;
                }
                return DentRepairClickResult.NoChange;
            }

            lastDamageClick = now;
            clicksUntilDamage = ClicksBeforeDamage;
            return DentRepairClickResult.NoChange;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
