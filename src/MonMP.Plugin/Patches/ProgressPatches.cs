using HarmonyLib;
namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(ToDoList_Manager), "ToDoListCompleted")]
    internal static class ProgressCompletionPatch
    {
        private static bool Prefix(ToDoList_Line todolist_Line)
        {
            if (MonMPPlugin.Instance?.World?.Progress.BlocksLocalChanges != true) return true;
            // Kinpin: the guest's own native dealer slide fills the crate, even when the host-owned line is long done.
            if (todolist_Line == ToDoList_Line.Dealer_SellHuge8PackInADay) Replication.GuestAchievements.AwardToDo(todolist_Line);
            return false;
        }
    }
    [HarmonyPatch(typeof(ToDoList_Manager), "CheckLineFromSaveSystem")]
    internal static class ProgressLoadPatch
    { private static bool Prefix() => MonMPPlugin.Instance?.World?.Progress.BlocksLocalChanges != true; }
    [HarmonyPatch(typeof(ToDoList_LineDetails), "CheckLine")]
    internal static class ProgressLinePatch
    {
        private static bool Prefix(ToDoList_LineDetails __instance)
        { var progress = MonMPPlugin.Instance?.World?.Progress; return progress?.BlocksLocalChanges != true || !progress.Contains(__instance); }
    }
}
