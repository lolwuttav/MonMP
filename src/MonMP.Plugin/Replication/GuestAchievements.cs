using System;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Native achievements granted inside host-run methods never reach the guest whose action
    // (or shared world event) earned them; these calls grant them locally instead.
    internal static class GuestAchievements
    {
        internal static void Award(AchievementType type)
        {
            try { Singleton<Steam_AchievementManager>.i?.AchievementDone(type); }
            catch (Exception ex) { Debug.LogWarning("MonMP " + type + " achievement failed: " + ex.Message); }
        }

        // Native Cash_Manager.TryToBuy grants this for the VIP goat statue.
        internal static void Purchased(ItemForSale_Display display)
        {
            try
            {
                if (display.Get_RealObjectToBuy?.GetComponent<InteractableItems>()?.PrefabName == "Deco/Goat_Sculpture")
                    Award(AchievementType.VIPRoomBuyTheGoatStatue);
            }
            catch (Exception ex) { Debug.LogWarning("MonMP goat statue achievement check failed: " + ex.Message); }
        }

        internal static void AwardToDo(ToDoList_Line line)
        {
            try { Singleton<Steam_AchievementManager>.i?.AchievementDone_ToDoList(line); }
            catch (Exception ex) { Debug.LogWarning("MonMP to-do achievement failed: " + ex.Message); }
        }
    }
}
