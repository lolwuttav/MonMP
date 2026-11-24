using System.IO;

namespace MonMP.Core.Protocol
{
    // Host-owned session rules, sent reliably before a guest's world snapshot.
    public sealed class SessionSettingsState
    {
        public bool ShowPlayersOnMap { get; }
        public bool TruckExtraSeatsEnabled { get; }
        public bool FreezeVehicleCargo { get; }

        public SessionSettingsState(bool showPlayersOnMap = true, bool truckExtraSeatsEnabled = true, bool freezeVehicleCargo = false)
        { ShowPlayersOnMap = showPlayersOnMap; TruckExtraSeatsEnabled = truckExtraSeatsEnabled; FreezeVehicleCargo = freezeVehicleCargo; }

        public void Write(BinaryWriter writer) { writer.Write(ShowPlayersOnMap); writer.Write(TruckExtraSeatsEnabled); writer.Write(FreezeVehicleCargo); }

        public static SessionSettingsState Read(BinaryReader reader)
        {
            byte showPlayers = reader.ReadByte();
            if (showPlayers > 1) throw new InvalidDataException("Invalid map player visibility setting.");
            byte extraSeats = reader.ReadByte();
            if (extraSeats > 1) throw new InvalidDataException("Invalid truck extra-seat setting.");
            byte freezeCargo = reader.ReadByte();
            if (freezeCargo > 1) throw new InvalidDataException("Invalid cargo-freezing setting.");
            return new SessionSettingsState(showPlayers == 1, extraSeats == 1, freezeCargo == 1);
        }
    }
}
