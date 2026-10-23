using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ScamWYF.Setup;

namespace Switchboard
{
    /// <summary>Which BepInEx folder a file belongs in.</summary>
    internal enum ModFolder
    {
        /// <summary>BepInEx\plugins. The chainloader loads everything here as a plugin.</summary>
        Plugins,

        /// <summary>BepInEx\core. Loaded once, before any plugin, and shared.</summary>
        Core
    }

    /// <summary>One file a mod needs installed, and where it goes.</summary>
    internal sealed class ModFile
    {
        public string FileName { get; private set; }
        public ModFolder Folder { get; private set; }

        /// <summary>
        /// Whether this file must carry a [BepInPlugin] attribute.
        /// </summary>
        /// <remarks>
        /// True for a mod, false for the shared library: the library deliberately has no plugin in it,
        /// because the chainloader would try to load it as one. So "no [BepInPlugin]" is the correct
        /// answer for that file and must not be reported as a problem.
        /// </remarks>
        public bool ExpectsPluginAttribute { get; private set; }

        /// <summary>
        /// The SHA-256 the catalogue recorded when a person looked at this file, or null when the
        /// entry is not pinned.
        /// </summary>
        /// <remarks>
        /// Unlike GitHub's digest, this was written down at review time, so a match means these
        /// are the bytes somebody approved.
        /// </remarks>
        public string Sha256 { get; private set; }

        /// <summary>
        /// What the file can reach outside the game process, as the catalogue's scan found it.
        /// Never null; empty for a file that touches nothing outside the game.
        /// </summary>
        public string[] Capabilities { get; private set; }

        /// <summary>What the file's [BepInPlugin] attribute declares, when it declares one.</summary>
        public string[] BepInPlugin { get; private set; }

        public ModFile(string fileName, ModFolder folder, bool expectsPluginAttribute,
                       string sha256 = null, string[] capabilities = null, string[] bepInPlugin = null)
        {
            FileName = fileName;
            Folder = folder;
            ExpectsPluginAttribute = expectsPluginAttribute;
            Sha256 = sha256;
            Capabilities = capabilities ?? new string[0];
            BepInPlugin = bepInPlugin ?? new string[0];
        }

        /// <summary>Whether this file is pinned to the bytes somebody reviewed.</summary>
        public bool IsPinned { get { return !string.IsNullOrEmpty(Sha256); } }
    }

    /// <summary>
    /// An optional setup step a mod offers after its files are installed.
    /// </summary>
    /// <remarks>
    /// The canonical case is a companion the mod needs but cannot bundle: a stream mod's ChatBridge,
    /// something that talks to Twitch and is configured per machine. That setup cannot run at install
    /// time without asking, and it cannot run before the files are there - so it is offered afterwards,
    /// one step at a time, each deniable on its own.
    ///
    /// Declared in the catalogue but shipped in the download: the script runs out of the unpacked zip,
    /// never fetched from anywhere else. A catalogue entry naming a script the release does not contain
    /// fails the install plan rather than going looking for it.
    /// </remarks>
    internal sealed class PostInstallStep
    {
        /// <summary>A bare .ps1 name, resolved against the unpacked download at plan time.</summary>
        public string ScriptName { get; private set; }

        /// <summary>What the prompt shows: what this does and what skipping costs. Shown verbatim.</summary>
        public string Description { get; private set; }

        /// <summary>The SHA-256 the catalogue recorded for this script, or null when not pinned.</summary>
        /// <remarks>A setup step reaches a shell, so it is the part that most needs pinning.</remarks>
        public string Sha256 { get; private set; }

        /// <summary>Full path once the plan has verified it is in the download. Null before that.</summary>
        public string ScriptPath { get; private set; }

        /// <summary>The script's text, read at plan time so the prompt can show it. Null before that.</summary>
        public string ScriptText { get; private set; }

