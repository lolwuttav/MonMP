using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MonMP.Core.World
{
    // These are the native save's world entitlements, shared by everybody in the session.
    public sealed class MeetProgressState
    {
        public static readonly ulong Entity = StaticId.Hash("meet-progress/v1");
        public IReadOnlyList<float> BestTimes { get; }
        public IReadOnlyList<bool> Claimed { get; }
        public int Streak { get; }
        public int BestStreak { get; }

        public MeetProgressState(IEnumerable<float> bestTimes, int streak, int bestStreak, IEnumerable<bool> claimed)
        {
            BestTimes = Array.AsReadOnly(bestTimes.ToArray());
            Claimed = Array.AsReadOnly(claimed.ToArray());
            Streak = streak; BestStreak = bestStreak;
            if (BestTimes.Count != 3 || Claimed.Count != 3 || streak < 0 || bestStreak < streak ||
                BestTimes.Any(time => float.IsNaN(time) || float.IsInfinity(time) || time < 0f && time != -1f || time > 121f))
                throw new InvalidDataException("Invalid meet progression.");
        }

        public bool Eligible(int reward) => reward >= 0 && reward < 3 && !Claimed[reward] &&
            (reward == 0 ? BestStreak >= 5 : BestTimes[reward] > 0f);

        public MeetProgressState Win(int car, float seconds)
        {
            if (car < 0 || car >= 3 || float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds <= 0f || seconds > 121f)
                throw new InvalidDataException("Invalid meet result.");
            var times = BestTimes.ToArray();
            if (times[car] <= 0f || seconds < times[car]) times[car] = seconds;
            int streak = Streak == int.MaxValue ? int.MaxValue : Streak + 1;
            return new MeetProgressState(times, streak, Math.Max(streak, BestStreak), Claimed);
        }

        public MeetProgressState Lose() => new MeetProgressState(BestTimes, 0, BestStreak, Claimed);

        public MeetProgressState Claim(int reward)
        {
            if (!Eligible(reward)) throw new InvalidOperationException("Meet reward is unavailable.");
            var flags = Claimed.ToArray(); flags[reward] = true;
            return new MeetProgressState(BestTimes, Streak, BestStreak, flags);
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write((byte)1);
            foreach (float time in BestTimes) writer.Write(time);
            writer.Write(Streak); writer.Write(BestStreak);
            writer.Write((byte)((Claimed[0] ? 1 : 0) | (Claimed[1] ? 2 : 0) | (Claimed[2] ? 4 : 0)));
            return stream.ToArray();
        }

        public static MeetProgressState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 22) throw new InvalidDataException("Invalid meet progression size.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            if (reader.ReadByte() != 1) throw new InvalidDataException("Unknown meet progression layout.");
            var times = new[] { reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() };
            int streak = reader.ReadInt32(), best = reader.ReadInt32(); byte flags = reader.ReadByte();
            if ((flags & ~7) != 0) throw new InvalidDataException("Invalid meet reward flags.");
            return new MeetProgressState(times, streak, best, new[] { (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0 });
        }
    }
}
