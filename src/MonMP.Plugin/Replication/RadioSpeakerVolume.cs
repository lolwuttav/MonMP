using System;

namespace MonMP.Plugin.Replication
{
    internal static class RadioSpeakerVolume
    {
        internal static ushort Capture(SOUND_Radio sound)
        {
            float gain = sound.GetVolume * 0.3f;
            if (sound.Speakers != null)
                foreach (var speaker in sound.Speakers)
                    if (speaker != null) { gain = speaker.volume; break; }
            return (ushort)Math.Round(Math.Max(0f, Math.Min(1f, gain)) * 1000f);
        }

        internal static void Apply(SOUND_Radio sound, ushort thousandths)
        {
            if (sound.Speakers == null) return;
            foreach (var speaker in sound.Speakers)
                if (speaker != null) speaker.volume = thousandths / 1000f;
        }
    }
}