        public PostInstallStep(string scriptName, string description, string sha256)
        {
            ScriptName = scriptName;
            Description = description;
            Sha256 = sha256;
        }

        public PostInstallStep(string scriptName, string description, string scriptPath, string scriptText,
                               string sha256)
        {
            ScriptName = scriptName;
            Description = description;
            ScriptPath = scriptPath;
            ScriptText = scriptText;
            Sha256 = sha256;
        }

        /// <summary>Whether this script is pinned to the bytes somebody reviewed.</summary>
        public bool IsPinned { get { return !string.IsNullOrEmpty(Sha256); } }
    }

    /// <summary>A mod this tool knows about, and can fetch.</summary>
    internal sealed class ModSource
    {
        public string Owner { get; private set; }
        public string Repo { get; private set; }
        public string DisplayName { get; private set; }
        public string Summary { get; private set; }
        public ModFile[] Files { get; private set; }

        /// <summary>Optional setup steps offered after installing, each deniable. Never null.</summary>
        public PostInstallStep[] PostInstall { get; private set; }

        /// <summary>
        /// The release the catalogue says somebody reviewed, or null when the entry is not pinned.
        /// This is the version that is installed, not whatever the author published since.
        /// </summary>
        public string ApprovedVersion { get; private set; }

        public ModSource(string repo, string displayName, string summary, ModFile[] files,
                         string owner = ModCatalogue.DefaultOwner, PostInstallStep[] postInstall = null,
                         string approvedVersion = null)
        {
            Owner = string.IsNullOrEmpty(owner) ? ModCatalogue.DefaultOwner : owner;
            Repo = repo;
            DisplayName = displayName;
            Summary = summary;
            Files = files;
            PostInstall = postInstall ?? new PostInstallStep[0];
            ApprovedVersion = string.IsNullOrEmpty(approvedVersion) ? null : approvedVersion;
            IsVerified = ComputeVerified();
        }

        /// <summary>Whether the entry names the release a person reviewed.</summary>
        public bool IsPinned { get { return ApprovedVersion != null; } }

        /// <summary>
        /// Whether everything this entry installs is pinned to a hash: the release, every file and
        /// every setup script. The UI reads it to say which entries were reviewed.
        /// </summary>
        public bool IsVerified { get; private set; }

        private bool ComputeVerified()
        {
            if (!IsPinned) return false;

            foreach (var file in Files)
            {
                if (!file.IsPinned) return false;
            }

            foreach (var step in PostInstall)
            {
                if (!step.IsPinned) return false;
            }

            return true;
        }

        /// <summary>owner/repo, which is what names a third-party entry's origin.</summary>
        public string QualifiedName
        {
            get { return Owner + "/" + Repo; }
        }

        /// <summary>Whether this entry comes from outside the project's own organisation.</summary>
        public bool IsThirdParty
        {
            get { return !string.Equals(Owner, ModCatalogue.DefaultOwner, StringComparison.OrdinalIgnoreCase); }
        }
    }

    /// <summary>A mod, and the latest published release of it.</summary>
    internal sealed class ReleaseInfo
    {
        public ModSource Source { get; private set; }
        public string TagName { get; private set; }
        public string PageUrl { get; private set; }
        public ReleaseAsset Package { get; private set; }

        public ReleaseInfo(ModSource source, string tagName, string pageUrl, ReleaseAsset package)
        {
            Source = source;
            TagName = tagName;
            PageUrl = pageUrl;
            Package = package;
        }
    }

