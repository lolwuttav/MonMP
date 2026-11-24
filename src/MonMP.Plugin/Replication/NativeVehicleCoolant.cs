using HarmonyLib;
using MonMP.Core.World;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.Modules.Coolant;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class NativeVehicleCoolant
    {
        private static readonly AccessTools.FieldRef<CoolantModule, float> EngineBlockTemp =
            AccessTools.FieldRefAccess<CoolantModule, float>("Temp_EngineBlock");
        private static readonly AccessTools.FieldRef<CoolantModule, float> RadiatorTemp =
            AccessTools.FieldRefAccess<CoolantModule, float>("Temp_Radiator");

        internal static float RadiatorHeat(CoolantModule coolant) => RadiatorTemp(coolant) / 180f;

        internal static void Apply(CoolantModule coolant, VehicleController vehicle, VehicleEffects effects)
        {
            float block = Mathf.Max(5f, effects.EngineHeat * 180f);
            EngineBlockTemp(coolant) = block;
            RadiatorTemp(coolant) = Mathf.Max(5f, effects.RadiatorHeat * 180f);
            vehicle.isCoolantOverheating = block >= 105f;
            vehicle.isCoolantBoiling = effects.CoolantBoiling;
        }
    }
}
