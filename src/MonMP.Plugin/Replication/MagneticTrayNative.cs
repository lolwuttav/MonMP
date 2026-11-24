using System;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // Narrow native bridge for Tool_MagneticTray's private 55-slot layout. The host continues to
    // call AddBolt for gameplay; replicas restore a host-selected slot without running player input.
    internal static class MagneticTrayNative
    {
        internal const int Capacity = 55;
        private static readonly System.Reflection.FieldInfo BoltsInTray = AccessTools.Field(typeof(Tool_MagneticTray), "BoltsInTray");
        private static readonly System.Reflection.FieldInfo BoltLocation0 = AccessTools.Field(typeof(Tool_MagneticTray), "BoltLocation0");
        private static readonly System.Reflection.FieldInfo BoltTrayCooldown = AccessTools.Field(typeof(Hardware_Bolt), "LastTimePickFromMagneticTray");
        private static readonly System.Reflection.MethodInfo AddBolt = AccessTools.Method(typeof(Tool_MagneticTray), "AddBolt", new[] { typeof(Hardware_Bolt) });
        private static readonly System.Reflection.MethodInfo RemovedFromTray = AccessTools.Method(typeof(Hardware_Bolt), nameof(Hardware_Bolt.RemovedFromTray));

        internal static Hardware_Bolt[] Slots(Tool_MagneticTray tray) => BoltsInTray.GetValue(tray) as Hardware_Bolt[] ??
            throw new InvalidOperationException("Magnetic tray slot array is unavailable.");

        internal static int SlotOf(Tool_MagneticTray tray, Hardware_Bolt bolt)
        {
            var slots = Slots(tray);
            for (int i = 0; i < slots.Length; i++) if (ReferenceEquals(slots[i], bolt)) return i;
            return -1;
        }

        internal static bool HasRoom(Tool_MagneticTray tray)
        {
            var slots = Slots(tray);
            for (int i = 0; i < slots.Length; i++)
                if (slots[i] == null || slots[i].isBoltInMagneticTray != tray) return true;
            return false;
        }

        internal static void AddNative(Tool_MagneticTray tray, Hardware_Bolt bolt) => AddBolt.Invoke(tray, new object[] { bolt });

        internal static void AttachReplica(Tool_MagneticTray tray, Hardware_Bolt bolt, byte slot)
        {
            if (slot >= Capacity) throw new InvalidOperationException("Invalid magnetic tray slot.");
            var slots = Slots(tray);
            if (slots[slot] != null && !ReferenceEquals(slots[slot], bolt) && slots[slot].isBoltInMagneticTray == tray)
                throw new InvalidOperationException("Magnetic tray slot is already occupied.");
            if (bolt.isBoltInMagneticTray != null && bolt.isBoltInMagneticTray != tray)
                Detach(bolt);
            for (int i = 0; i < slots.Length; i++) if (ReferenceEquals(slots[i], bolt) && i != slot) slots[i] = null!;

            bolt.isBoltInMagneticTray = tray;
            if (bolt.rb != null) { Object.Destroy(bolt.rb); bolt.rb = null; }
            if (bolt.itemCollider == null) bolt.itemCollider = bolt.GetComponent<Collider>();
            bolt.itemCollider.isTrigger = true;
            slots[slot] = bolt;
            var location = BoltLocation0.GetValue(tray) as Transform ?? throw new InvalidOperationException("Magnetic tray bolt origin is unavailable.");
            var offset = new Vector3(-(slot % 5) * 0.041826f, 0, -(int)Math.Floor(slot / 5f) * 0.041826f);
            bolt.transform.SetParent(tray.transform);
            bolt.transform.localPosition = location.localPosition + offset;
            bolt.transform.localRotation = location.localRotation;
            var player = Singleton<Gameplay>.i.Player;
            player.CheckIfHoldingItemToCancel(bolt);
            var toolType = player.Get_ActiveToolType;
            bolt.gameObject.layer = tray.isEquiped || toolType == ToolsType.AdjustableSpanner || toolType == ToolsType.Air_ImpactGun
                ? LayerMask.NameToLayer("3DObjectInFrontOfEverything") : LayerMask.NameToLayer("InteractableItem");
        }

        internal static void Detach(Hardware_Bolt bolt)
        {
            var tray = bolt.isBoltInMagneticTray;
            if (tray == null) tray = bolt.transform.parent?.GetComponent<Tool_MagneticTray>();
            if (tray != null)
            {
                var slots = Slots(tray);
                for (int i = 0; i < slots.Length; i++) if (ReferenceEquals(slots[i], bolt)) slots[i] = null!;
                if (bolt.isBoltInMagneticTray != null) RemovedFromTray.Invoke(bolt, null);
                else
                {
                    bolt.isBoltInMagneticTray = null;
                    BoltTrayCooldown.SetValue(bolt, Time.realtimeSinceStartup);
                    bolt.transform.SetParent(null, true);
                    if (bolt.rb == null) bolt.CreateBasics();
                    bolt.UnGrab();
                }
            }
            else
            {
                if (bolt.transform.parent != null) bolt.transform.SetParent(null, true);
                if (bolt.rb == null) bolt.CreateBasics();
                bolt.UnGrab();
            }
        }

        internal static void DetachAll(Tool_MagneticTray tray)
        {
            var slots = Slots(tray);
            for (int i = 0; i < slots.Length; i++)
            {
                var bolt = slots[i]; slots[i] = null!;
                if (bolt != null && bolt.isBoltInMagneticTray == tray) RemovedFromTray.Invoke(bolt, null);
            }
        }
    }
}