    /// <summary>
    /// The known mods, and how to ask GitHub for the release each one names.
    /// </summary>
    /// <remarks>
    /// A short list rather than a search, and that is the point. A list is reviewed: every entry names a
    /// repository someone chose to put there. A search box over arbitrary mod sites would be a different
    /// tool with a different set of risks, and nothing in this switchboard should imply that a mod came from
    /// here just because it was easy to fetch.
    ///
    /// The list itself is fetched live from the ModCatalogue repository rather than compiled in, so adding
    /// a mod no longer needs a switchboard release. The compiled-in list below remains as the offline
    /// fallback: if the fetch fails, the tab shows what it has always shown and says so. Entries may name
    /// repositories outside the project's own organisation; those are marked as third-party wherever they
    /// appear, because unlike the list there is no shared review behind them.
    ///
    /// "Install from a URL" exists for the cases a list cannot cover, and it is deliberately the
    /// separate, louder path: the user types the address, so there is no curated list behind it.
    ///
    /// The API is unauthenticated, which GitHub limits to 60 requests an hour per address. One catalogue
    /// fetch plus one lookup per mod is well inside that; the error is reported as what it is rather
    /// than as a generic failure.
    ///
    /// The JSON is read with System.Text.Json, which is in the runtime on every platform. The shape and
    /// every refusal are unchanged from the .NET Framework build; only the reader is different.
    /// </remarks>
    internal static class ModCatalogue
    {
        internal const string DefaultOwner = "swyf-modding";

        /// <summary>Where the live mod list lives. A branch, not a tag: catalogue edits reach users
        /// without a switchboard release, which is the point of fetching it instead of compiling it in.</summary>
        internal const string CatalogueUrl =
            "https://raw.githubusercontent.com/swyf-modding/ModCatalogue/main/mods.json";

        /// <summary>
        /// The capabilities the catalogue's scan can report, the same list as
        /// tools/scan-capabilities.ps1. A name it cannot produce is refused rather than shown.
        /// </summary>
        internal static readonly string[] KnownCapabilities =
        {
            "base64",
            "binary-serialization",
            "dynamic-code",
            "environment",
            "filesystem-read",
            "filesystem-write",
            "native-interop",
            "network",
            "process",
            "registry"
        };

        /// <summary>What a capability means, in the words the install confirmation uses.</summary>
        internal static string DescribeCapability(string capability)
        {
            switch (capability)
            {
                case "process": return "can start other programs";
                case "network": return "can use the network";
                case "filesystem-read": return "can read files";
                case "filesystem-write": return "can write files";
                case "native-interop": return "can call native code";
                case "dynamic-code": return "can load code at runtime";
                case "base64": return "can encode or decode base64";
                case "registry": return "can read the registry";
                case "environment": return "can read the environment";
                case "binary-serialization": return "can use BinaryFormatter";
                default: return "can " + capability;
            }
        }

        /// <summary>
        /// A file's capabilities as one sentence, or null when it has none. Most are things mods
        /// legitimately need; the point is that a change is visible.
        /// </summary>
        internal static string DescribeCapabilities(ModFile file)
        {
            if (file == null || file.Capabilities.Length == 0) return null;

            var words = new List<string>();
            foreach (var capability in file.Capabilities)
            {
                words.Add(DescribeCapability(capability));
            }

            return string.Join(", ", words.ToArray());
        }

        private static readonly ModFile LibraryFile =
            new ModFile("ScamWYF.Modding.Core.dll", ModFolder.Core, false);

        /// <summary>
        /// Characters a catalogue name may not contain.
        /// </summary>
        /// <remarks>
        /// A script or file name from the list is used as a bare name under a folder the folders above
        /// chose, so any separator is refused. Built from the platform's own separators rather than a
        /// hard-coded backslash, so this reads the same on Windows and Linux.
        /// </remarks>
        private static readonly char[] NameSeparators =
        {
            '/', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar
        };

