using System;
using System.Collections.Generic;

namespace MonMP.Core.World
{
    // One radio request in flight, with a small ordered input buffer. World records may
    // include the in-flight press already, so only its exact receipt rebases the preview.
    public sealed class RadioPresentationIntent
    {
        public const int MaximumQueued = 16;
        public const float TimeoutSeconds = 8f;
        private readonly List<FurnitureRadioCommand> queued = new List<FurnitureRadioCommand>();
        private static readonly ushort[] Presets = { 1009, 1021, 1075, 989, 919, 1043 };
        private float sentAt;
        public ulong Token { get; private set; }
        public uint Revision { get; private set; }
        public FurnitureRadioState Authority { get; private set; }
        public FurnitureRadioState Presentation { get; private set; }
        public int Count => queued.Count;

        public void Observe(FurnitureRadioState state, uint revision)
        {
            if (revision < Revision) return;
            Authority = state; Revision = revision;
            if (queued.Count == 0) Presentation = state;
        }
        public void Reset(FurnitureRadioState state, uint revision)
        {
            queued.Clear(); Token = 0;
            Observe(state, revision); Presentation = Authority;
        }
        public bool Enqueue(FurnitureRadioCommand command, bool canPowerOn)
        {
            if (Revision == 0 || queued.Count >= MaximumQueued) return false;
            queued.Add(command); Presentation = Project(Presentation, command, canPowerOn); return true;
        }
        public bool TryBegin(ulong token, float now, out FurnitureRadioCommand command)
        {
            command = default;
            if (Token != 0 || token == 0 || queued.Count == 0) return false;
            Token = token; sentAt = now; command = queued[0]; return true;
        }
        public bool Acknowledge(ClientInteractionReceipt receipt, FurnitureRadioState state, bool canPowerOn)
        {
            if (Token == 0 || receipt.Token != Token) return false;
            Observe(state, receipt.Revision);
            queued.RemoveAt(0); Token = 0;
            if (receipt.Status != ClientInteractionStatus.Accepted) queued.Clear();
            Presentation = Authority;
            foreach (var command in queued) Presentation = Project(Presentation, command, canPowerOn);
            return true;
        }
        public bool Expire(float now)
        {
            if (Token == 0 || now >= sentAt && now - sentAt < TimeoutSeconds) return false;
            queued.Clear(); Token = 0; Presentation = Authority; return true;
        }

        public static FurnitureRadioState Project(FurnitureRadioState state, FurnitureRadioCommand command, bool canPowerOn)
        {
            bool on = state.On; byte mode = state.Mode;
            ushort station = state.StationTenths, volume = state.VolumeThousandths, gain = state.SpeakerVolumeThousandths;
            if (command.Button == FurnitureRadioButton.Power) on = on ? false : canPowerOn;
            else if (on)
            {
                switch (command.Button)
                {
                    case FurnitureRadioButton.Volume:
                        volume = (ushort)Math.Round(Math.Max(0, Math.Min(1000, volume + command.Value * 10f)));
                        gain = (ushort)Math.Round(volume * 0.3); break;
                    case FurnitureRadioButton.Band: mode = (byte)(mode == 0 ? 1 : 0); break;
                    case FurnitureRadioButton.Preset:
                        if (mode == 0) station = Presets[(int)command.Value - 1];
                        break;
                }
            }
            // Seeks retain native delayed tuning/track choice and the host's CD clock.
            return new FurnitureRadioState(on, mode, station, volume, gain);
        }
    }
}
