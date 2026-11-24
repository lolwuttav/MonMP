using System.IO;

namespace MonMP.Core.World
{
    public static class ScrapyardRadioPolicy
    {
        public static bool AllowNativePickup(bool multiplayerRunning, bool isHost) => !multiplayerRunning || isHost;

        // Native NewDay, LoadAndUnlock and SpawnRadios mutate the save-backed slot bits.
        // Keep the game's own random selection and save loader on the host, and suppress
        // those callbacks on guests while they present the received host state.
        public static bool AllowNativeLifecycle(bool multiplayerRunning, bool isHost) => !multiplayerRunning || isHost;

        // Scrapyard_RadioSlot.isRemoved is written into each player's native save.
        // Shared copies may update presentation on guests, but only the host may write it.
        public static bool AllowSharedSlotMutation(bool isHost) => isHost;

        // Called after an authoritative native callback (pickup, NewDay respawn or save
        // load). A guest's local save is never a source of truth for these shared slots.
        public static ScrapyardRadioState AdoptNativeSlotState(bool isHost, ScrapyardRadioState canonical,
            ScrapyardRadioState native)
        {
            if (canonical == null) throw new System.ArgumentNullException(nameof(canonical));
            if (native == null) throw new System.ArgumentNullException(nameof(native));
            if (canonical.Count != native.Count) throw new InvalidDataException("Native scrapyard radio slot count changed.");
            return isHost ? native : canonical;
        }
    }
}
