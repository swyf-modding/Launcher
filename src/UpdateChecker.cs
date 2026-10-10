using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;

namespace ScamWYF.Launcher
{
    /// <summary>The latest published release of the launcher itself.</summary>
    internal sealed class LauncherRelease
    {
        public LauncherRelease(string tagName, string pageUrl)
        {
            TagName = tagName;
            PageUrl = pageUrl;
        }

        public string TagName { get; private set; }
        public string PageUrl { get; private set; }
    }

    /// <summary>
    /// Checks GitHub for a newer release of the launcher.
    /// </summary>
    /// <remarks>
    /// UI-free on purpose, like the rest of src\: the tests compile this file directly, and a version
    /// comparison is exactly the sort of thing worth pinning with offline assertions. Everything that
    /// touches a control lives in MainWindow, which calls in from a background thread.
    ///
    /// One request, unauthenticated, so the same 60-requests-an-hour limit as the mods tab applies -
    /// and the same reporting: Http already turns a 403/429 into a sentence about the limit, so a
    /// refusal here reads as what it is rather than as a generic failure.
    /// </remarks>
    internal static class UpdateChecker
    {
        internal const string Owner = "swyf-modding";
        internal const string Repo = "Launcher";

        internal static string LatestUrl
        {
            get { return "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest"; }
        }

        /// <summary>This build's version, for display and for comparison.</summary>
        /// <remarks>
        /// The informational version, not the assembly version: the build fills it from the git tag and
        /// it carries the commit, which is also what the window title shows. Falls back to the assembly
        /// version, which is all a build with no tag behind it has.
        /// </remarks>
        public static string CurrentVersion()
        {
            var assembly = typeof(UpdateChecker).Assembly;

            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (informational != null && !string.IsNullOrEmpty(informational.InformationalVersion))
            {
                return informational.InformationalVersion;
            }

            var version = assembly.GetName().Version;
            return version != null ? version.ToString() : "0.0.0";
        }

        /// <summary>Ask GitHub for the latest published launcher release.</summary>
        public static LauncherRelease FetchLatest()
        {
            var json = Http.GetText(LatestUrl);
            return ParseReleaseJson(json);
        }

        /// <summary>Read a tag and a page URL out of a releases/latest reply.</summary>
        /// <remarks>
        /// Separate from the fetch so the shape can be tested without a network: the reply is a few KB
        /// of JSON and the only two fields that matter here are tag_name and html_url.
        /// </remarks>
        internal static LauncherRelease ParseReleaseJson(string json)
        {
            object parsed;
            try
            {
                parsed = new JavaScriptSerializer { MaxJsonLength = Http.MaxTextBytes }.DeserializeObject(json);
            }
            catch (ArgumentException ex)
            {
                throw new IOException("GitHub's reply could not be read as JSON, which usually means a " +
                                      "rate limit page rather than a release. " + ex.Message, ex);
            }

            var root = parsed as Dictionary<string, object>;
            if (root == null) throw new IOException("GitHub's reply was not in the expected shape");

            object tag;
            if (!root.TryGetValue("tag_name", out tag) || tag == null || string.IsNullOrWhiteSpace(tag.ToString()))
            {
                throw new IOException("GitHub's reply named no release version");
            }

            object page;
            string pageUrl = null;
            if (root.TryGetValue("html_url", out page) && page != null) pageUrl = page.ToString();

            return new LauncherRelease(tag.ToString().Trim(), pageUrl);
        }

