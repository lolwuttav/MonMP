using System;
using System.Linq;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedWorld
    {
        // Only ownership that must be relinquished for the native checkpoint belongs
        // in the save barrier. Session teardown continues to use the complete Owners.
        internal uint[] SaveOwners => Items.Owners.Concat(BoatMotion.Owners)
            .Concat(ExtraSeats.SaveOwners).Concat(FurnitureSeats.Owners)
            .Concat(SceneAirEquipment.Owners).Concat(Bolts.Owners).Concat(Creepers.Owners).Concat(Wheelbarrows.Owners)
            .Distinct().ToArray();

        private bool HasLocalSaveVehicleSeat => Vehicles.HasLocalSaveSeat || ExtraSeats.HasLocalSaveSeat ||
            SchoolBusPassengers.HasLocalSaveSeat || MartinCar.HasLocalSaveSeat;

        internal bool LocalSaveFlushComplete => Items.LocalFlushComplete &&
            Vehicles.LocalSaveFlushComplete(HasLocalSaveVehicleSeat) && BoatMotion.LocalFlushComplete &&
            SchoolBusPassengers.LocalSaveFlushComplete(HasLocalSaveVehicleSeat) && MartinCar.LocalSaveFlushComplete && ExtraSeats.LocalSaveFlushComplete &&
            FurnitureSeats.LocalFlushComplete && Creepers.LocalFlushComplete && Wheelbarrows.LocalFlushComplete &&
            SceneAirEquipment.LocalFlushComplete && Bolts.LocalFlushComplete && VehicleParts.LocalFlushComplete &&
            Doors.TransactionsSettled && NpcInteractions.TransactionsSettled && Statistics.LocalFlushComplete;

        private void FlushForSave()
        {
            SceneAirEquipment.FlushLocalOwnership(); Bolts.FlushLocalOwnership(); Items.FlushLocalOwnership();
            Creepers.FlushLocalOwnership(); Wheelbarrows.FlushLocalOwnership(); ExtraSeats.FlushForSave();
            FurnitureSeats.FlushLocalOwnership(); Vehicles.FlushForSave(HasLocalSaveVehicleSeat);
            BoatMotion.FlushLocalOwnership(); SchoolBusPassengers.FlushForSave(); MartinCar.FlushForSave();
        }

        private void RevokeForSave(uint peer)
        {
            foreach (var release in new Action<uint>[] { Items.RevokeOwnership, BoatMotion.RevokeOwnership, ExtraSeats.RevokeForSave,
                FurnitureSeats.RevokeOwnership, SceneAirEquipment.RevokeOwnership,
                Bolts.RevokeOwnership, Creepers.RevokeOwnership, Wheelbarrows.RevokeOwnership })
                SaveStep(() => release(peer));
        }
    }
}
