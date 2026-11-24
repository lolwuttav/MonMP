using System;
using System.Globalization;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Fixed, host-authored world announcements. No arbitrary text, rewards or gameplay
    // callbacks cross the wire; each receiver formats its own native localization.
    internal static class SharedWorldNotices
    {
        private enum Notice : byte { FederationBarrel = 1, IslandBarrel, BulkSyrup, BunkerBuilt, DealerUnlocked }
        private const int MaximumSummary = 1000000;
        internal static bool Playing { get; private set; }

        internal static bool Matches(string text, string name) => PresentationReplays.Events?.Live == true && TryCapture(text, name, out _, out _);

        internal static void NativeMessage(string text, string name)
        {
            var events = PresentationReplays.Events;
            if (Playing || SharedPresentationEvents.Replaying || events?.IsHost != true ||
                !TryCapture(text, name, out byte kind, out ulong summary)) return;
            // Native remote actions suppress their UI on the host. This announcement belongs
            // to everyone, so restore it locally too without rerunning the native action.
            events.HostAnnounce(PresentationKind.WorldNotice, kind, summary, Vector3.zero, 0, 0,
                playHere: Patches.RemoteActionScope.Active);
        }

        private static bool TryCapture(string text, string name, out byte kind, out ulong summary)
        {
            kind = 0; summary = 0;
            if (string.IsNullOrEmpty(text)) return false;
            if (name == TEXT.GetText("SyrupFederation_Name"))
            {
                if (text == TEXT.GetText("ERROR_FederationSyrup_BarrelSuccess")) kind = (byte)Notice.FederationBarrel;
                else
                {
                    string suffix = "\n\n" + TEXT.GetText("ERROR_FederationSyrup_BedTankSuccess");
                    if (!text.EndsWith(suffix, StringComparison.Ordinal)) return false;
                    string prefix = text.Substring(0, text.Length - suffix.Length);
                    int split = prefix.IndexOf("L = ", StringComparison.Ordinal);
                    if (split <= 0 || !prefix.EndsWith("$", StringComparison.Ordinal) ||
                        !int.TryParse(prefix.Substring(0, split), NumberStyles.Integer, CultureInfo.CurrentCulture, out int litres) ||
                        !int.TryParse(prefix.Substring(split + 4, prefix.Length - split - 5), NumberStyles.Integer, CultureInfo.CurrentCulture, out int dollars) ||
                        litres < 0 || litres > MaximumSummary || dollars < 0 || dollars > MaximumSummary) return false;
                    kind = (byte)Notice.BulkSyrup;
                    summary = (ulong)(uint)litres << 32 | (uint)dollars;
                }
            }
            else if (name == TEXT.GetText("IslandHouses_Name") && text == TEXT.GetText("ERROR_IslandHouses_BarrelSuccess"))
                kind = (byte)Notice.IslandBarrel;
            else if (name == TEXT.GetText("Dealers_Name"))
            {
                if (text == TEXT.GetText("BUNKER_BuildCompleted")) kind = (byte)Notice.BunkerBuilt;
                else if (text == TEXT.GetText("SUCCESS_Dealer_Unlocked")) kind = (byte)Notice.DealerUnlocked;
            }
            return kind != 0;
        }

        internal static void Play(PresentationEvent e)
        {
            if (e.Kind != (byte)PresentationKind.WorldNotice || e.Value != 0 || e.X != 0 || e.Y != 0 || e.Z != 0) return;
            string text, name; float seconds = 14f;
            var icon = UI_EventMessage.EventIconType.Info;
            switch ((Notice)e.Variant)
            {
                case Notice.FederationBarrel:
                    text = TEXT.GetText("ERROR_FederationSyrup_BarrelSuccess"); name = TEXT.GetText("SyrupFederation_Name"); break;
                case Notice.IslandBarrel:
                    text = TEXT.GetText("ERROR_IslandHouses_BarrelSuccess"); name = TEXT.GetText("IslandHouses_Name"); break;
                case Notice.BulkSyrup:
                    uint litres = (uint)(e.Target >> 32), dollars = (uint)e.Target;
                    if (litres > MaximumSummary || dollars > MaximumSummary) return;
                    text = litres + "L = " + dollars + "$\n\n" + TEXT.GetText("ERROR_FederationSyrup_BedTankSuccess");
                    name = TEXT.GetText("SyrupFederation_Name"); break;
                case Notice.BunkerBuilt:
                    text = TEXT.GetText("BUNKER_BuildCompleted"); name = TEXT.GetText("Dealers_Name"); seconds = 16f;
                    icon = UI_EventMessage.EventIconType.Talk; break;
                case Notice.DealerUnlocked:
                    text = TEXT.GetText("SUCCESS_Dealer_Unlocked"); name = TEXT.GetText("Dealers_Name"); break;
                default: return;
            }
            if ((Notice)e.Variant != Notice.BulkSyrup && e.Target != 0) return;
            Playing = true;
            bool previous = Patches.RemoteActionScope.SharedNotice;
            Patches.RemoteActionScope.SharedNotice = true;
            try { Singleton<UI_EventMessage>.i?.Show(text, name, seconds, icon); }
            finally { Patches.RemoteActionScope.SharedNotice = previous; Playing = false; }
        }
    }
}
