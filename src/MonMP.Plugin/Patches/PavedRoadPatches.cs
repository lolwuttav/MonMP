using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    // A guest's Construction Turbo order goes to the host (#124); the host's shared ToDo state then
    // raises the road on every copy.
    [HarmonyPatch(typeof(ToDoList_Manager), nameof(ToDoList_Manager.PavedRoadsBought))]
    internal static class PavedRoadOrderPatch
    {
        private static bool Prefix(ToDoList_Line pavedDone) =>
            MonMPPlugin.Instance?.World?.Progress.RequestPaved(pavedDone) != true;
    }

    [HarmonyPatch(typeof(PavedRoads), nameof(PavedRoads.PavedRoadBought))]
    internal static class PavedRoadRaisePatch
    {
        private static bool Prefix(ToDoList_Line pavedBought)
        {
            var progress = MonMPPlugin.Instance?.World?.Progress;
            if (progress == null || progress.AllowPavedRaise) return true;
            progress.RequestPaved(pavedBought);
            return false;
        }
    }
}
