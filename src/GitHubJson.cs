using System;
using System.Text.Json;

namespace Switchboard
{
    /// <summary>
    /// Reading the fields out of the JSON GitHub serves.
    /// </summary>
    /// <remarks>
    /// Shared, because two places need to agree about it: the mod catalogue reads a release's
    /// assets to find the mod's package, and the self-update reads the same list to find this
    /// program's own file. If they disagreed about what a digest means, one of them would be
    /// checking a download against a hash the other never computed.
    /// </remarks>
    internal static class GitHubJson
    {
        /// <summary>A string field, or null when it is absent or is not a string.</summary>
        public static string Text(JsonElement from, string key)
        {
            if (!from.TryGetProperty(key, out var value)) return null;

            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return value.GetString();
                case JsonValueKind.Number:
                    return value.GetRawText();
                default:
                    return null;
            }
        }

        /// <summary>A number field, or zero when it is absent or is not a number.</summary>
        public static long Number(JsonElement from, string key)
        {
            if (!from.TryGetProperty(key, out var value)) return 0;

            switch (value.ValueKind)
            {
                case JsonValueKind.Number:
                    return value.TryGetInt64(out var number) ? number : 0;
                case JsonValueKind.String:
                    return long.TryParse(value.GetString(), out var parsed) ? parsed : 0;
                default:
                    return 0;
            }
        }

        /// <summary>
        /// GitHub's digest field, which is "sha256:&lt;hex&gt;" or absent.
        /// </summary>
        /// <remarks>
        /// Returned as bare hex so it can be compared with what was actually downloaded, which is
        /// hashed locally. An unrecognised prefix yields null rather than a hash that would never
        /// match - and null means "no checksum to check against", not "a checksum that happens to
        /// be in another form".
        /// </remarks>
        public static string Digest(JsonElement asset, string key)
        {
            var raw = Text(asset, key);
            if (string.IsNullOrEmpty(raw)) return null;

            var colon = raw.IndexOf(':');
            if (colon < 0) return null;

            var algorithm = raw.Substring(0, colon);
            if (!string.Equals(algorithm, "sha256", StringComparison.OrdinalIgnoreCase)) return null;

            var hex = raw.Substring(colon + 1).Trim();
            return hex.Length == 64 ? hex.ToLowerInvariant() : null;
        }
    }
}
