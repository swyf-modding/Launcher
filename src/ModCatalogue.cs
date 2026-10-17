using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace ScamWYF.Launcher
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

        public ModFile(string fileName, ModFolder folder, bool expectsPluginAttribute)
        {
            FileName = fileName;
            Folder = folder;
            ExpectsPluginAttribute = expectsPluginAttribute;
        }
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

        /// <summary>Full path once the plan has verified it is in the download. Null before that.</summary>
        public string ScriptPath { get; private set; }

        /// <summary>The script's text, read at plan time so the prompt can show it. Null before that.</summary>
        public string ScriptText { get; private set; }

        public PostInstallStep(string scriptName, string description)
            : this(scriptName, description, null, null)
        {
        }

        public PostInstallStep(string scriptName, string description, string scriptPath, string scriptText)
        {
            ScriptName = scriptName;
            Description = description;
            ScriptPath = scriptPath;
            ScriptText = scriptText;
        }
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

        public ModSource(string repo, string displayName, string summary, ModFile[] files,
                         string owner = ModCatalogue.DefaultOwner, PostInstallStep[] postInstall = null)
        {
            Owner = string.IsNullOrEmpty(owner) ? ModCatalogue.DefaultOwner : owner;
            Repo = repo;
            DisplayName = displayName;
            Summary = summary;
            Files = files;
            PostInstall = postInstall ?? new PostInstallStep[0];
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

    /// <summary>A downloadable file attached to a GitHub release.</summary>
    internal sealed class ReleaseAsset
    {
        public string Name { get; private set; }
        public long Size { get; private set; }
        public string DownloadUrl { get; private set; }

        /// <summary>
        /// GitHub's own SHA-256 for the asset, when it publishes one.
        /// </summary>
        /// <remarks>
        /// Worth having, and worth being precise about what it proves: it catches a truncated or mangled
        /// download, and it catches an asset host that is not GitHub's. It does not make the mod
        /// trustworthy - the digest comes from the same place as the file - so it is an integrity check,
        /// not a signature, and nothing in the UI should imply otherwise.
        /// </remarks>
        public string Sha256 { get; private set; }

        /// <summary>
        /// A public constructor, because the URL path builds one too: there is no GitHub listing behind
        /// that download, so the caller supplies what would otherwise have been read from the API.
        /// </summary>
        public ReleaseAsset(string name, long size, string downloadUrl, string sha256)
        {
            Name = name;
            Size = size;
            DownloadUrl = downloadUrl;
            Sha256 = sha256;
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
    /// The known mods, and how to ask GitHub what the latest release of each one is.
    /// </summary>
    /// <remarks>
    /// A short list rather than a search, and that is the point. A list is reviewed: every entry names a
    /// repository someone chose to put there. A search box over arbitrary mod sites would be a different
    /// tool with a different set of risks, and nothing in this launcher should imply that a mod came from
    /// here just because it was easy to fetch.
    ///
    /// The list itself is fetched live from the ModCatalogue repository rather than compiled in, so adding
    /// a mod no longer needs a launcher release. The compiled-in list below remains as the offline
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
    /// </remarks>
    internal static class ModCatalogue
    {
        internal const string DefaultOwner = "swyf-modding";

        /// <summary>Where the live mod list lives. A branch, not a tag: catalogue edits reach users
        /// without a launcher release, which is the point of fetching it instead of compiling it in.</summary>
        internal const string CatalogueUrl =
            "https://raw.githubusercontent.com/swyf-modding/ModCatalogue/main/mods.json";

        private static readonly ModFile LibraryFile =
            new ModFile("ScamWYF.Modding.Core.dll", ModFolder.Core, false);

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
                "HostBoss",
                "HostBoss",
                "Lets the lobby host control the boss presentation, slides, quota, quotes and recordings. " +
                "Use the BepInEx plugin release; remove legacy HostBoss disk patches before switching.",
                new[] { new ModFile("ScamWYF.HostBoss.dll", ModFolder.Plugins, true) },
                "ProProgramer123"),

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
            object parsed;
            try
            {
                parsed = new JavaScriptSerializer { MaxJsonLength = Http.MaxTextBytes }.DeserializeObject(json);
            }
            catch (ArgumentException ex)
            {
                throw new IOException("The mod list could not be read as JSON. " + ex.Message, ex);
            }

            var root = parsed as Dictionary<string, object>;
            if (root == null) throw new IOException("The mod list was not in the expected shape");

            object schemaValue;
            if (!root.TryGetValue("schemaVersion", out schemaValue))
            {
                throw new IOException("The mod list names no schema version");
            }

            var schema = Number(root, "schemaVersion");
            if (schema != 1)
            {
                throw new IOException("The mod list uses schema version " + schema + ", which this launcher " +
                                      "does not understand. Update the launcher to read it");
            }

            object raw;
            if (!root.TryGetValue("mods", out raw)) throw new IOException("The mod list names no mods");

            var entries = raw as object[];
            if (entries == null) throw new IOException("The mod list names no mods");
            if (entries.Length == 0) throw new IOException("The mod list names no mods");
            if (entries.Length > 64)
            {
                throw new IOException("The mod list names " + entries.Length + " mods, which is more than " +
                                      "the 64 this launcher will consider");
            }

            var sources = new List<ModSource>();
            for (var i = 0; i < entries.Length; i++)
            {
                sources.Add(ParseEntry(entries[i] as Dictionary<string, object>, i));
            }

            return sources;
        }

        /// <summary>One catalogue entry, checked field by field.</summary>
        private static ModSource ParseEntry(Dictionary<string, object> entry, int index)
        {
            var where = "entry " + (index + 1);
            if (entry == null) throw new IOException("The mod list's " + where + " is not an object");

            object repoValue;
            var repo = entry.TryGetValue("repo", out repoValue) && repoValue != null
                ? repoValue.ToString().Trim() : null;
            if (!IsAccountName(repo))
            {
                throw new IOException("The mod list's " + where + " names no repository");
            }
            where = "mod '" + repo + "'";

            object ownerValue;
            var owner = entry.TryGetValue("owner", out ownerValue) && ownerValue != null
                ? ownerValue.ToString().Trim() : DefaultOwner;
            if (!IsAccountName(owner))
            {
                throw new IOException("The mod list's " + where + " names no valid owner");
            }

            var displayName = Text(entry, "displayName");
            if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 80)
            {
                throw new IOException("The mod list's " + where + " has no usable display name");
            }

            var summary = Text(entry, "summary");
            if (string.IsNullOrWhiteSpace(summary) || summary.Trim().Length > 500)
            {
                throw new IOException("The mod list's " + where + " has no usable summary");
            }

            object filesValue;
            if (!entry.TryGetValue("files", out filesValue)) throw new IOException("The mod list's " + where + " names no files");

            var rawFiles = filesValue as object[];
            if (rawFiles == null || rawFiles.Length == 0 || rawFiles.Length > 16)
            {
                throw new IOException("The mod list's " + where + " names no files");
            }

            var files = new List<ModFile>();
            foreach (var rawFile in rawFiles)
            {
                files.Add(ParseFile(rawFile as Dictionary<string, object>, where));
            }

            return new ModSource(repo, displayName.Trim(), summary.Trim(), files.ToArray(), owner,
                                 ParsePostInstall(entry, where));
        }

        /// <summary>
        /// A mod's optional setup steps. Absent means none; anything present must be exact.
        /// </summary>
        /// <remarks>
        /// Capped at four: these are prompts a person answers one by one after installing, and a mod
        /// with more setup steps than that has a setup problem no launcher dialog will fix.
        /// </remarks>
        private static PostInstallStep[] ParsePostInstall(Dictionary<string, object> entry, string where)
        {
            object raw;
            if (!entry.TryGetValue("postInstall", out raw) || raw == null) return new PostInstallStep[0];

            var rawSteps = raw as object[];
            if (rawSteps == null || rawSteps.Length == 0 || rawSteps.Length > 4)
            {
                throw new IOException("The mod list's " + where + " names no usable setup steps");
            }

            var steps = new List<PostInstallStep>();
            for (var i = 0; i < rawSteps.Length; i++)
            {
                steps.Add(ParsePostInstallStep(rawSteps[i] as Dictionary<string, object>, where, i + 1));
            }

            return steps.ToArray();
        }

        /// <summary>One setup step: which script in the download, and what the prompt says about it.</summary>
        private static PostInstallStep ParsePostInstallStep(Dictionary<string, object> step, string where, int number)
        {
            var which = where + " setup step " + number;
            if (step == null) throw new IOException("The mod list's " + which + " is not an object");

            var script = Text(step, "script");
            if (string.IsNullOrWhiteSpace(script) || script.Trim().Length > 100 ||
                script.IndexOf('/') >= 0 || script.IndexOf('\\') >= 0 ||
                !script.Trim().EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The mod list's " + which + " names no script this launcher will run");
            }

            var description = Text(step, "description");
            if (string.IsNullOrWhiteSpace(description) || description.Trim().Length > 280)
            {
                throw new IOException("The mod list's " + which + " says nothing usable about what it does");
            }

            return new PostInstallStep(script.Trim(), description.Trim());
        }

        /// <summary>One file entry: a bare dll name and a known folder, nothing else.</summary>
        /// <remarks>
        /// Strict about the name because it becomes a file under the game folder: no separators, no
        /// parent references, and a .dll suffix, so a catalogue entry can never route a download
        /// somewhere the folders above did not intend.
        /// </remarks>
        private static ModFile ParseFile(Dictionary<string, object> file, string where)
        {
            if (file == null) throw new IOException("The mod list's " + where + " names a file that is not an object");

            var name = Text(file, "fileName");
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100 ||
                name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 ||
                name.Trim() == "." || name.Trim() == ".." ||
                !name.Trim().EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The mod list's " + where + " names a file this launcher will not install");
            }

            var folderText = Text(file, "folder");
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

            object flag;
            if (!file.TryGetValue("expectsPluginAttribute", out flag) || !(flag is bool))
            {
                throw new IOException("The mod list's " + where + " does not say whether " +
                                      name.Trim() + " is a plugin");
            }

            return new ModFile(name.Trim(), folder, (bool)flag);
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

        /// <summary>Ask GitHub for the latest release of one mod.</summary>
        public static ReleaseInfo FetchLatest(ModSource source)
        {
            var url = "https://api.github.com/repos/" + source.Owner + "/" + source.Repo + "/releases/latest";
            var json = Http.GetText(url);

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

            var tag = Text(root, "tag_name");
            var page = Text(root, "html_url");

            var package = FindPackage(root);
            if (package == null)
            {
                throw new IOException(source.Repo + " has a release (" + tag + ") but no downloadable zip in it. " +
                                      "Nothing can be installed from it");
            }

            return new ReleaseInfo(source, tag, page, package);
        }

        /// <summary>The release's zip. A release with none cannot be installed from.</summary>
        private static ReleaseAsset FindPackage(Dictionary<string, object> root)
        {
            object raw;
            if (!root.TryGetValue("assets", out raw)) return null;

            var assets = raw as object[];
            if (assets == null) return null;

            foreach (var entry in assets)
            {
                var asset = entry as Dictionary<string, object>;
                if (asset == null) continue;

                var name = Text(asset, "name");
                if (string.IsNullOrEmpty(name)) continue;

                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

                var url = Text(asset, "browser_download_url");
                if (!Http.IsAcceptable(url)) continue;

                return new ReleaseAsset(name, Number(asset, "size"), url, Digest(asset, "digest"));
            }

            return null;
        }

        /// <summary>
        /// GitHub's digest field, which is "sha256:&lt;hex&gt;" or absent.
        /// </summary>
        /// <remarks>
        /// Returned as bare hex so it can be compared with what was actually downloaded, which is hashed
        /// locally. An unrecognised prefix yields null rather than a hash that would never match.
        /// </remarks>
        private static string Digest(Dictionary<string, object> asset, string key)
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

        private static string Text(Dictionary<string, object> from, string key)
        {
            object value;
            return from.TryGetValue(key, out value) && value != null ? value.ToString() : null;
        }

        private static long Number(Dictionary<string, object> from, string key)
        {
            object value;
            if (!from.TryGetValue(key, out value) || value == null) return 0;

            try
            {
                return Convert.ToInt64(value);
            }
            catch (Exception)
            {
                return 0;
            }
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
