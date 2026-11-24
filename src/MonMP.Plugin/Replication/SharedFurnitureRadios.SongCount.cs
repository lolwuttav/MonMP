using TMPro;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedFurnitureRadios
    {
        private TextMeshProUGUI? songCountLabel;
        private int? sharedCdSongCount;
        private int displayedCdSongCount = -1;
        private string displayedCdSongText = "";
        private readonly CdTransferRates cdTransferRates = new CdTransferRates();
        private readonly CdTransferIndicator cdTransferIndicator = new CdTransferIndicator();

        private int? rebuildingCdSongCount;
        private int nativeCdLoads;
        private bool rebuildingCdList;
        internal void NativeCdPlaylistRebuilding()
        {
            if (!Active || !host) return;
            rebuildingCdSongCount = NativeCdSongCount;
            rebuildingCdList = true;
        }
        internal System.Action? BeginNativeCdTrackLoad()
        {
            if (!Active || !host || !rebuildingCdSongCount.HasValue) return null;
            nativeCdLoads++;
            bool finished = false;
            return () =>
            {
                if (finished) return;
                finished = true;
                nativeCdLoads--;
                FinishNativeCdRebuild();
            };
        }
        private void FinishNativeCdRebuild()
        {
            if (!rebuildingCdList && nativeCdLoads == 0) rebuildingCdSongCount = null;
            RefreshCdSongCount();
        }
        private int NativeCdSongCount
        {
            get
            {
                var manager = Singleton<SOUND_Manager>.i;
                int count = manager == null ? 0 : RadioCdNative.CdPlayer(manager)?.Count ?? 0;
                return rebuildingCdSongCount.HasValue ? System.Math.Max(count, rebuildingCdSongCount.Value) : count;
            }
        }
        // Only the pause-menu label uses the shared count. Native playback/indexing
        // must continue to use the local clips, which may still be downloading.
        internal void BindNativeCdMenu()
        {
            if (NativeCdMenuBinding.Current is TextMeshProUGUI label) ShowCdSongCount(label);
        }
        internal void ShowCdSongCount(TextMeshProUGUI label)
        {
            songCountLabel = label;
            cdTransferIndicator.Bind(label);
            cdTransferIndicator.Show(Active ? cdTransferRates.Text : null);
            RefreshCdSongCount();
        }
        private void SetCdSongCount(int count)
        {
            sharedCdSongCount = count;
            RefreshCdSongCount();
        }
        private void RefreshCdSongCount()
        {
            if (songCountLabel == null) return;
            int count = Active && !host && sharedCdSongCount.HasValue ? sharedCdSongCount.Value : NativeCdSongCount;
            if (displayedCdSongCount != count)
            {
                displayedCdSongCount = count;
                displayedCdSongText = count.ToString();
            }
            if (songCountLabel.text != displayedCdSongText) songCountLabel.text = displayedCdSongText;
        }
        private void ClearCdSongCount()
        {
            cdTransferIndicator.Dispose(); cdTransferRates.Reset();
            sharedCdSongCount = null;
            rebuildingCdSongCount = null; rebuildingCdList = false;
            RefreshCdSongCount();
            songCountLabel = null;
        }
    }
}
