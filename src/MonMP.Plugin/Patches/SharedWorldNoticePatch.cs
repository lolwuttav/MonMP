using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(UI_EventMessage), nameof(UI_EventMessage.Show), typeof(string), typeof(string), typeof(float), typeof(UI_EventMessage.EventIconType))]
    internal static class SharedWorldNoticePatch
    {
        // Also observes a native message suppressed by RemoteActionScope. Only the fixed
        // host-authoritative success notices are eligible; refusals stay with their actor.
        private static void Postfix(string _text, string _name) => SharedWorldNotices.NativeMessage(_text, _name);
    }
}
