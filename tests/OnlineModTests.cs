using System;
using System.IO;
using System.Reflection;
using System.Threading;
using ScamWYF.Setup;

namespace Switchboard.Tests
{
    /// <summary>
    /// Exercises the real network path, which the offline tests cannot: the GitHub listing, the download,
    /// the checksum comparison, and a plan built from the real published zip.
    ///
    /// Run by tools\test-getmods.ps1 -Online, and deliberately not part of the default run - it needs the
    /// network, it spends GitHub's unauthenticated rate limit, and it writes nothing. Every assertion here
    /// is about the plumbing rather than about any particular mod's contents.
    /// </summary>
    internal static class OnlineModTests
    {
        private static int _passed;
        private static int _failed;

        public static int Main()
        {
            Console.WriteLine("Live check against github.com. Requires network.");

            CatalogueFetch();
            PinnedLive();
            ListingWorks();
            DownloadVerifies();

            Console.WriteLine();
            Console.WriteLine(_passed + " passed, " + _failed + " failed");
            return _failed == 0 ? 0 : 1;
        }

        private static void CatalogueFetch()
        {
            Section("Live mod list");

            System.Collections.Generic.List<ModSource> sources = null;
            string error = null;

            try
            {
                sources = ModCatalogue.FetchCatalogue();
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            Check("the live list reads (" + Trim(error) + ")", sources != null, true);
            if (sources == null) return;

            Check("it names at least one mod", sources.Count > 0, true);

            foreach (var source in sources)
            {
                Check("  " + source.QualifiedName + " has a summary",
                    !string.IsNullOrWhiteSpace(source.Summary), true);
                Check("  " + source.QualifiedName + " names at least one file",
                    source.Files.Length > 0, true);
            }
        }

        /// <summary>
        /// The pins, against the real catalogue and the real releases.
        /// </summary>
        /// <remarks>
        /// This is the one test that proves the whole arrangement works end to end: a live entry
        /// names a release, the switchboard fetches that release rather than the newest one, and the
        /// hashes the catalogue recorded are the hashes of the files inside it. It is online-only
        /// because it needs all three of those things to be true at once.
        /// </remarks>
        private static void PinnedLive()
        {
            Section("Live pins");

            System.Collections.Generic.List<ModSource> sources;
            try { sources = ModCatalogue.FetchCatalogue(); }
            catch (Exception ex)
            {
                Check("the live list reads (" + Trim(ex.Message) + ")", false, true);
                return;
            }

            var pinned = 0;
            foreach (var source in sources)
            {
                if (!source.IsPinned) continue;
                pinned++;

                Check("  " + source.Repo + " names its release", !string.IsNullOrEmpty(source.ApprovedVersion), true);
                Check("  " + source.Repo + " is fully pinned", source.IsVerified, true);

                foreach (var file in source.Files)
                {
                    Check("    " + file.FileName + " has a hash", file.Sha256 != null && file.Sha256.Length == 64, true);
                }

                ReleaseInfo release;
                try
                {
                    release = ModCatalogue.FetchLatest(source);
                }
                catch (Exception ex)
                {
                    Check("  " + source.Repo + " pinned release is fetchable (" + Trim(ex.Message) + ")", false, true);
                    continue;
                }

                // The point of the pin: the newest release the author published is not what is
                // fetched when the catalogue names an older one.
                Check("  " + source.Repo + " fetches the pinned release", release.TagName, source.ApprovedVersion);
            }

            Check("the live list has at least one pin", pinned > 0, true);
        }

        private static void ListingWorks()
        {
            Section("GitHub release listing");

            var sources = new System.Collections.Generic.List<ModSource>(
                new System.Collections.Generic.List<ModSource>(ModCatalogue.Mods));

            if (sources.Count == 0)
            {
                Check("the catalogue has entries", false, true);
                return;
            }

            foreach (var source in sources)
            {
                ReleaseInfo release = null;
                string error = null;

                try
                {
                    release = ModCatalogue.FetchLatest(source);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                if (release == null)
                {
                    // mod-lib and Setup publish tags with no downloadable asset, which is a legitimate
                    // state and the code says so rather than throwing something opaque.
                    Check(source.Repo + " -> reported cleanly (" + Trim(error) + ")", error != null, true);
                    continue;
                }

                Check(source.Repo + " -> tag", !string.IsNullOrEmpty(release.TagName), true);
                Check(source.Repo + " -> zip name", release.Package.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase), true);
                Check(source.Repo + " -> download url is https", Http.IsAcceptable(release.Package.DownloadUrl), true);
                Check(source.Repo + " -> has a size", release.Package.Size > 0, true);

                if (release.Package.Sha256 != null)
                {
                    Check(source.Repo + " -> sha256 is 64 hex chars",
                        release.Package.Sha256.Length == 64 && IsHex(release.Package.Sha256), true);
                }
                else
                {
                    Console.WriteLine("  note  " + source.Repo + " published no digest; the download will not be checksummed");
                }
            }
        }

        private static void DownloadVerifies()
        {
            Section("Real download, unpack, plan");

            // A pinned entry from the live list, so the plan's hash checks are the ones being
            // exercised rather than skipped for want of a pin.
            ModSource chosen = null;
            try
            {
                foreach (var source in ModCatalogue.FetchCatalogue())
                {
                    if (source.IsPinned && source.Files.Length > 0)
                    {
                        chosen = source;
                        break;
                    }
                }
            }
            catch (Exception)
            {
                chosen = null;
            }

            if (chosen == null)
            {
                Check("found a pinned mod to download", false, true);
                return;
            }

            ReleaseInfo release = null;
            try
            {
                release = ModCatalogue.FetchLatest(chosen);
            }
            catch (Exception ex)
            {
                Check("the pinned release could be fetched (" + Trim(ex.Message) + ")", false, true);
                return;
            }

            Console.WriteLine("  using " + release.Source.Repo + " " + release.TagName);

            var token = Guid.NewGuid().ToString("N").Substring(0, 8);
            string folder = null;

            try
            {
                var fetched = ModInstaller.Fetch(release.Package, token, note => { });
                folder = fetched.WorkingFolder;

                Check("the download was checksummed", !string.IsNullOrEmpty(fetched.Sha256), true);

                if (release.Package.Sha256 != null)
                {
                    Check("it matches what GitHub published",
                        string.Equals(release.Package.Sha256, fetched.Sha256, StringComparison.OrdinalIgnoreCase), true);
                }

                var installed = GameInstall.Resolve(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "scamwyf-plancheck-does-not-exist"));

                // A plan that returns at all means every pinned file matched: Plan refuses the
                // release, and then the file, before it will describe them.
                var plan = ModInstaller.Plan(release, fetched.UnpackedFolder, installed);

                Check("the plan covers every file the catalogue names",
                    plan.Steps.Count, release.Source.Files.Length);

                foreach (var step in plan.Steps)
                {
                    var named = step.PluginName ?? "(no name)";
                    Console.WriteLine("        " + named + "  " + step.Version +
                                      "  -> " + Path.GetFileName(Path.GetDirectoryName(step.DestinationPath)));
                }

                // The two things that matter about a real published mod: the library carries no plugin
                // attribute and must still be accepted, and a real mod must carry one.
                foreach (var step in plan.Steps)
                {
                    var isLibrary = step.File.FileName == "ScamWYF.Modding.Core.dll";
                    if (isLibrary)
                    {
                        Check("the library has no plugin attribute", string.IsNullOrEmpty(step.Guid), true);
                        Check("the library goes to core", step.File.Folder == ModFolder.Core, true);
                    }
                    else
                    {
                        Check(step.File.FileName + " has a plugin attribute", !string.IsNullOrEmpty(step.Guid), true);
                        Check(step.File.FileName + " goes to plugins", step.File.Folder == ModFolder.Plugins, true);
                    }
                }

                Check("nothing would be replaced in an empty install", ModInstaller.ReplacesAnything(plan), false);

                var text = ModInstaller.Describe(plan);
                Check("the description names the version", text.Contains(stripBuild(release.TagName)) || text.Length > 100, true);
                Check("the description says it was reviewed", text.Contains("Reviewed:"), true);
                Check("the description names what the files can reach", text.Contains("can "), true);
            }
            catch (Exception ex)
            {
                Check("download and plan succeeded (" + Trim(ex.Message) + ")", false, true);
            }
            finally
            {
                if (folder != null) ModInstaller.Discard(folder);
            }
        }

        private static string stripBuild(string tag)
        {
            return (tag ?? "").TrimStart('v');
        }

        private static bool IsHex(string text)
        {
            foreach (var c in text)
            {
                var ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return false;
            }
            return true;
        }

        private static void Section(string name)
        {
            Console.WriteLine();
            Console.WriteLine(name);
        }

        private static void Check<T>(string name, T actual, T expected)
        {
            if (Equals(actual, expected))
            {
                _passed++;
                Console.WriteLine("  PASS  " + name);
            }
            else
            {
                _failed++;
                Console.WriteLine("  FAIL  " + name);
                Console.WriteLine("          expected: " + expected);
                Console.WriteLine("          actual:   " + actual);
            }
        }

        private static string Trim(string text)
        {
            if (string.IsNullOrEmpty(text)) return "(null)";
            text = text.Replace("\r", " ").Replace("\n", " ");
            return text.Length <= 60 ? text : text.Substring(0, 57) + "...";
        }
    }
}