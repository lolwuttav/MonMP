using System;
using System.Text;

namespace MonMP.Plugin.Replication
{
    // WorldChannel.Reply accepts at most 128 bytes, including the presentation tag.
    // Known native refusals localize on the requester; other text stays valid UTF-8
    // when shortened to the remaining 127-byte budget.
    internal static class NativeErrorReply
    {
        private static readonly UTF8Encoding Strict = new UTF8Encoding(false, true);

        internal static byte[] Encode(string text)
        {
            for (byte kind = 1; kind <= 6; kind++)
                if (text == TEXT.GetText(Key(kind)!)) return new byte[] { 254, kind };
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            int length = Math.Min(bytes.Length, 127);
            if (length < bytes.Length)
                while (length > 0 && (bytes[length] & 0xC0) == 0x80) length--;
            byte[] result = new byte[length + 1];
            result[0] = 255;
            Buffer.BlockCopy(bytes, 0, result, 1, length);
            return result;
        }

        internal static bool TryDecode(byte[] payload, out string text)
        {
            text = "";
            if (payload == null || payload.Length < 2 || payload.Length > 128) return false;
            if (payload.Length == 2 && payload[0] == 254 && Key(payload[1]) is string key)
            { text = TEXT.GetText(key); return true; }
            if (payload[0] != 255) return false;
            try { text = Strict.GetString(payload, 1, payload.Length - 1); return true; }
            catch (DecoderFallbackException) { return false; }
        }

        private static string? Key(byte kind)
        {
            return kind switch
            {
                1 => "ERROR_NoElectricity",
                2 => "ERROR_ComputerisShutingDown",
                3 => "ERROR_ComputerisBooting",
                4 => "ERROR_WindowTint_TintRollIsEmpty",
                5 => "ERROR_WindowTint_AlreadyTint",
                6 => "ERROR_WindowTint_NoTintToRemove",
                _ => null
            };
        }
    }
}
