using System.Collections.Generic;
using MonMP.Core.Protocol;
using MonMP.Core.World;
using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class RadioCdNative
    {
        internal static readonly CdTrackSourceMap<AudioClip> TrackSources = new CdTrackSourceMap<AudioClip>();
        private static readonly AccessTools.FieldRef<SOUND_Manager, List<AudioClip>> CdPlayerField =
            AccessTools.FieldRefAccess<SOUND_Manager, List<AudioClip>>("CDPlayer");
        private static readonly AccessTools.FieldRef<SOUND_Manager, List<SOUND_Radio>> PlayingRadiosField =
            AccessTools.FieldRefAccess<SOUND_Manager, List<SOUND_Radio>>("RadioPlaying");
        private static readonly AccessTools.FieldRef<SOUND_Radio, int> CdSongField =
            AccessTools.FieldRefAccess<SOUND_Radio, int>("CurrentCDSong");
        private static readonly AccessTools.FieldRef<SOUND_Radio, float> CdTimeField =
            AccessTools.FieldRefAccess<SOUND_Radio, float>("CurrentCDTime");
        private static readonly AccessTools.FieldRef<SOUND_Radio, int> RadioModeField =
            AccessTools.FieldRefAccess<SOUND_Radio, int>("FMCD");
        private static readonly System.Reflection.MethodInfo PlayCurrentStation =
            AccessTools.Method(typeof(SOUND_Manager), "PlayStationOnThisRadio", new[] { typeof(SOUND_Radio) })!;

        internal static List<AudioClip>? CdPlayer(SOUND_Manager manager) => manager == null ? null : CdPlayerField(manager);
        internal static List<SOUND_Radio>? PlayingRadios(SOUND_Manager manager) => manager == null ? null : PlayingRadiosField(manager);
        internal static int CdSong(SOUND_Radio sound) => sound == null ? -1 : CdSongField(sound);
        internal static float CdTime(SOUND_Radio sound) => sound == null ? 0 : CdTimeField(sound);
        internal static int RadioMode(SOUND_Radio sound) => sound == null ? 0 : RadioModeField(sound);
        internal static void SetCdSong(SOUND_Radio sound, int value) => CdSongField(sound) = value;
        internal static void SetCdTime(SOUND_Radio sound, float value) => CdTimeField(sound) = value;
        internal static void SetRadioMode(SOUND_Radio sound, int value) => RadioModeField(sound) = value;
        internal static void Play(SOUND_Manager manager, SOUND_Radio sound)
        { if (manager != null && sound != null) PlayCurrentStation.Invoke(manager, new object[] { sound }); }

        internal static void ResumeLoadedTrack(SOUND_Manager manager, AudioClip clip)
        {
            var tracks = CdPlayer(manager);
            var playing = PlayingRadios(manager);
            if (tracks == null || playing == null || clip == null) return;
            foreach (var sound in playing)
            {
                if (sound == null || RadioMode(sound) != 1 || sound.Speakers == null) continue;
                int index = CdSong(sound);
                if (index < 0 || index >= tracks.Count || tracks[index] != clip) continue;
                // Native import never revisits an already-on CD radio when its
                // asynchronous clip arrives. Start this newly available selection
                // once, preserving every other radio and already-playing track.
                bool stale = false;
                foreach (var speaker in sound.Speakers)
                    if (speaker != null && speaker.clip != clip) { stale = true; break; }
                if (!stale) continue;
                SetCdTime(sound, 0);
                Play(manager, sound);
            }
        }

        internal static AudioType FormatToAudioType(CdMediaFormat format) => format switch
        {
            CdMediaFormat.Wav => AudioType.WAV,
            CdMediaFormat.Mp3 => AudioType.MPEG,
            CdMediaFormat.Ogg => AudioType.OGGVORBIS,
            _ => AudioType.UNKNOWN
        };
        internal static CdMediaFormat FormatFromPath(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".wav" => CdMediaFormat.Wav,
            ".mp3" => CdMediaFormat.Mp3,
            ".ogg" => CdMediaFormat.Ogg,
            _ => 0
        };
    }
}
