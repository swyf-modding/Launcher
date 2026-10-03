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

    /// <summary>A mod this tool knows about, and can fetch.</summary>
    internal sealed class ModSource
    {
        public string Repo { get; private set; }
        public string DisplayName { get; private set; }
        public string Summary { get; private set; }
        public ModFile[] Files { get; private set; }

        public ModSource(string repo, string displayName, string summary, ModFile[] files)
        {
            Repo = repo;
            DisplayName = displayName;
            Summary = summary;
            Files = files;
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
    /// repository whose releases this project publishes, so what appears here is what someone chose to
    /// put there. A search box over arbitrary mod sites would be a different tool with a different set of
    /// risks, and nothing in this launcher should imply that a mod came from here just because it was
    /// easy to fetch.
    ///
    /// "Install from a URL" exists for the cases a list cannot cover, and it is deliberately the
    /// separate, louder path: the user types the address, so there is no curated list behind it.
    ///
    /// The API is unauthenticated, which GitHub limits to 60 requests an hour per address. Three lookups
    /// is well inside that; the error is reported as what it is rather than as a generic failure.
    /// </remarks>
    internal static class ModCatalogue
    {
        private const string Owner = "swyf-modding";

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
                "The library both mods are built against. Installed on its own it does nothing; it is " +
                "here so the first mod you install brings it with it.",
                new[] { LibraryFile })
        };

        public static IEnumerable<ModSource> Mods { get { return Known; } }

        /// <summary>Ask GitHub for the latest release of one mod.</summary>
        public static ReleaseInfo FetchLatest(ModSource source)
        {
            var url = "https://api.github.com/repos/" + Owner + "/" + source.Repo + "/releases/latest";
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