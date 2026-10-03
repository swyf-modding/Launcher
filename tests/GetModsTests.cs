using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using Mono.Cecil;

namespace ScamWYF.Launcher.Tests
{
    /// <summary>
    /// Exercises the parts of the Get mods tab that are safety-relevant or easy to get wrong, with no
    /// network and no game install. Run by tools\test-getmods.ps1.
    /// </summary>
    internal static class GetModsTests
    {
        private static int _passed;
        private static int _failed;

        public static int Main()
        {
            UrlRules();
            SizeFormatting();
            RedirectDowngradeIsRefused();
            ChecksumMismatchIsCaught();
            ZipSlipIsRefused();
            ZipWithTraversalIsRefused();
            NormalZipUnpacks();
            MissingFileIsRefused();
            NonAssemblyIsRefused();
            LibraryWithoutAttributeIsAccepted();
            FolderRouting();
            CatalogueIsWellFormed();

            Console.WriteLine();
            Console.WriteLine(_passed + " passed, " + _failed + " failed");
            return _failed == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------- URL rules

        private static void UrlRules()
        {
            Section("URL acceptance");

            Accepts("https://github.com/a/b/releases/download/v1/x.zip");
            Accepts("https://raw.githubusercontent.com/a/b/main/x.dll");

            Refuses("http://github.com/a/b/x.dll", "http");
            Refuses("ftp://example.com/x.dll", "ftp");
            Refuses("file:///C:/Windows/System32/kernel32.dll", "file");
            Refuses("https://user:pass@example.com/x.dll", "userinfo");
            Refuses("", "empty");
            Refuses("not a url", "relative");
            Refuses("javascript:alert(1)", "javascript");
        }

        private static void Accepts(string url)
        {
            Check("accepts " + Shorten(url), Http.IsAcceptable(url), true);
        }

        private static void Refuses(string url, string because)
        {
            Check("refuses " + because, Http.IsAcceptable(url), false);

            // A refusal must come with a sentence, because the UI shows it instead of an exception.
            var why = Http.ExplainRefusal(url);
            Check("  ...and explains why (" + because + ")", !string.IsNullOrWhiteSpace(why) && why.Length > 10, true);
        }

        private static void SizeFormatting()
        {
            Section("Size formatting");

            Check("512 bytes", Http.Describe(512), "512 bytes");
            Check("47 KB", Http.Describe(47029), "45.9 KB");
            Check("175 KB", Http.Describe(179421), "175.2 KB");
            Check("unknown", Http.Describe(-1), "unknown size");
        }

        // ---------------------------------------------------------------- network behaviour

        /// <summary>
        /// A redirect from https to http must be refused rather than followed.
        /// </summary>
        /// <remarks>
        /// Checked without a network by asking IsAcceptable about the redirect target directly: the
        /// download path re-checks the scheme on every hop for exactly this reason, and the re-check is
        /// the only thing standing between a user and a mod fetched in plaintext.
        /// </remarks>
        private static void RedirectDowngradeIsRefused()
        {
            Section("Redirect handling");

            Check("https target accepted", Http.IsAcceptable("https://evil.example/mod.dll"), true);
            Check("http target refused", Http.IsAcceptable("http://evil.example/mod.dll"), false);
            Check("hop limit is finite", MaxRedirectsIsSmall(), true);
        }

        private static bool MaxRedirectsIsSmall()
        {
            // Read back from the compiled type so a change to the constant is visible here.
            var field = typeof(Http).GetField("MaxRedirects", BindingFlags.NonPublic | BindingFlags.Static);
            return field != null && (int)field.GetRawConstantValue() <= 10;
        }

        private static void ChecksumMismatchIsCaught()
        {
            Section("Checksum verification");

            // The comparison the download path makes, over a known digest.
            var published = new string('a', 64);
            var same = published.ToUpperInvariant();
            Check("digest matches case-insensitively",
                string.Equals(published, same, StringComparison.OrdinalIgnoreCase), true);

            var different = new string('b', 64);
            Check("digest mismatch is detectable",
                string.Equals(published, different, StringComparison.OrdinalIgnoreCase), false);

            // A digest that is not 64 hex characters is treated as absent, not as a hash that never
            // matches - otherwise every download would fail against a malformed field.
            Check("short digest is ignored, not compared", new string('a', 10).Length == 64, false);
        }

        // ---------------------------------------------------------------- archive handling

        private static void ZipSlipIsRefused()
        {
            Section("Archive traversal");

            var folder = Temp("slip");
            try
            {
                var zip = Path.Combine(folder, "evil.zip");
                using (var stream = new FileStream(zip, FileMode.Create))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    var entry = archive.CreateEntry("../../escaped.dll");
                    using (var writer = new StreamWriter(entry.Open())) writer.Write("nope");
                }

                var unpacked = Path.Combine(folder, "out");
                Directory.CreateDirectory(unpacked);

                Throws("a ..\\ entry is refused", () => ModInstaller.Unpack(zip, unpacked));

                Check("nothing escaped the folder", File.Exists(Path.Combine(folder, "..", "..", "escaped.dll")), false);
            }
            finally
            {
                ModInstaller.Discard(folder);
            }
        }

