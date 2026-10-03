using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ScamWYF.Launcher
{
    /// <summary>One file that would be written, and what is already there.</summary>
    internal sealed class InstallStep
    {
        public ModFile File;
        public string SourcePath;
        public string DestinationPath;

        /// <summary>The version read out of the downloaded file's metadata, when it has one.</summary>
        public string Version;

        /// <summary>The name its [BepInPlugin] claims, when it has one.</summary>
        public string PluginName;

        public string Guid;

        /// <summary>Set when a file is already at the destination and would be replaced.</summary>
        public string ReplacingVersion;

        public bool IsReplacement { get { return ReplacingVersion != null; } }
    }

    /// <summary>
    /// Everything that would happen, decided before anything happens.
    /// </summary>
    /// <remarks>
    /// The plan is built and shown, and only then applied. A mod is code the game will load on the next
    /// launch, so the user is told what the files are, where they are going, and what is being replaced -
    /// with the name and version read out of the file itself rather than out of the URL, which is the only
    /// version worth trusting.
    /// </remarks>
    internal sealed class InstallPlan
    {
        public ReleaseInfo Release;
        public List<InstallStep> Steps = new List<InstallStep>();
        public List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// Fetching a mod, and putting it in the game folder.
    /// </summary>
    /// <remarks>
    /// The order is the whole design: download to a temporary folder, unpack, read the metadata with Cecil,
    /// build a plan, show it, and only then copy. Nothing is written into the game folder until a person
    /// has seen what is about to be written, and nothing is ever loaded to find out what a file is -
    /// <see cref="PluginScanner"/> exists precisely so that a mod's code does not run in this process.
    ///
    /// Installing is a copy rather than a move, and the temporary folder is left for the caller to delete.
    /// A copy leaves the downloaded artefact alone, which is what makes it safe to re-run: a second
    /// attempt re-downloads rather than half-installing.
    /// </remarks>
    internal static class ModInstaller
    {
        /// <summary>Where downloads are unpacked. Under TEMP, so it is not inside the game folder.</summary>
        internal static string WorkingFolder(string token)
        {
            var folder = Path.Combine(Path.GetTempPath(), "scamwyf-mods-" + token);
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>Throw away a working folder, ignoring whatever is still held open.</summary>
        internal static void Discard(string folder)
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            catch (Exception)
            {
                // A leftover temp folder is untidy, not dangerous, and failing an install over it would
                // be the wrong trade.
            }
        }

        /// <summary>What a fetch produced, and where it put it.</summary>
    internal sealed class Fetched
    {
        /// <summary>
        /// The temp folder holding the zip and the unpacked copy. This is what Discard takes.
        /// </summary>
        /// <remarks>
        /// Deliberately not the unpacked subfolder. Discarding that leaves the downloaded zip and its
        /// parent behind, so every install would quietly add a copy to %TEMP% and nothing would ever
        /// reclaim it - which is the unbounded growth this project's own docs call out as the reason a
        /// delete has to actually delete.
        /// </remarks>
        public string WorkingFolder;

        public string UnpackedFolder;

        public string Sha256;
    }

    /// <summary>
    /// Download a release's zip and unpack it.
    /// </summary>
    public static Fetched Fetch(ReleaseAsset asset, string token, Action<string> status)
    {
        var folder = WorkingFolder(token);

        if (status != null) status("Downloading " + asset.Name + " (" + Http.Describe(asset.Size) + ")...");

        var zipPath = Path.Combine(folder, asset.Name);
        var actual = Http.Download(asset.DownloadUrl, zipPath, null);

        // GitHub's digest is from the same place as the file, so this catches a mangled download and a
        // CDN that served something else - not a compromised release. Reported either way, because a
        // number the user can check is worth more than silence.
        if (!string.IsNullOrEmpty(asset.Sha256) &&
            !string.Equals(asset.Sha256, actual, StringComparison.OrdinalIgnoreCase))
        {
            Discard(folder);
            throw new IOException("the download does not match the checksum GitHub published for it, so it " +
                                  "has been discarded and nothing was installed. Either the download was " +
                                  "corrupted or the file served was not the one published");
        }

        if (status != null) status("Unpacking...");

        var unpacked = Path.Combine(folder, "unpacked");
        Directory.CreateDirectory(unpacked);
        Unpack(zipPath, unpacked);

        return new Fetched
        {
            WorkingFolder = folder,
            UnpackedFolder = unpacked,
            Sha256 = actual
        };
    }

        /// <summary>
        /// Unpack a zip, refusing any entry that would land outside the destination.
        /// </summary>
        /// <remarks>
        /// Zip-slip: an entry named "..\..\Windows\System32\..." is legal in the archive format and would
        /// otherwise write wherever it liked. Every entry's full path is checked against the destination
        /// before anything is written, so a hostile archive cannot escape the temp folder - let alone the
        /// game directory.
        /// </remarks>
        internal static void Unpack(string zipPath, string destination)
        {
            var root = Path.GetFullPath(destination);
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                root += Path.DirectorySeparatorChar;
            }

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    var target = Path.GetFullPath(Path.Combine(root, entry.FullName));

                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("the archive contains an entry that would write outside the " +
                                              "unpack folder (" + entry.FullName + "). It was refused");
                    }

                    if (entry.Length == 0 && entry.Name.Length == 0)
                    {
                        // A directory entry.
                        Directory.CreateDirectory(target);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target));

                    // ExtractToFile rather than ExtractToDirectory, so the per-entry check above is the
                    // one that decides.
                    entry.ExtractToFile(target, true);
                }
            }
        }

        /// <summary>
        /// Work out what installing this would do, and refuse anything that should not be installed.
        /// </summary>
        /// <remarks>
        /// Refusals are refused rather than installed-with-a-warning, because a file that is not what the
        /// catalogue said it was is either a broken download or something that should not be near a game
        /// folder. Either way the answer is the same: stop and say why.
        /// </remarks>
        public static InstallPlan Plan(ReleaseInfo release, string unpackedFolder, GameInstall install)
        {
            if (release == null) throw new ArgumentNullException("release");
            if (install == null) throw new ArgumentNullException("install");

            var plan = new InstallPlan { Release = release };

            foreach (var wanted in release.Source.Files)
            {
                var source = Path.Combine(unpackedFolder, wanted.FileName);

                if (!File.Exists(source))
                {
                    throw new IOException("the download did not contain " + wanted.FileName + ". " +
                                          DescribeAvailable(unpackedFolder));
                }

                // The same reader the Mods tab uses, so a file that would list badly is caught before it
                // is installed rather than after. Its code is not executed.
                var entry = PluginScanner.Scan(Path.GetDirectoryName(source), null)
                    .FirstOrDefaultNamed(wanted.FileName);

                if (entry == null)
                {
                    throw new IOException(wanted.FileName + " could not be read as a .NET assembly, so it was " +
                                          "not installed");
                }

                if (!entry.IsAssembly)
                {
                    throw new IOException(wanted.FileName + " could not be read: " + entry.LoadError);
                }

                if (wanted.ExpectsPluginAttribute && string.IsNullOrEmpty(entry.Guid))
                {
                    throw new IOException(wanted.FileName + " has no [BepInPlugin] attribute, which means " +
                                          "BepInEx would not load it. It was not installed");
                }

                var destination = Path.Combine(
                    ModCatalogue.FolderFor(install, wanted.Folder), wanted.FileName);

                var step = new InstallStep
                {
                    File = wanted,
                    SourcePath = source,
                    DestinationPath = destination,
                    Version = entry.DisplayVersion,
                    PluginName = entry.DisplayName,
                    Guid = entry.Guid
                };

                if (File.Exists(destination))
                {
                    // What is there now, read the same way, so the comparison is like for like.
                    var existing = PluginScanner.Scan(Path.GetDirectoryName(destination), null)
                        .FirstOrDefaultNamed(wanted.FileName);

                    step.ReplacingVersion = existing != null && !string.IsNullOrEmpty(existing.DisplayVersion)
                        ? existing.DisplayVersion
                        : "a copy with no version";

                    if (string.Equals(step.ReplacingVersion, step.Version, StringComparison.Ordinal))
                    {
                        plan.Warnings.Add(wanted.FileName + " is already version " + step.Version +
                                          ". Reinstalling changes nothing.");
                    }
                }

                plan.Steps.Add(step);
            }

            if (plan.Steps.Count == 0) throw new IOException("there was nothing in the download to install");

            return plan;
        }

        /// <summary>
        /// Carry out a plan.
        /// </summary>
        /// <remarks>
        /// The only method that writes into the game folder. Everything it does was shown first.
        /// </remarks>
        public static string[] Apply(InstallPlan plan)
        {
            var written = new List<string>();

            foreach (var step in plan.Steps)
            {
                var folder = Path.GetDirectoryName(step.DestinationPath);
                Directory.CreateDirectory(folder);

                File.Copy(step.SourcePath, step.DestinationPath, true);
                written.Add(Path.GetFileName(step.DestinationPath));
            }

            return written.ToArray();
        }

        /// <summary>Whether anything would be replaced, which is what the confirmation is about.</summary>
        public static bool ReplacesAnything(InstallPlan plan)
        {
            foreach (var step in plan.Steps)
            {
                if (step.IsReplacement) return true;
            }
            return false;
        }

        /// <summary>A multi-line description of the plan, for the confirmation box.</summary>
        public static string Describe(InstallPlan plan)
        {
            var text = new StringBuilder();

            text.AppendLine(plan.Release.Source.DisplayName + "  " + plan.Release.TagName);
            text.AppendLine();

            foreach (var step in plan.Steps)
            {
                var folder = step.File.Folder == ModFolder.Core ? "BepInEx\\core" : "BepInEx\\plugins";
                var identity = string.IsNullOrEmpty(step.PluginName)
                    ? step.File.FileName
                    : step.PluginName + "  " + step.Version;

                text.AppendLine("  " + identity);
                text.AppendLine("    -> " + folder + "\\" + step.File.FileName);

                if (step.IsReplacement)
                {
                    text.AppendLine("    replaces " + step.ReplacingVersion);
                }
            }

            text.AppendLine();
            text.AppendLine("Files come from " + plan.Release.Package.Name + " on GitHub, " +
                            Http.Describe(plan.Release.Package.Size) + ".");

            if (plan.Release.Package.Sha256 != null)
            {
                text.AppendLine("SHA-256 " + plan.Release.Package.Sha256);
            }

            return text.ToString();
        }

        private static string DescribeAvailable(string folder)
        {
            var names = new List<string>();
            foreach (var file in Directory.GetFiles(folder)) names.Add(Path.GetFileName(file));
            names.Sort();

            return names.Count == 0
                ? "The download was empty."
                : "It contained: " + string.Join(", ", names.ToArray());
        }
    }

    /// <summary>Small helper so the installer can pick one file out of a scan without a Linq predicate dance.</summary>
    internal static class PluginEntryLookup
    {
        public static PluginEntry FirstOrDefaultNamed(this List<PluginEntry> entries, string fileName)
        {
            foreach (var entry in entries)
            {
                if (string.Equals(Path.GetFileName(entry.Path), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }
            return null;
        }
    }
}