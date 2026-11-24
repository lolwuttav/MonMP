using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(MainMenu_Manager), "CloseOpenWindows")]
    internal static class ChatMenuInputPatch
    {

#if MONMP_DEVELOPMENT
        internal static int TestBlockedCallbacks { get; private set; }
#endif


        private static bool Prefix()
        {
            // The menu owns a separate input asset; its stale Escape callback must not handle chat (#161).
            if (MonMPPlugin.Instance?.Chat?.BlocksInput != true && MonMPPlugin.Instance?.HoldPositionEditorOpen != true) return true;

#if MONMP_DEVELOPMENT
            TestBlockedCallbacks++;
#endif

            return false;
        }
    }
}