        /// <summary>Whether the published tag is newer than this build.</summary>
        /// <remarks>
        /// Numeric parts decide; build metadata (+g123abcd, .dirty) never does, because it names where a
        /// build came from rather than what it is. A stable release supersedes a prerelease with the same
        /// numbers - the exact case of running an rc and the final going out - while a prerelease is never
        /// offered to a stable install: telling someone on a finished release to "update" to a release
        /// candidate would be a downgrade dressed up as news, and /latest does not serve prereleases
        /// anyway. Someone already on a prerelease is told about a higher one, because they opted into
        /// that track.
        ///
        /// An unversioned build (0.0.0, no tag) compares as older than any real release, which is honest:
        /// without a tag there is nothing to claim currency with.
        /// </remarks>
        public static bool IsUpdateAvailable(string currentInformational, string latestTag)
        {
            if (string.IsNullOrWhiteSpace(latestTag)) return false;
            if (string.IsNullOrWhiteSpace(currentInformational)) return true;

            int[] currentNumbers;
            string currentPrerelease;
            Parse(currentInformational, out currentNumbers, out currentPrerelease);

            int[] latestNumbers;
            string latestPrerelease;
            Parse(latestTag, out latestNumbers, out latestPrerelease);

            var currentPre = currentPrerelease ?? "";
            var latestPre = latestPrerelease ?? "";

            var width = Math.Max(Math.Max(currentNumbers.Length, latestNumbers.Length), 3);
            for (var i = 0; i < width; i++)
            {
                var left = i < currentNumbers.Length ? currentNumbers[i] : 0;
                var right = i < latestNumbers.Length ? latestNumbers[i] : 0;
                if (right == left) continue;

                // Higher numbers, but the "higher" build is a prerelease and this one is finished:
                // stay silent rather than recruiting stable installs onto a candidate.
                if (latestPre.Length > 0 && currentPre.Length == 0) return false;
                return right > left;
            }

            // Same numbers throughout: only a prerelease-to-stable promotion counts.
            if (currentPre.Length > 0 && latestPre.Length == 0) return true;
            if (currentPre.Length == 0) return false;

            return string.Compare(latestPre, currentPre, StringComparison.Ordinal) > 0;
        }

        /// <summary>Split a tag or informational version into numbers and a prerelease name.</summary>
        /// <remarks>
        /// Accepts what this project actually stamps: an optional leading v, dot-separated integers, an
        /// optional -prerelease, and optional +metadata (commit, distance, dirty) which is dropped. The
        /// distance suffix a describe string carries (-3-g0a1b2c3) is metadata too, not a prerelease: the
        /// tag is split off it first, for the same reason build.ps1 strips it first - "v1.1.0-beta.2" has
        /// a dash inside the prerelease, so splitting on the first dash would read the tag as v1.1.0.
        /// Anything unparseable yields zeros rather than throwing, so a strange string reports "not newer"
        /// instead of breaking the check.
        /// </remarks>
        internal static void Parse(string version, out int[] numbers, out string prerelease)
        {
            numbers = new int[0];
            prerelease = "";

            if (string.IsNullOrWhiteSpace(version)) return;

            var text = version.Trim();

            // Build metadata (+g0a1b2c3, +3.g0a1b2c3.dirty) names the commit, not the release.
            var plus = text.IndexOf('+');
            if (plus >= 0) text = text.Substring(0, plus);

            // A describe distance suffix (-3-g0a1b2c3) likewise counts commits, not versions.
            var distance = System.Text.RegularExpressions.Regex.Match(text, @"-\d+-g[0-9a-fA-F]+$");
            if (distance.Success) text = text.Substring(0, distance.Index);

            // A dirty marker with no distance (a build from a modified tree exactly on a tag).
            if (text.EndsWith("-dirty", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(0, text.Length - "-dirty".Length);
            }

            var dash = text.IndexOf('-');
            if (dash >= 0)
            {
                prerelease = text.Substring(dash + 1).Trim();
                text = text.Substring(0, dash);
            }

            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);

            var parts = text.Split('.');
            var parsed = new List<int>();
            foreach (var part in parts)
            {
                int number;
                if (!int.TryParse(part.Trim(), out number) || number < 0) return;
                parsed.Add(number);
            }

            if (parsed.Count == 0) return;
            numbers = parsed.ToArray();
        }
    }
}
