using System;
using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    // While the host runs a native action on a guest's behalf, that action's on-screen messages belong
    // to the guest (who receives them through its reply), not to the host's screen.
    internal static class RemoteActionScope
    {
        private static int depth;
        internal static bool Active => depth > 0;
        internal static bool SharedNotice { get; set; }
        // The last native error the scope kept off the host's screen, for a reply to the guest.
        internal static string? LastError { get; set; }
        internal static IDisposable Enter() { depth++; return new Exit(); }
        private sealed class Exit : IDisposable
        {
            private bool done;
            public void Dispose() { if (!done) { done = true; depth--; } }
        }
    }

    [HarmonyPatch(typeof(UI_EventMessage), nameof(UI_EventMessage.Show), new[] { typeof(string), typeof(string), typeof(float), typeof(UI_EventMessage.EventIconType) })]
    internal static class RemoteActionEventMessagePatch
    {
        private static bool Prefix(string _text) => MonMPPlugin.Instance?.Cheats?.CaptureFeedback(_text, false) != true &&
            (!RemoteActionScope.Active || RemoteActionScope.SharedNotice);
    }

    [HarmonyPatch(typeof(UI_EventMessage), nameof(UI_EventMessage.Show), new[] { typeof(UnityEngine.Color), typeof(string), typeof(string), typeof(float), typeof(UI_EventMessage.EventIconType) })]
    internal static class RemoteActionColoredEventMessagePatch
    {
        private static bool Prefix(string _text) => MonMPPlugin.Instance?.Cheats?.CaptureFeedback(_text, false) != true && !RemoteActionScope.Active;
    }

    [HarmonyPatch(typeof(UI_ErrorMessage), nameof(UI_ErrorMessage.Show))]
    internal static class RemoteActionErrorMessagePatch
    {
        private static bool Prefix(string _message)
        {
            if (MonMPPlugin.Instance?.Cheats?.CaptureFeedback(_message, true) == true) return false;
            if (!RemoteActionScope.Active) return true;
            RemoteActionScope.LastError = _message;
            return false;
        }
    }
}