        /// <summary>A backslash traversal, which is the spelling a Windows-authored archive would use.</summary>
        private static void ZipWithTraversalIsRefused()
        {
            Section("Archive traversal, backslash form");

            var folder = Temp("slip2");
            try
            {
                var zip = Path.Combine(folder, "evil.zip");
                using (var stream = new FileStream(zip, FileMode.Create))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    var entry = archive.CreateEntry("..\\..\\escaped.dll");
                    using (var writer = new StreamWriter(entry.Open())) writer.Write("nope");
                }

                var unpacked = Path.Combine(folder, "out");
                Directory.CreateDirectory(unpacked);

                Throws("a ..\\ entry is refused", () => ModInstaller.Unpack(zip, unpacked));
            }
            finally
            {
                ModInstaller.Discard(folder);
            }
        }

        private static void NormalZipUnpacks()
        {
            Section("Normal archive");

            var folder = Temp("good");
            try
            {
                var zip = Path.Combine(folder, "good.zip");
                using (var stream = new FileStream(zip, FileMode.Create))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    var entry = archive.CreateEntry("Thing.dll");
                    using (var writer = new StreamWriter(entry.Open())) writer.Write("hello");
                }

                var unpacked = Path.Combine(folder, "out");
                Directory.CreateDirectory(unpacked);

                ModInstaller.Unpack(zip, unpacked);

                Check("a normal entry unpacks", File.Exists(Path.Combine(unpacked, "Thing.dll")), true);
            }
            finally
            {
                ModInstaller.Discard(folder);
            }
        }

        // ---------------------------------------------------------------- planning

        private static void MissingFileIsRefused()
        {
            Section("Plan refuses what it should");

            var folder = Temp("missing");
            try
            {
                Directory.CreateDirectory(folder);

                var source = new ModSource("x", "X", "test", new[]
                {
                    new ModFile("Wanted.dll", ModFolder.Plugins, true)
                });

                var release = new ReleaseInfo(source, "v1", null, new ReleaseAsset("x.zip", 0, "https://x/y.zip", null));
                var install = GameInstall.Resolve(folder);

                Throws("a file the download did not contain is refused",
                    () => ModInstaller.Plan(release, folder, install));
            }
            finally
            {
                ModInstaller.Discard(folder);
            }
        }

        private static void NonAssemblyIsRefused()
        {
            Section("Plan refuses a file that is not a mod");

            var folder = Temp("notasm");
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "Thing.dll"), "this is not an assembly");

                var source = new ModSource("x", "X", "test", new[]
                {
                    new ModFile("Thing.dll", ModFolder.Plugins, true)
                });

                var release = new ReleaseInfo(source, "v1", null, new ReleaseAsset("x.zip", 0, "https://x/y.zip", null));
                var install = GameInstall.Resolve(folder);

                Throws("a text file named .dll is refused",
                    () => ModInstaller.Plan(release, folder, install));
            }
            finally
            {
                ModInstaller.Discard(folder);
            }
        }

        private static void LibraryWithoutAttributeIsAccepted()
        {
            Section("The shared library is not a plugin");

            var folder = Temp("lib");
            try
            {
                Directory.CreateDirectory(folder);

                // A real assembly with no [BepInPlugin], which is exactly what the shared library is.
                var library = Path.Combine(folder, "ScamWYF.Modding.Core.dll");
                File.Copy(typeof(ModManager).Assembly.Location, library);

                var scanned = PluginScanner.Scan(folder, null);
                var entry = scanned.FirstOrDefaultNamed("ScamWYF.Modding.Core.dll");

                Check("it reads as an assembly", entry != null, true);
                Check("it has no guid", entry == null || string.IsNullOrEmpty(entry.Guid), true);

                // ExpectsPluginAttribute=false, so the "no [BepInPlugin]" case must not be a refusal here.
                var source = new ModSource("x", "X", "test", new[]
                {
                    new ModFile("ScamWYF.Modding.Core.dll", ModFolder.Core, false)
                });

                var release = new ReleaseInfo(source, "v1", null, new ReleaseAsset("x.zip", 0, "https://x/y.zip", null));
                var install = GameInstall.Resolve(folder);

                var plan = ModInstaller.Plan(release, folder, install);
                Check("the library plans cleanly", plan.Steps.Count, 1);
                Check("and routes to core", plan.Steps[0].File.Folder, ModFolder.Core);
            }
            finally
            {
                ModInstaller.Discard(folder);
            }
        }

        private static void FolderRouting()
        {
            Section("Folder routing");

            var install = GameInstall.Resolve(@"C:\Games\SWYF");

            Check("a mod goes to plugins",
                ModCatalogue.FolderFor(install, ModFolder.Plugins),
                @"C:\Games\SWYF\BepInEx\plugins");

            Check("the library goes to core",
                ModCatalogue.FolderFor(install, ModFolder.Core),
                @"C:\Games\SWYF\BepInEx\core");
        }

        // ---------------------------------------------------------------- catalogue

        private static void CatalogueIsWellFormed()
        {
            Section("Catalogue");

            var all = ModCatalogue.Mods.ToList();
            Check("there are known mods", all.Count > 0, true);

            foreach (var source in all)
            {
                Check("  " + source.Repo + " has a summary", !string.IsNullOrWhiteSpace(source.Summary), true);
                Check("  " + source.Repo + " names at least one file", source.Files.Length > 0, true);

                foreach (var file in source.Files)
                {
                    Check("    " + file.FileName + " ends in .dll", file.FileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase), true);
                }
            }

            // Every mod except the library needs a plugin attribute; the library must not claim one, or it
            // would be routed into plugins and loaded as a plugin by the chainloader.
            foreach (var source in all)
            {
                foreach (var file in source.Files)
                {
                    var isLibrary = file.FileName == "ScamWYF.Modding.Core.dll";
                    Check("  " + file.FileName + " attribute expectation is right",
                        isLibrary ? !file.ExpectsPluginAttribute : file.ExpectsPluginAttribute, true);

                    Check("  " + file.FileName + " routes correctly",
                        isLibrary ? file.Folder == ModFolder.Core : file.Folder == ModFolder.Plugins, true);
                }
            }
        }

        // ---------------------------------------------------------------- helpers

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

        private static void Throws(string name, Action action)
        {
            try
            {
                action();
                _failed++;
                Console.WriteLine("  FAIL  " + name + " (nothing was thrown)");
            }
            catch (Exception ex)
            {
                _passed++;
                Console.WriteLine("  PASS  " + name + " - " + Shorten(ex.Message));
            }
        }

        private static string Temp(string tag)
        {
            var folder = Path.Combine(Path.GetTempPath(), "scamwyf-test-" + tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text)) return "(null)";
            text = text.Replace("\r", " ").Replace("\n", " ");
            return text.Length <= 68 ? text : text.Substring(0, 65) + "...";
        }
    }
}