        private static readonly ModSource[] Known =
        {
            new ModSource(
                "AI-Backend",
                "AI Backend",
                "Routes the game's AI calls to your own LLM - Ollama, LM Studio, llama.cpp, OpenAI, " +
                "OpenRouter, vLLM, Groq - instead of the hosted backend.",
                new[]
                {
                    new ModFile("ScamWYF.AiBackend.dll", ModFolder.Plugins, true),
                    LibraryFile
                }),

            new ModSource(
                "Mod-Handler",
                "Mod Handler",
                "Adds a Plugins tab to the in-game menu (F1), listing every installed plugin with a " +
                "switch for each.",
                new[]
                {
                    new ModFile("ScamWYF.ModHandler.dll", ModFolder.Plugins, true),
                    LibraryFile
                }),

            new ModSource(
                "mod-lib",
                "Shared Library",
                "The library the mods are built against. Installed on its own it does nothing; it is " +
                "here so the first mod you install brings it with it.",
                new[] { LibraryFile })
        };

        public static IEnumerable<ModSource> Mods { get { return Known; } }

        /// <summary>
        /// The live mod list. Falls back to the compiled-in list when the fetch fails, so an offline
        /// machine shows what it has always shown rather than an empty tab.
        /// </summary>
        /// <param name="usedFallback">True when the returned list is the built-in one, and why.</param>
        public static List<ModSource> FetchSources(out string usedFallback)
        {
            usedFallback = null;

            try
            {
                return FetchCatalogue();
            }
            catch (Exception ex)
            {
                usedFallback = ex.Message;
                return new List<ModSource>(Known);
            }
        }

        /// <summary>Fetch and validate the live mod list.</summary>
        public static List<ModSource> FetchCatalogue()
        {
            return ParseCatalogue(Http.GetText(CatalogueUrl));
        }

        /// <summary>Read a mod list out of catalogue JSON.</summary>
        /// <remarks>
        /// Separate from the fetch so the shape can be tested without a network. Fail-closed: anything
        /// unexpected rejects the whole file rather than skipping one entry, because a catalogue that
        /// only half-parsed is how a typo silently unlists a mod. The caller falls back to the built-in
        /// list and shows the message, so a rejection is visible rather than silent.
        /// </remarks>
        internal static List<ModSource> ParseCatalogue(string json)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new IOException("The mod list could not be read as JSON. " + ex.Message, ex);
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new IOException("The mod list was not in the expected shape");
                }

                if (!root.TryGetProperty("schemaVersion", out _))
                {
                    throw new IOException("The mod list names no schema version");
                }

                var schema = GitHubJson.Number(root, "schemaVersion");
                if (schema != 1)
                {
                    throw new IOException("The mod list uses schema version " + schema + ", which this switchboard " +
                                          "does not understand. Update the switchboard to read it");
                }

                if (!root.TryGetProperty("mods", out var raw) || raw.ValueKind != JsonValueKind.Array)
                {
                    throw new IOException("The mod list names no mods");
                }

                var count = raw.GetArrayLength();
                if (count == 0) throw new IOException("The mod list names no mods");
                if (count > 64)
                {
                    throw new IOException("The mod list names " + count + " mods, which is more than " +
                                          "the 64 this switchboard will consider");
                }

                var sources = new List<ModSource>(count);
                var index = 0;
                foreach (var entry in raw.EnumerateArray())
                {
                    sources.Add(ParseEntry(entry, index));
                    index++;
                }

