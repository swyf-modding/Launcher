using System;
using System.Collections.Generic;
using System.IO;

namespace ScamWYF.Launcher
{
    /// <summary>
    /// What the tool found when it looked at a game install.
    /// </summary>
    /// <remarks>
    /// Probing rather than assuming, because the interesting case is a partly-installed game: a
    /// player who has BepInEx but not the corlib override gets a crash they cannot explain, and the
    /// fix is exactly the setup script. So each piece is reported separately instead of as one
    /// installed/not-installed answer.
    /// </remarks>
    public sealed class GameInstall
    {
        private GameInstall(string path) { Path = path; }

        public string Path { get; private set; }

        /// <summary>The game's Unity assemblies. Present in a real install, absent otherwise.</summary>
        public string ManagedFolder
        {
            get { return System.IO.Path.Combine(Path, "Scam With Your Friends_Data\\Managed"); }
        }

        public string BepInExCore
        {
            get { return System.IO.Path.Combine(Path, "BepInEx\\core"); }
        }

        /// <summary>
        /// The BepInEx folder itself, which is where configs live.
        /// </summary>
        /// <remarks>
        /// Named here rather than derived at each call site. <see cref="BepInExCore"/> is two levels down,
        /// and every caller that wanted the config folder had to walk back out of it - which is the sort
        /// of thing that is correct until someone adds a level.
        /// </remarks>
        public string BepInExRoot
        {
            get { return System.IO.Path.Combine(Path, "BepInEx"); }
        }

        public string PluginsFolder
        {
            get { return System.IO.Path.Combine(Path, "BepInEx\\plugins"); }
        }

        public string ConfigFolder
        {
            get { return System.IO.Path.Combine(Path, "BepInEx\\config"); }
        }

        /// <summary>The corlib override Doorstop redirects to. Without it the loader cannot start.</summary>
        public string OverrideFolder
        {
            get { return System.IO.Path.Combine(Path, "unstripped_corlib"); }
        }

        public string DoorstopConfig
        {
            get { return System.IO.Path.Combine(Path, "doorstop_config.ini"); }
        }

        public string LogFile
        {
            get { return System.IO.Path.Combine(Path, "BepInEx\\LogOutput.log"); }
        }

        public string Executable
        {
            get { return System.IO.Path.Combine(Path, "Scam With Your Friends.exe"); }
        }

        /// <summary>The game's assemblies exist at all.</summary>
        public bool HasGame
        {
            get { return File.Exists(System.IO.Path.Combine(ManagedFolder, "mscorlib.dll")); }
        }

        /// <summary>Doorstop is installed: the loader half.</summary>
        public bool HasDoorstop
        {
            get { return File.Exists(System.IO.Path.Combine(Path, "winhttp.dll")); }
        }

        /// <summary>Doorstop is pointed at the corlib override, which is what makes it usable.</summary>
        public bool DoorstopPointsAtOverride
        {
            get
            {
                try
                {
                    return File.Exists(DoorstopConfig) &&
                        File.ReadAllText(DoorstopConfig).IndexOf("dll_search_path_override", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>BepInEx is installed.</summary>
        public bool HasBepInEx
        {
            get { return File.Exists(System.IO.Path.Combine(BepInExCore, "BepInEx.dll")); }
        }

        /// <summary>The unstripped corlib is seeded. The single most common cause of "it crashed".</summary>
        public bool HasCorlibOverride
        {
            get { return File.Exists(System.IO.Path.Combine(OverrideFolder, "mscorlib.dll")); }
        }

        /// <summary>Anything at all to say about this install.</summary>
        public bool Exists
        {
            get { return Directory.Exists(Path); }
        }

        /// <summary>Setup has everything it needs to work.</summary>
        public bool IsReady
        {
            get { return HasGame && HasDoorstop && HasBepInEx && HasCorlibOverride; }
        }

        /// <summary>Mods can be listed, which needs the loader present.</summary>
        public bool CanManageMods
        {
            get { return HasBepInEx; }
        }

        /// <summary>
        /// The install to work on.
        /// </summary>
        /// <remarks>
        /// Explicit path first, then the environment variable, then the usual Steam places. The
        /// environment variable is what an automated build wants: a runner has no Steam, so nothing
        /// else would resolve. Candidate names differ because the game moved between Playtest and
        /// release, and the folder is what identifies the install, not the executable.
        ///
        /// Unlike the setup scripts this does not throw when nothing is found. The app's job is to
        /// show the user their game is not where it was expected, which a stack trace cannot do.
        /// </remarks>
        public static GameInstall Resolve(string explicitPath)
        {
            // An explicit path means exactly that path. Nothing else is considered, and it is returned
            // even when it is not an install: the UI then shows that exact folder and says what is
            // missing, which is answerable. Substituting a Steam location that happened to exist
            // would mean quietly working on - or offering to set up - a game the user did not ask
            // about, and a typo in a path would silently succeed somewhere else entirely.
            if (!string.IsNullOrWhiteSpace(explicitPath)) return new GameInstall(explicitPath);

            // Same for SWYG_GAME_DIR: it is a person or a build agent naming a folder, so honour it
            // exactly rather than second-guessing it against the Steam list.
            var environment = Environment.GetEnvironmentVariable("SWYG_GAME_DIR");
            if (!string.IsNullOrWhiteSpace(environment)) return new GameInstall(environment);

            var tried = new List<string>();
            foreach (var candidate in SteamCandidates())
            {
                var install = new GameInstall(candidate);
                if (install.HasGame || install.HasBepInEx || install.HasDoorstop) return install;
                tried.Add(candidate);
            }

            // Nothing anywhere. Return the first location so the UI can show where it looked, which
            // is more use than an empty string.
            return new GameInstall(FirstNonEmpty(tried) ?? "");
        }

        private static IEnumerable<string> SteamCandidates()
        {
            var names = new[] { "Scam With Your Friends", "Scam With Your Friends Playtest" };
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + "\\Steam\\steamapps\\common",
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + "\\Steam\\steamapps\\common",
                Environment.GetEnvironmentVariable("LOCALAPPDATA") + "\\Steam\\steamapps\\common",
                "C:\\Program Files (x86)\\Steam\\steamapps\\common",
                "D:\\SteamLibrary\\steamapps\\common"
            };

            var all = new List<string>();
            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                foreach (var name in names) all.Add(System.IO.Path.Combine(root, name));
            }
            return all;
        }

        private static string FirstNonEmpty(IEnumerable<string> candidates)
        {
            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate)) return candidate;
            }
            return null;
        }

        /// <summary>A one-line summary for the status bar.</summary>
        public string Summary()
        {
            if (!Exists) return "game folder not found";
            if (!HasGame) return "not a game install (no Scam With Your Friends_Data\\Managed)";

            var missing = new List<string>();
            if (!HasDoorstop) missing.Add("loader");
            if (!HasBepInEx) missing.Add("BepInEx");
            if (!HasCorlibOverride) missing.Add("corlib override");

            return missing.Count == 0
                ? "ready"
                : "needs setup: " + string.Join(", ", missing.ToArray());
        }
    }
}
