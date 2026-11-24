using System;
using System.IO;

namespace MonMP.Core.World
{
    public enum VehicleControlMethod : byte { MouseDown = 1, MouseRightDown, Use, MouseUp }

    // A walking or seated guest's click on a parked vehicle control. The host, which simulates the
    // vehicle while nobody drives it, runs the native control and replicates the result in vehicle state.
    public readonly struct VehicleControlCommand
    {
        public const byte Marker = 0x7C;
        // Dome mode controls use a separate index range so adding them cannot renumber existing controls.
        public const ushort DomeModeControlBase = 0x8000;
        public readonly ushort Control;
        public readonly VehicleControlMethod Method;

        public VehicleControlCommand(ushort control, VehicleControlMethod method)
        {
            if (!Enum.IsDefined(typeof(VehicleControlMethod), method)) throw new ArgumentException("Invalid vehicle control method.");
            Control = control; Method = method;
        }

        public static bool HasMarker(byte[]? payload) => payload != null && payload.Length > 0 && payload[0] == Marker;

        public byte[] Encode() => new[] { Marker, (byte)(Control & 0xFF), (byte)(Control >> 8), (byte)Method };

        public static VehicleControlCommand Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 4 || payload[0] != Marker || !Enum.IsDefined(typeof(VehicleControlMethod), payload[3]))
                throw new InvalidDataException("Invalid vehicle control command.");
            return new VehicleControlCommand((ushort)(payload[1] | payload[2] << 8), (VehicleControlMethod)payload[3]);
        }
    }
}
