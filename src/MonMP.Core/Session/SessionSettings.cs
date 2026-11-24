using System;
using System.IO;
using MonMP.Core.Protocol;

namespace MonMP.Core.Session
{
    public sealed partial class NetworkSession
    {
        public SessionSettingsState Settings { get; private set; } = new SessionSettingsState();
        public event Action<SessionSettingsState>? SettingsChanged;

        public void SetShowPlayersOnMap(bool show)
        {
            if (!IsHost) throw new InvalidOperationException("Only the host can change session settings.");
            if (!ApplySessionSettings(new SessionSettingsState(show, Settings.TruckExtraSeatsEnabled, Settings.FreezeVehicleCargo))) return;
            Broadcast(Wire.Encode(MessageKind.SessionSettings, Epoch, Settings.Write), true, worldOnly: true);
        }

        public void SetTruckExtraSeatsEnabled(bool enabled)
        {
            if (!IsHost) throw new InvalidOperationException("Only the host can change session settings.");
            if (!ApplySessionSettings(new SessionSettingsState(Settings.ShowPlayersOnMap, enabled, Settings.FreezeVehicleCargo))) return;
            Broadcast(Wire.Encode(MessageKind.SessionSettings, Epoch, Settings.Write), true, worldOnly: true);
        }

        public void SetFreezeVehicleCargo(bool enabled)
        {
            if (!IsHost) throw new InvalidOperationException("Only the host can change session settings.");
            if (!ApplySessionSettings(new SessionSettingsState(Settings.ShowPlayersOnMap, Settings.TruckExtraSeatsEnabled, enabled))) return;
            Broadcast(Wire.Encode(MessageKind.SessionSettings, Epoch, Settings.Write), true, worldOnly: true);
        }

        private bool ApplySessionSettings(SessionSettingsState state)
        {
            if (Settings.ShowPlayersOnMap == state.ShowPlayersOnMap && Settings.TruckExtraSeatsEnabled == state.TruckExtraSeatsEnabled &&
                Settings.FreezeVehicleCargo == state.FreezeVehicleCargo) return false;
            Settings = state;
            SettingsChanged?.Invoke(state);
            return true;
        }

        private void SendSessionSettings(int connection) =>
            transport!.Send(connection, Wire.Encode(MessageKind.SessionSettings, Epoch, Settings.Write), true);

        private void ReceiveSessionSettings(Packet packet)
        {
            if (IsHost || !IsReady) throw new InvalidDataException("Only a ready guest can receive host session settings.");
            var state = SessionSettingsState.Read(packet.Reader);
            packet.Complete();
            ApplySessionSettings(state);
        }
    }
}
