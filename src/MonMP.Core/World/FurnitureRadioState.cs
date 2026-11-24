using System;
using System.Buffers.Binary;
using System.IO;

namespace MonMP.Core.World
{
    // One native Furniture_Radio (house, vehicle or furniture radio). The host owns power, band,
    // station and volume; guests send button intents. Records are dynamic so radios that exist only
    // in a host's played save (bought furniture, fleet vehicles) never change the join catalog.
    public readonly struct FurnitureRadioState : IEquatable<FurnitureRadioState>
    {
        public const ushort Adapter = 90;
        public const byte Version = 2;
        public const int Size = 9;
        public readonly bool On;
        public readonly byte Mode; // 0 FM, 1 CD (each machine plays its own CD folder)
        public readonly ushort StationTenths;
        public readonly ushort VolumeThousandths;
        // Native startup uses the displayed volume directly; its volume buttons use 0.3 times it.
        // Carry the audible gain separately so late joins preserve either native state.
        public readonly ushort SpeakerVolumeThousandths;

        public FurnitureRadioState(bool on, byte mode, ushort stationTenths, ushort volumeThousandths)
            : this(on, mode, stationTenths, volumeThousandths, (ushort)Math.Round(volumeThousandths * 0.3)) { }

        public FurnitureRadioState(bool on, byte mode, ushort stationTenths, ushort volumeThousandths, ushort speakerVolumeThousandths)
        {
            On = on; Mode = mode; StationTenths = stationTenths; VolumeThousandths = volumeThousandths;
            SpeakerVolumeThousandths = speakerVolumeThousandths;
            Validate();
        }
        private void Validate()
        {
            if (Mode > 1 || StationTenths > 2000 || VolumeThousandths > 1000 || SpeakerVolumeThousandths > 1000) throw new InvalidDataException("Invalid radio state.");
        }
        public static ulong EntityFor(string identity) => StaticId.Hash("radio/furniture/v1/" + identity) | WorldRecord.DynamicIdStart;
        public byte[] Encode()
        {
            Validate();
            var payload = new byte[Size];
            payload[0] = Version; payload[1] = (byte)(On ? 1 : 0); payload[2] = Mode;
            BitConverter.GetBytes(StationTenths).CopyTo(payload, 3);
            BitConverter.GetBytes(VolumeThousandths).CopyTo(payload, 5);
            BitConverter.GetBytes(SpeakerVolumeThousandths).CopyTo(payload, 7);
            return payload;
        }
        public static FurnitureRadioState Decode(byte[] payload)
        {
            if (payload == null) throw new InvalidDataException("Invalid radio state.");
            return Decode(payload.AsSpan());
        }
        public static FurnitureRadioState Decode(ReadOnlySpan<byte> payload)
        {
            if (payload.Length != Size || payload[0] != Version || payload[1] > 1) throw new InvalidDataException("Invalid radio state.");
            return new FurnitureRadioState(payload[1] == 1, payload[2], BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(3)),
                BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(5)), BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(7)));
        }
        public bool Equals(FurnitureRadioState other) => On == other.On && Mode == other.Mode && StationTenths == other.StationTenths && VolumeThousandths == other.VolumeThousandths && SpeakerVolumeThousandths == other.SpeakerVolumeThousandths;
        public override bool Equals(object? obj) => obj is FurnitureRadioState other && Equals(other);
        public override int GetHashCode() => ((On ? 1 : 0) | Mode << 1 | StationTenths << 2 ^ VolumeThousandths << 16) ^ SpeakerVolumeThousandths * 397;
    }

    public enum FurnitureRadioButton : byte { Power = 1, Volume = 2, Preset = 3, Band = 4, SeekNext = 5, SeekPrevious = 6 }

    // A guest's radio button press, addressed to the radio's entity.
    public readonly struct FurnitureRadioCommand
    {
        public const byte Marker = 0xC4;
        public const int Size = 6;
        public readonly FurnitureRadioButton Button;
        public readonly float Value;
        public FurnitureRadioCommand(FurnitureRadioButton button, float value)
        {
            Button = button; Value = value;
            Validate();
        }
        private void Validate()
        {
            if (!Enum.IsDefined(typeof(FurnitureRadioButton), Button) || float.IsNaN(Value) || float.IsInfinity(Value) || Math.Abs(Value) > 100)
                throw new InvalidDataException("Invalid radio command.");
            if (Button == FurnitureRadioButton.Preset && (Value < 1 || Value > 6 || Value != Math.Floor(Value)))
                throw new InvalidDataException("Invalid radio preset.");
        }
        public byte[] Encode()
        {
            Validate();
            var payload = new byte[Size];
            payload[0] = Marker; payload[1] = (byte)Button;
            BitConverter.GetBytes(Value).CopyTo(payload, 2);
            return payload;
        }
        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public static FurnitureRadioCommand Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid radio command.");
            return new FurnitureRadioCommand((FurnitureRadioButton)payload[1], BitConverter.ToSingle(payload, 2));
        }
    }

    // The native radio stations are a shared broadcast: each station plays one song at one time
    // offset for every radio tuned to it. The host's random song choice and position are replicated.
    public sealed class RadioStationClockState
    {
        public const ushort Adapter = 91;
        public const byte Version = 2;
        public const int Stations = 7;
        public const int Size = 1 + Stations * 6 + 4;
        public static readonly ulong Entity = StaticId.Hash("radio/stations/v1") | WorldRecord.DynamicIdStart;
        public readonly short[] Songs;
        public readonly float[] Times;
        public readonly int CdSongCount;
        public RadioStationClockState(short[] songs, float[] times, int cdSongCount = 0)
        {
            if (songs == null || times == null || songs.Length != Stations || times.Length != Stations || cdSongCount < 0) throw new InvalidDataException("Invalid radio station clock.");
            for (int i = 0; i < Stations; i++)
                if (songs[i] < -1 || float.IsNaN(times[i]) || float.IsInfinity(times[i]) || times[i] < -1 || times[i] > 36000)
                    throw new InvalidDataException("Invalid radio station clock.");
            Songs = (short[])songs.Clone(); Times = (float[])times.Clone(); CdSongCount = cdSongCount;
        }
        public byte[] Encode()
        {
            var payload = new byte[Size];
            payload[0] = Version;
            for (int i = 0; i < Stations; i++)
            {
                BitConverter.GetBytes(Songs[i]).CopyTo(payload, 1 + i * 6);
                BitConverter.GetBytes(Times[i]).CopyTo(payload, 3 + i * 6);
            }
            BitConverter.GetBytes(CdSongCount).CopyTo(payload, 1 + Stations * 6);
            return payload;
        }
        public static RadioStationClockState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size || payload[0] != Version) throw new InvalidDataException("Invalid radio station clock.");
            var songs = new short[Stations]; var times = new float[Stations];
            for (int i = 0; i < Stations; i++) { songs[i] = BitConverter.ToInt16(payload, 1 + i * 6); times[i] = BitConverter.ToSingle(payload, 3 + i * 6); }
            return new RadioStationClockState(songs, times, BitConverter.ToInt32(payload, 1 + Stations * 6));
        }
    }
}
