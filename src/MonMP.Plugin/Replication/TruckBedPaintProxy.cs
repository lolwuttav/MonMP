using System;
using System.Reflection;

namespace MonMP.Plugin.Replication
{
    // Update_VehicleParts redirects these native Seat_Rear hit proxies to the
    // installed bed addon's paint owner and clears that parent on removal. They
    // are never independent body paint save units, even when the socket is empty.
    internal static class TruckBedPaintProxy
    {
        private static readonly FieldInfo Left = Field("OlTruck_Socket_CamperShell_Colliders_L");
        private static readonly FieldInfo Right = Field("OlTruck_Socket_CamperShell_Colliders_R");

        private static FieldInfo Field(string name) => typeof(Vehicle_PartsManager).GetField(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Unsupported truck bed paint proxy field: " + name);

        internal static bool Contains(Vehicle_PaintablePanel panel)
        {
            var manager = panel.GetComponentInParent<Vehicle_PartsManager>(true);
            return manager != null && ((Vehicle_PaintablePanel?)Left.GetValue(manager) == panel ||
                (Vehicle_PaintablePanel?)Right.GetValue(manager) == panel);
        }
    }
}