                return sources;
            }
        }

        /// <summary>One catalogue entry, checked field by field.</summary>
        private static ModSource ParseEntry(JsonElement entry, int index)
        {
            var where = "entry " + (index + 1);
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new IOException("The mod list's " + where + " is not an object");
            }

            var repo = (GitHubJson.Text(entry, "repo") ?? "").Trim();
            if (!IsAccountName(repo))
            {
                throw new IOException("The mod list's " + where + " names no repository");
            }
            where = "mod '" + repo + "'";

            var ownerValue = GitHubJson.Text(entry, "owner");
            var owner = ownerValue != null ? ownerValue.Trim() : DefaultOwner;
            if (!IsAccountName(owner))
            {
                throw new IOException("The mod list's " + where + " names no valid owner");
            }

            var displayName = GitHubJson.Text(entry, "displayName");
            if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 80)
            {
                throw new IOException("The mod list's " + where + " has no usable display name");
            }

            var summary = GitHubJson.Text(entry, "summary");
            if (string.IsNullOrWhiteSpace(summary) || summary.Trim().Length > 500)
            {
                throw new IOException("The mod list's " + where + " has no usable summary");
            }

            if (!entry.TryGetProperty("files", out var filesValue) || filesValue.ValueKind != JsonValueKind.Array)
            {
                throw new IOException("The mod list's " + where + " names no files");
            }

            var fileCount = filesValue.GetArrayLength();
            if (fileCount == 0 || fileCount > 16)
            {
                throw new IOException("The mod list's " + where + " names no files");
            }

            var files = new List<ModFile>();
            foreach (var rawFile in filesValue.EnumerateArray())
            {
                files.Add(ParseFile(rawFile, where));
            }

            var approved = GitHubJson.Text(entry, "approvedVersion");
            if (approved != null)
            {
                approved = approved.Trim();
                if (approved.Length == 0 || approved.Length > 100)
                {
                    throw new IOException("The mod list's " + where + " pins an unusable release");
                }
            }

            var steps = ParsePostInstall(entry, where);
            var source = new ModSource(repo, displayName.Trim(), summary.Trim(), files.ToArray(), owner,
                                       steps, approved);

            // A pinned entry has to pin everything it installs. An approvedVersion whose files or
            // scripts are missing a hash is not a reviewed release with a gap in it - it is an
            // entry that would be installed on a promise, so it is refused rather than half-trusted.
            if (source.IsPinned)
            {
                foreach (var file in source.Files)
                {
                    if (!file.IsPinned)
                    {
                        throw new IOException("The mod list's " + where + " pins " + approved + " but gives " +
                                              file.FileName + " no hash to check it against");
                    }
                }

                foreach (var step in source.PostInstall)
                {
                    if (!step.IsPinned)
                    {
                        throw new IOException("The mod list's " + where + " pins " + approved + " but gives the " +
                                              "setup script " + step.ScriptName + " no hash to check it against");
                    }
                }
            }

            return source;
        }

        /// <summary>
        /// A mod's optional setup steps. Absent means none; anything present must be exact.
        /// </summary>
        /// <remarks>
        /// Capped at four: these are prompts a person answers one by one after installing, and a mod
        /// with more setup steps than that has a setup problem no switchboard dialog will fix.
        /// </remarks>
        private static PostInstallStep[] ParsePostInstall(JsonElement entry, string where)
        {
            if (!entry.TryGetProperty("postInstall", out var raw) || raw.ValueKind == JsonValueKind.Null)
            {
                return new PostInstallStep[0];
            }

            if (raw.ValueKind != JsonValueKind.Array)
            {
                throw new IOException("The mod list's " + where + " names no usable setup steps");
            }

            var count = raw.GetArrayLength();
            if (count == 0 || count > 4)
            {
                throw new IOException("The mod list's " + where + " names no usable setup steps");
            }

            var steps = new List<PostInstallStep>();
            var number = 1;
            foreach (var rawStep in raw.EnumerateArray())
            {
                steps.Add(ParsePostInstallStep(rawStep, where, number));
                number++;
            }

            return steps.ToArray();
        }

        /// <summary>One setup step: which script in the download, and what the prompt says about it.</summary>
        private static PostInstallStep ParsePostInstallStep(JsonElement step, string where, int number)
        {
            var which = where + " setup step " + number;
            if (step.ValueKind != JsonValueKind.Object)
            {
                throw new IOException("The mod list's " + which + " is not an object");
            }

            var script = GitHubJson.Text(step, "script");
            if (string.IsNullOrWhiteSpace(script) || script.Trim().Length > 100 ||
                script.IndexOfAny(NameSeparators) >= 0 ||
                !script.Trim().EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The mod list's " + which + " names no script this switchboard will run");
            }

            var description = GitHubJson.Text(step, "description");
            if (string.IsNullOrWhiteSpace(description) || description.Trim().Length > 280)
            {
                throw new IOException("The mod list's " + which + " says nothing usable about what it does");
            }

            return new PostInstallStep(script.Trim(), description.Trim(), Hash(step, "sha256", which));
        }

        /// <summary>
        /// A pinned hash: 64 hex characters, or null when the field is absent.
        /// </summary>
        /// <remarks>
        /// An unreadable hash is refused rather than ignored. Ignoring it would turn a pin that
        /// was written wrong into an entry that is not pinned at all, which is the one thing the
        /// field exists to prevent.
        /// </remarks>
        private static string Hash(JsonElement from, string key, string where)
        {
            var raw = GitHubJson.Text(from, key);
            if (raw == null) return null;

            var trimmed = raw.Trim();
            if (trimmed.Length != 64)
            {
                throw new IOException("The mod list's " + where + " has a " + key + " that is not a sha256");
            }

            foreach (var c in trimmed)
            {
                var ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) throw new IOException("The mod list's " + where + " has a " + key + " that is not a sha256");
            }

            return trimmed.ToLowerInvariant();
        }

        /// <summary>One file entry: a bare dll name and a known folder, nothing else.</summary>
        /// <remarks>
        /// Strict about the name because it becomes a file under the game folder: no separators, no
        /// parent references, and a .dll suffix, so a catalogue entry can never route a download
        /// somewhere the folders above did not intend.
        /// </remarks>
        private static ModFile ParseFile(JsonElement file, string where)
        {
            if (file.ValueKind != JsonValueKind.Object)
            {
                throw new IOException("The mod list's " + where + " names a file that is not an object");
            }

            var name = GitHubJson.Text(file, "fileName");
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100 ||
                name.IndexOfAny(NameSeparators) >= 0 ||
                name.Trim() == "." || name.Trim() == ".." ||
                !name.Trim().EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The mod list's " + where + " names a file this switchboard will not install");
            }

            var folderText = GitHubJson.Text(file, "folder");
            ModFolder folder;
            if (string.Equals(folderText, "plugins", StringComparison.OrdinalIgnoreCase))
            {
                folder = ModFolder.Plugins;
            }
            else if (string.Equals(folderText, "core", StringComparison.OrdinalIgnoreCase))
            {
                folder = ModFolder.Core;
            }
            else
            {
                throw new IOException("The mod list's " + where + " puts " + name.Trim() +
                                      " somewhere other than plugins or core");
            }

            if (!file.TryGetProperty("expectsPluginAttribute", out var flag) ||
                (flag.ValueKind != JsonValueKind.True && flag.ValueKind != JsonValueKind.False))
            {
                throw new IOException("The mod list's " + where + " does not say whether " +
                                      name.Trim() + " is a plugin");
            }

            var capabilities = Strings(file, "capabilities", where + " " + name.Trim());
            foreach (var capability in capabilities)
            {
                if (Array.IndexOf(KnownCapabilities, capability) < 0)
                {
                    throw new IOException("The mod list's " + where + " gives " + name.Trim() + " a capability " +
                                          "this switchboard does not know: " + capability);
                }
            }

            return new ModFile(name.Trim(), folder, flag.ValueKind == JsonValueKind.True,
                               Hash(file, "sha256", where + " " + name.Trim()),
                               capabilities,
                               Strings(file, "bepInPlugin", where + " " + name.Trim()));
        }

        /// <summary>An optional array of strings, or an empty array when the field is absent.</summary>
        private static string[] Strings(JsonElement from, string key, string where)
        {
            if (!from.TryGetProperty(key, out var raw) || raw.ValueKind == JsonValueKind.Null)
            {
                return new string[0];
            }

            if (raw.ValueKind != JsonValueKind.Array)
            {
                throw new IOException("The mod list's " + where + " has a " + key + " that is not a list");
            }

            var values = new List<string>();
            foreach (var entry in raw.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String)
                {
                    throw new IOException("The mod list's " + where + " has a " + key + " with something other " +
                                          "than a name in it");
                }

                var text = entry.GetString();
                if (string.IsNullOrWhiteSpace(text) || text.Length > 200)
                {
                    throw new IOException("The mod list's " + where + " has a " + key + " that says nothing");
                }

                values.Add(text);
            }

            return values.ToArray();
        }

        /// <summary>A GitHub account or repository name: letters, digits, dot, dash, underscore.</summary>
        internal static bool IsAccountName(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 100) return false;

            foreach (var c in text)
            {
                var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                         c == '.' || c == '-' || c == '_';
                if (!ok) return false;
            }

            return true;
        }

        /// <summary>
        /// Ask GitHub for the release of one mod: the pinned one when the catalogue names it,
        /// otherwise the latest. A pin that has gone is a refusal, not a quiet fallback to a
        /// newer release.
        /// </summary>
        public static ReleaseInfo FetchLatest(ModSource source)
        {
            var url = source.IsPinned
                ? "https://api.github.com/repos/" + source.Owner + "/" + source.Repo + "/releases/tags/" +
                  Uri.EscapeDataString(source.ApprovedVersion)
                : "https://api.github.com/repos/" + source.Owner + "/" + source.Repo + "/releases/latest";

            string json;
            try
            {
                json = Http.GetText(url);
            }
            catch (IOException ex)
            {
                if (!source.IsPinned) throw;

                throw new IOException("the mod list lists " + source.ApprovedVersion + " of " + source.Repo +
                                      ", and GitHub does not have that release any more. Nothing was installed. " +
                                      ex.Message, ex);
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new IOException("GitHub's reply could not be read as JSON, which usually means a " +
                                      "rate limit page rather than a release. " + ex.Message, ex);
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new IOException("GitHub's reply was not in the expected shape");
                }

                var tag = GitHubJson.Text(root, "tag_name");
                var page = GitHubJson.Text(root, "html_url");

                // Defence in depth: the URL asked for the pinned tag, and this checks that the
                // reply really is that release rather than something the API decided to return.
                if (source.IsPinned && !string.Equals(tag, source.ApprovedVersion, StringComparison.Ordinal))
                {
                    throw new IOException("the mod list lists " + source.ApprovedVersion + " of " + source.Repo +
                                          ", and GitHub returned " + tag + " instead. Nothing was installed");
                }

                var package = FindPackage(root);
                if (package == null)
                {
                    throw new IOException(source.Repo + " has a release (" + tag + ") but no downloadable zip in it. " +
                                          "Nothing can be installed from it");
                }

                return new ReleaseInfo(source, tag, page, package);
            }
        }

        /// <summary>The release's zip. A release with none cannot be installed from.</summary>
        private static ReleaseAsset FindPackage(JsonElement root)
        {
            if (!root.TryGetProperty("assets", out var raw) || raw.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var entry in raw.EnumerateArray())
            {
                var asset = ReleaseAsset.FromJson(entry);
                if (asset == null) continue;

                // A mod is installed from a zip, so that is the only asset that will do.
                if (!asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

                if (!Http.IsAcceptable(asset.DownloadUrl)) continue;

                return asset;
            }

            return null;
        }


        /// <summary>
        /// The destination folder for a file, given the install.
        /// </summary>
        /// <remarks>
        /// Core and plugins are siblings under BepInEx, and putting the library in plugins is the single
        /// most common way to break a working install: the chainloader would load it as a plugin, find no
        /// plugin in it, and the mods would then load against a second copy of the library's statics.
        /// </remarks>
        public static string FolderFor(GameInstall install, ModFolder folder)
        {
            return folder == ModFolder.Core ? install.BepInExCore : install.PluginsFolder;
        }
    }
}
