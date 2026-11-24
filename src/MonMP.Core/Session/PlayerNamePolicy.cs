using System;
using System.IO;
using MonMP.Core.Protocol;

namespace MonMP.Core.Session
{
    public static class PlayerNamePolicy
    {
        public const string DefaultName = "Player";
        public const string RequiredMessage = "Choose a custom player name before joining (not 'Player').";
        public const string InvalidMessage = "Use a player name of 1 to 48 UTF-8 bytes, without control characters or < >.";

        public static bool IsDefault(string? name) =>
            string.Equals(name?.Trim(), DefaultName, StringComparison.OrdinalIgnoreCase);

        public static string? ValidationError(string? name)
        {
            if (string.IsNullOrWhiteSpace(name) || IsDefault(name)) return RequiredMessage;
            try { Wire.ValidateName(name!.Trim()); }
            catch (InvalidDataException) { return InvalidMessage; }
            catch (ArgumentException) { return InvalidMessage; }
            return null;
        }

        public static string RequireCustom(string? name)
        {
            if (ValidationError(name) is string error) throw new InvalidDataException(error);
            return name!.Trim();
        }
    }
}
