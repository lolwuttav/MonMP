using System;
using TMPro;

namespace MonMP.Plugin.Replication
{
    // The host opens this native panel before creating its session/world. Remember
    // that exact label weakly so Attach can bind without reopening or scene searches.
    internal static class NativeCdMenuBinding
    {
        private static WeakReference<TextMeshProUGUI>? current;
        internal static void Remember(TextMeshProUGUI label) => current = new WeakReference<TextMeshProUGUI>(label);
        internal static TextMeshProUGUI? Current => current != null && current.TryGetTarget(out var label) && label != null ? label : null;
    }
}
