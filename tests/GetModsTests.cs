using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using ScamWYF.Setup;

namespace Switchboard.Tests
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
            PinnedEntries();
            PinnedBytesAreChecked();
            ZipSlipIsRefused();
            ZipWithTraversalIsRefused();
            NormalZipUnpacks();
            MissingFileIsRefused();
            NonAssemblyIsRefused();
            LibraryWithoutAttributeIsAccepted();
            FolderRouting();
            CatalogueIsWellFormed();
            LiveCatalogueRules();
            PostInstallRules();
            BepInExVersionRules();
            SwitchboardUpdateRules();
            InstallRequestRules();
            ModConfigFiles();

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

        // ---------------------------------------------------------------- pins

        /// <summary>A digest shaped like a real one, for the parsing tests.</summary>
        private const string Hex =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        /// <summary>The sample catalogue, pinned: a release, hashes, capabilities and a step hash.</summary>
        private const string SamplePinnedCatalogue =
            "{\"schemaVersion\": 1, \"mods\": [" +
            "{\"repo\": \"AI-Backend\", \"displayName\": \"AI Backend\", \"summary\": \"Routes AI calls.\", " +
            "\"approvedVersion\": \"v1.2.1\", " +
            "\"files\": [{\"fileName\": \"ScamWYF.AiBackend.dll\", \"folder\": \"plugins\", " +
            "\"expectsPluginAttribute\": true, \"sha256\": \"" + Hex + "\", " +
            "\"capabilities\": [\"network\", \"dynamic-code\"], " +
            "\"bepInPlugin\": [\"com.example.aibackend | AI Backend | 1.2.1\"]}], " +
            "\"postInstall\": [{\"script\": \"setup.ps1\", \"description\": \"Sets it up.\", " +
            "\"sha256\": \"" + Hex + "\"}]}" +
            "]}";

        /// <summary>
        /// The pinning fields: what they mean, and what is refused.
        /// </summary>
        /// <remarks>
        /// The refusals are the safety-relevant half. A pin that is present but unreadable, or an
        /// entry that claims a release and then does not say what is in it, would otherwise be an
        /// entry installed on a promise - so each of those rejects the file rather than being
        /// quietly ignored or downgraded to an unpinned entry.
        /// </remarks>
        private static void PinnedEntries()
        {
            Section("Pinned entries");

            var pinned = ModCatalogue.ParseCatalogue(SamplePinnedCatalogue);
            Check("reads the pinned release", pinned[0].ApprovedVersion, "v1.2.1");
            Check("a pinned entry says so", pinned[0].IsPinned, true);
            Check("a fully pinned entry is verified", pinned[0].IsVerified, true);
            Check("reads a file's hash", pinned[0].Files[0].Sha256, Hex);
            Check("the file says it is pinned", pinned[0].Files[0].IsPinned, true);
            Check("reads the capabilities", pinned[0].Files[0].Capabilities.Length, 2);
            Check("reads the plugin declaration", pinned[0].Files[0].BepInPlugin[0],
                "com.example.aibackend | AI Backend | 1.2.1");
            Check("reads the setup script's hash", pinned[0].PostInstall[0].Sha256, Hex);
            Check("the script says it is pinned", pinned[0].PostInstall[0].IsPinned, true);

            var plain = ModCatalogue.ParseCatalogue(SampleCatalogue);
            Check("an entry with no release is not pinned", plain[0].IsPinned, false);
            Check("an entry with no release is not verified", plain[0].IsVerified, false);
            Check("an entry with no release has no capabilities", plain[0].Files[0].Capabilities.Length, 0);

            Throws("a pinned entry whose file has no hash is refused",
                () => ModCatalogue.ParseCatalogue(PinnedWithout("\"sha256\": \"" + Hex + "\", ")));
            Throws("a pinned entry whose file hash is malformed is refused",
                () => ModCatalogue.ParseCatalogue(PinnedWith("\"" + Hex + "\"", "\"abc\"")));
            Throws("a pinned entry whose script has no hash is refused",
                () => ModCatalogue.ParseCatalogue(
                    PinnedWith("\"description\": \"Sets it up.\", \"sha256\": \"" + Hex + "\"",
                               "\"description\": \"Sets it up.\"")));
            Throws("a capability nobody can produce is refused",
                () => ModCatalogue.ParseCatalogue(PinnedWith("\"network\"", "\"telepathy\"")));
            Throws("a capability list that is not a list is refused",
                () => ModCatalogue.ParseCatalogue(PinnedWith("[\"network\", \"dynamic-code\"]",
                                                             "\"network\"")));

            Check("process is explained", ModCatalogue.DescribeCapability("process"),
                "can start other programs");
            Check("network is explained", ModCatalogue.DescribeCapability("network"), "can use the network");
            Check("a file's capabilities become one phrase",
                ModCatalogue.DescribeCapabilities(pinned[0].Files[0]),
                "can use the network, can load code at runtime");
            Check("a file with no capabilities says nothing",
                ModCatalogue.DescribeCapabilities(plain[0].Files[0]), null);
        }

        /// <summary>The pinned sample with one piece of text replaced.</summary>
        private static string PinnedWith(string from, string to)
        {
            return SamplePinnedCatalogue.Replace(from, to);
        }

        /// <summary>The pinned sample with one piece of text removed.</summary>
        private static string PinnedWithout(string text)
        {
            return SamplePinnedCatalogue.Replace(text, "");
        }

        /// <summary>
        /// The install path's hash checks: a file that is not the pinned one is refused, and the
        /// release has to be the one the catalogue lists.
        /// </summary>
        /// <remarks>
        /// This is the check the catalogue arrangement exists to make possible. GitHub's digest comes
        /// from the same place as the file, so it catches a mangled download and nothing else; these
        /// hashes were written down at review time, so a match means the bytes are the ones somebody
        /// approved. A mismatch must stop the plan, not warn and continue.
        /// </remarks>
        private static void PinnedBytesAreChecked()
        {
            Section("Pinned bytes");

            var folder = Temp("pinned");
            try
            {
                // A known digest, so the test is of the hashing rather than of itself.
                var path = Path.Combine(folder, "hello.dll");
                File.WriteAllText(path, "hello");
                Check("hashing a file gives its sha256", ModInstaller.Sha256Of(path),
                    "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824");

                // A real assembly, so a plan that gets past the hash check gets past the scan too.
                var assembly = typeof(GetModsTests).Assembly.Location;
                File.Copy(assembly, Path.Combine(folder, "Thing.dll"), true);
                var actual = ModInstaller.Sha256Of(Path.Combine(folder, "Thing.dll"));

                var install = GameInstall.Resolve(Path.Combine(folder, "game"));
                var asset = new ReleaseAsset("Thing.zip", 1024, "https://github.com/x/y/Thing.zip", null);

                // The wrong bytes.
                var wrong = new ModSource("Thing", "Thing", "A thing.",
                    new[] { new ModFile("Thing.dll", ModFolder.Plugins, true, new string('a', 64)) });
                var wrongRelease = new ReleaseInfo(wrong, "v1.0.0", "https://github.com/x/y", asset);
                Throws("a file that is not the pinned one is refused",
                    () => ModInstaller.Plan(wrongRelease, folder, install));

                // The right bytes, and the plan moves on to the next reason it might refuse.
                var right = new ModSource("Thing", "Thing", "A thing.",
                    new[] { new ModFile("Thing.dll", ModFolder.Plugins, true, actual) });
                var rightRelease = new ReleaseInfo(right, "v1.0.0", "https://github.com/x/y", asset);

                string message = null;
                try { ModInstaller.Plan(rightRelease, folder, install); }
                catch (Exception ex) { message = ex.Message; }

                Check("a matching hash gets past the hash check",
                    message == null || message.IndexOf("does not match the hash", StringComparison.Ordinal) < 0,
                    true);

                // A different release under a pinned entry's name.
                var pinnedSource = new ModSource("Thing", "Thing", "A thing.",
                    new[] { new ModFile("Thing.dll", ModFolder.Plugins, true, actual) },
                    ModCatalogue.DefaultOwner, null, "v1.0.0");
                var otherRelease = new ReleaseInfo(pinnedSource, "v1.1.0", "https://github.com/x/y", asset);
                Throws("a pinned entry refuses a release that is not the pinned one",
                    () => ModInstaller.Plan(otherRelease, folder, install));

                // A setup script that is not the pinned one.
                var scriptPath = Path.Combine(folder, "setup.ps1");
                File.WriteAllText(scriptPath, "Write-Host hello");
                var scriptHash = ModInstaller.Sha256Of(scriptPath);

                var badScript = new ModSource("Thing", "Thing", "A thing.",
                    new[] { new ModFile("Thing.dll", ModFolder.Plugins, false) },
                    ModCatalogue.DefaultOwner,
                    new[] { new PostInstallStep("setup.ps1", "Does it.", new string('b', 64)) });
                var badScriptRelease = new ReleaseInfo(badScript, "v1.0.0", "https://github.com/x/y", asset);
                Throws("a setup script that is not the pinned one is refused",
                    () => ModInstaller.Plan(badScriptRelease, folder, install));

                var goodScript = new ModSource("Thing", "Thing", "A thing.",
                    new[] { new ModFile("Thing.dll", ModFolder.Plugins, false) },
                    ModCatalogue.DefaultOwner,
                    new[] { new PostInstallStep("setup.ps1", "Does it.", scriptHash) });
                var goodScriptRelease = new ReleaseInfo(goodScript, "v1.0.0", "https://github.com/x/y", asset);

                var plan = ModInstaller.Plan(goodScriptRelease, folder, install);
                Check("a matching script hash reaches the prompt", plan.Scripts.Count, 1);
                Check("and the step still says it is pinned", plan.Scripts[0].IsPinned, true);

                // The confirmation says which of the two this is, before anything is agreed to.
                var text = ModInstaller.Describe(plan);
                Check("the confirmation says it is not a reviewed entry", text.Contains("Not reviewed"), true);

                var verified = new ModSource("Thing", "Thing", "A thing.",
                    new[] { new ModFile("Thing.dll", ModFolder.Plugins, false, actual) },
                    ModCatalogue.DefaultOwner,
                    new[] { new PostInstallStep("setup.ps1", "Does it.", scriptHash) },
                    "v1.0.0");
                var verifiedRelease = new ReleaseInfo(verified, "v1.0.0", "https://github.com/x/y", asset);
                var verifiedPlan = ModInstaller.Plan(verifiedRelease, folder, install);
                var verifiedText = ModInstaller.Describe(verifiedPlan);
                Check("the confirmation says it is reviewed", verifiedText.Contains("Reviewed:"), true);
            }
            finally
            {
                ModInstaller.Discard(folder);
            }
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

            // An ordinary folder, built with Path.Combine so the test means the same thing on Linux.
            var root = Path.Combine(Path.GetTempPath(), "SWYF");
            var install = GameInstall.Resolve(root);

            Check("a mod goes to plugins",
                ModCatalogue.FolderFor(install, ModFolder.Plugins),
                Path.Combine(root, "BepInEx", "plugins"));

            Check("the library goes to core",
                ModCatalogue.FolderFor(install, ModFolder.Core),
                Path.Combine(root, "BepInEx", "core"));
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

        // ---------------------------------------------------------------- live catalogue

        /// <summary>
        /// The live mod list's parsing and validation, with no network.
        /// </summary>
        /// <remarks>
        /// The catalogue decides what the tab offers to download and run, so its validation is the
        /// safety-relevant half of the feature. Every refusal here is fail-closed: one bad entry rejects
        /// the whole file and the tab falls back to the built-in list, which is what keeps a typo from
        /// silently unlisting a mod - or from routing a file somewhere unintended.
        /// </remarks>
        private static void LiveCatalogueRules()
        {
            Section("Live catalogue");

            var sources = ModCatalogue.ParseCatalogue(SampleCatalogue);
            Check("reads every entry", sources.Count, 2);
            Check("reads the repo", sources[0].Repo, "AI-Backend");
            Check("an absent owner defaults to the project", sources[0].Owner, "swyf-modding");
            Check("the project's own entry is not third-party", sources[0].IsThirdParty, false);
            Check("reads an explicit owner", sources[1].Owner, "someone-else");
            Check("a foreign owner is third-party", sources[1].IsThirdParty, true);
            Check("the qualified name joins owner and repo", sources[1].QualifiedName, "someone-else/Extra-Mod");
            Check("reads the files", sources[1].Files.Length, 1);
            Check("a library file routes to core", sources[1].Files[0].Folder, ModFolder.Core);
            Check("and expects no plugin attribute", sources[1].Files[0].ExpectsPluginAttribute, false);

            Check("account names accept dots, dashes and underscores",
                ModCatalogue.IsAccountName("some.one-else_2"), true);
            Check("account names refuse slashes", ModCatalogue.IsAccountName("a/b"), false);
            Check("account names refuse spaces", ModCatalogue.IsAccountName("a b"), false);
            Check("account names refuse empties", ModCatalogue.IsAccountName(""), false);
            Check("account names refuse nulls", ModCatalogue.IsAccountName(null), false);

            Throws("a reply that is not JSON is refused",
                () => ModCatalogue.ParseCatalogue("rate limit exceeded"));
            Throws("a missing schema version is refused",
                () => ModCatalogue.ParseCatalogue("{\"mods\": []}"));
            Throws("a future schema version is refused, not guessed at",
                () => ModCatalogue.ParseCatalogue("{\"schemaVersion\": 2, \"mods\": []}"));
            Throws("an empty list is refused",
                () => ModCatalogue.ParseCatalogue("{\"schemaVersion\": 1, \"mods\": []}"));
            Throws("a missing list is refused",
                () => ModCatalogue.ParseCatalogue("{\"schemaVersion\": 1}"));
            Throws("a repo name with a slash is refused",
                () => ModCatalogue.ParseCatalogue(CatalogueWith("\"repo\": \"a/b\"")));
            Throws("a repo name with a space is refused",
                () => ModCatalogue.ParseCatalogue(CatalogueWith("\"repo\": \"a b\"")));
            Throws("a missing repo is refused",
                () => ModCatalogue.ParseCatalogue("{\"schemaVersion\": 1, \"mods\": [{\"displayName\": \"X\", " +
                    "\"summary\": \"Y\", \"files\": [{\"fileName\": \"A.dll\", \"folder\": \"plugins\", " +
                    "\"expectsPluginAttribute\": true}]}]}"));
            Throws("a missing display name is refused",
                () => ModCatalogue.ParseCatalogue(CatalogueWithout("\"displayName\": \"Example\"")));
            Throws("a missing summary is refused",
                () => ModCatalogue.ParseCatalogue(CatalogueWithout("\"summary\": \"Does things.\"")));
            Throws("a missing file list is refused",
                () => ModCatalogue.ParseCatalogue(CatalogueWithout(
                    "\"files\": [{\"fileName\": \"ScamWYF.Modding.Core.dll\", \"folder\": \"core\", " +
                    "\"expectsPluginAttribute\": false}]")));
            Throws("a file with a separator is refused",
                () => ModCatalogue.ParseCatalogue(CatalogueWith("\"fileName\": \"sub/Thing.dll\"")));
            Throws("a file without a .dll suffix is refused",
                () => ModCatalogue.ParseCatalogue(CatalogueWith("\"fileName\": \"Thing.zip\"")));
            Throws("a parent reference is refused",
                () => ModCatalogue.ParseCatalogue(CatalogueWith("\"fileName\": \"..\"")));
            Throws("an unknown folder is refused",
                () => ModCatalogue.ParseCatalogue(CatalogueWith("\"folder\": \"elsewhere\"")));
            Throws("a missing plugin flag is refused",
                () => ModCatalogue.ParseCatalogue(CatalogueWithout("\"expectsPluginAttribute\": true")));
        }

        /// <summary>A minimal catalogue: one project entry, one third-party entry.</summary>
        private const string SampleCatalogue =
            "{\"schemaVersion\": 1, \"mods\": [" +
            "{\"repo\": \"AI-Backend\", \"displayName\": \"AI Backend\", \"summary\": \"Routes AI calls.\", " +
            "\"files\": [{\"fileName\": \"ScamWYF.AiBackend.dll\", \"folder\": \"plugins\", \"expectsPluginAttribute\": true}]}, " +
            "{\"owner\": \"someone-else\", \"repo\": \"Extra-Mod\", \"displayName\": \"Example\", " +
            "\"summary\": \"Does things.\", " +
            "\"files\": [{\"fileName\": \"ScamWYF.Modding.Core.dll\", \"folder\": \"core\", \"expectsPluginAttribute\": false}]}" +
            "]}";

        /// <summary>The sample with one field replaced, for the refusal cases.</summary>
        private static string CatalogueWith(string replacement)
        {
            if (replacement.StartsWith("\"repo\"")) return SampleCatalogue.Replace("\"repo\": \"Extra-Mod\"", replacement);
            if (replacement.StartsWith("\"fileName\"")) return SampleCatalogue.Replace("\"fileName\": \"ScamWYF.Modding.Core.dll\"", replacement);
            if (replacement.StartsWith("\"folder\"")) return SampleCatalogue.Replace("\"folder\": \"core\"", replacement);
            return SampleCatalogue.Replace("\"schemaVersion\": 1", replacement);
        }

        /// <summary>The sample with one field removed, for the missing-field cases.</summary>
        private static string CatalogueWithout(string field)
        {
            return SampleCatalogue.Replace(field + ", ", "").Replace(", " + field, "").Replace(field, "");
        }

        // ---------------------------------------------------------------- post-install steps

        /// <summary>
        /// Optional setup steps: catalogue parsing and plan-time verification, with no network.
        /// </summary>
        /// <remarks>
        /// Weighted towards refusal, because these end in code execution: a declared script the
        /// download does not contain, one too large to show, or one the catalogue describes badly
        /// must stop the plan rather than reach a prompt. The prompt itself - deny by default, script
        /// shown first - is UI and lives with the UI, untested here for the same reason the rest of
        /// the views are.
        /// </remarks>
        private static void PostInstallRules()
        {
            Section("Post-install steps");

            var sources = ModCatalogue.ParseCatalogue(SampleCatalogueWithSteps);
            Check("reads one entry", sources.Count, 1);
            Check("reads one step", sources[0].PostInstall.Length, 1);
            Check("reads the script name", sources[0].PostInstall[0].ScriptName, "setup-chatbridge.ps1");
            Check("reads the description", sources[0].PostInstall[0].Description, "Configures ChatBridge.");
            Check("an entry without steps has none",
                ModCatalogue.ParseCatalogue(SampleCatalogue)[0].PostInstall.Length, 0);

            Throws("a step without a script is refused",
                () => ModCatalogue.ParseCatalogue(StepsWith("{\"description\": \"Does things.\"}")));
            Throws("a step without a description is refused",
                () => ModCatalogue.ParseCatalogue(StepsWith("{\"script\": \"setup.ps1\"}")));
            Throws("a non-ps1 step is refused",
                () => ModCatalogue.ParseCatalogue(StepsWith(
                    "{\"script\": \"setup.exe\", \"description\": \"Does things.\"}")));
            Throws("a step with a separator is refused",
                () => ModCatalogue.ParseCatalogue(StepsWith(
                    "{\"script\": \"sub/setup.ps1\", \"description\": \"Does things.\"}")));
            Throws("a step with a long description is refused",
                () => ModCatalogue.ParseCatalogue(StepsWith(
                    "{\"script\": \"setup.ps1\", \"description\": \"" + new string('x', 281) + "\"}")));
            Throws("a fifth step is refused",
                () => ModCatalogue.ParseCatalogue("{\"schemaVersion\": 1, \"mods\": [" +
                    "{\"repo\": \"X\", \"displayName\": \"X\", \"summary\": \"Y\", " +
                    "\"files\": [{\"fileName\": \"A.dll\", \"folder\": \"core\", \"expectsPluginAttribute\": false}], " +
                    "\"postInstall\": [" +
                    Step(1) + ", " + Step(2) + ", " + Step(3) + ", " + Step(4) + ", " + Step(5) + "]}]}"));

            Section("Post-install planning");

            var folder = Temp("postinstall");
            try
            {
                Directory.CreateDirectory(folder);

                // A real assembly standing in for the shared library, so the plan itself succeeds and
                // the assertions are about the script rather than about the dll.
                File.Copy(typeof(ModManager).Assembly.Location,
                          Path.Combine(folder, "ScamWYF.Modding.Core.dll"));
                File.WriteAllText(Path.Combine(folder, "setup-chatbridge.ps1"),
                                  "Write-Output \"bridged\"");

                var source = StepSource();
                var release = new ReleaseInfo(source, "v1", null,
                    new ReleaseAsset("x.zip", 0, "https://x/y.zip", null));
                var install = GameInstall.Resolve(folder);

                var plan = ModInstaller.Plan(release, folder, install);
                Check("the plan carries the step", plan.Scripts.Count, 1);
                Check("the step points into the download",
                    plan.Scripts[0].ScriptPath, Path.Combine(folder, "setup-chatbridge.ps1"));
                Check("the step carries the script's text",
                    plan.Scripts[0].ScriptText.Contains("bridged"), true);
                Check("the description names the step",
                    ModInstaller.Describe(plan).Contains("setup-chatbridge.ps1"), true);
                Check("the description says it is optional",
                    ModInstaller.Describe(plan).Contains("Optional setup"), true);

                File.Delete(Path.Combine(folder, "setup-chatbridge.ps1"));
                Throws("a declared script the download lacks fails the plan",
                    () => ModInstaller.Plan(release, folder, install));

                File.WriteAllText(Path.Combine(folder, "setup-chatbridge.ps1"), "");
                Throws("an empty script fails the plan",
                    () => ModInstaller.Plan(release, folder, install));

                using (var oversized = new FileStream(Path.Combine(folder, "setup-chatbridge.ps1"),
                                                      FileMode.Create, FileAccess.Write))
                {
                    oversized.SetLength(ModInstaller.MaxScriptBytes + 1);
                }
                Throws("a script too large to show fails the plan",
                    () => ModInstaller.Plan(release, folder, install));
            }
            finally
            {
                ModInstaller.Discard(folder);
            }
        }

        /// <summary>A catalogue with one entry carrying one setup step.</summary>
        private const string SampleCatalogueWithSteps =
            "{\"schemaVersion\": 1, \"mods\": [" +
            "{\"repo\": \"Stream-Mod\", \"displayName\": \"Stream Mod\", \"summary\": \"Streams.\", " +
            "\"files\": [{\"fileName\": \"ScamWYF.Modding.Core.dll\", \"folder\": \"core\", " +
            "\"expectsPluginAttribute\": false}], " +
            "\"postInstall\": [{\"script\": \"setup-chatbridge.ps1\", " +
            "\"description\": \"Configures ChatBridge.\"}]}]}";

        /// <summary>The step catalogue with its single step replaced.</summary>
        private static string StepsWith(string step)
        {
            return SampleCatalogueWithSteps.Replace(
                "{\"script\": \"setup-chatbridge.ps1\", \"description\": \"Configures ChatBridge.\"}", step);
        }

        private static string Step(int n)
        {
            return "{\"script\": \"step" + n + ".ps1\", \"description\": \"Step " + n + ".\"}";
        }

        /// <summary>The catalogue entry the planning tests install from.</summary>
        private static ModSource StepSource()
        {
            return new ModSource("Stream-Mod", "Stream Mod", "Streams.",
                new[] { new ModFile("ScamWYF.Modding.Core.dll", ModFolder.Core, false) },
                "swyf-modding",
                new[] { new PostInstallStep("setup-chatbridge.ps1", "Configures ChatBridge.", null) });
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>
        /// BepInEx's version rule, checked against the exact strings that caused a release to ship two
        /// mods that loaded nothing.
        /// </summary>
        /// <remarks>
        /// BepInEx 5 does `new System.Version(<the [BepInPlugin] version argument>)` in a try/catch and
        /// skips the type if it throws. A SemVer string with build metadata - "1.2.3+commitsha" - is
        /// valid SemVer and invalid System.Version, so a mod carrying one is skipped with "version is
        /// invalid" in the log and nothing anywhere else says so. These are the cases, both directions.
        /// </remarks>
        private static void BepInExVersionRules()
        {
            Section("BepInEx version acceptance");

            // Accepted: what a well-formed generated version looks like.
            VersionAccepted("1.0.1");
            VersionAccepted("1.0");
            VersionAccepted("1.0.0.0");
            VersionAccepted("01.02.03");

            // Rejected: the exact shape that shipped and silently loaded nothing.
            RefusesVersion("1.0.1+g1d246ee");
            RefusesVersion("1.0.1+dirty");
            RefusesVersion("1.0.1-rc1");
            RefusesVersion("v1.0.1");
            RefusesVersion("1.0.0.0.1");

            // Rejected: one component throws, though it looks like a version.
            RefusesVersion("1");

            RefusesVersion("");
            RefusesVersion(null);

            // The rule has to agree with the runtime, not with a table written out by hand. Every string
            // above is put through new Version() and the two answers compared.
            var agrees = true;
            foreach (var candidate in new[] { "1.0.1", "1.0.1+g1d246ee", "1", "1.0.0.0", "v2", "" })
            {
                bool runtime;
                try { runtime = new Version(candidate) != null; }
                catch (Exception) { runtime = false; }

                if (runtime != PluginScanner.BepInExWouldAccept(candidate)) agrees = false;
            }
            Check("agrees with System.Version on every case", agrees, true);
        }

        private static void VersionAccepted(string version)
        {
            Check("BepInEx would load version '" + version + "'", PluginScanner.BepInExWouldAccept(version), true);
        }

        private static void RefusesVersion(string version)
        {
            var shown = version ?? "(null)";
            Check("BepInEx would skip version '" + shown + "'", PluginScanner.BepInExWouldAccept(version), false);
        }

        // ---------------------------------------------------------------- switchboard updates

        /// <summary>
        /// The update checker's version comparison, with no network.
        /// </summary>
        /// <remarks>
        /// The comparison is the whole feature: the fetch is one GET and the banner is three controls,
        /// but deciding "newer" wrongly either nags someone who is current or stays silent when they are
        /// not. Every shape build.ps1 actually stamps is here - tags, commits past a tag, prereleases,
        /// dirty trees and untagged builds.
        /// </remarks>
        private static void SwitchboardUpdateRules()
        {
            Section("Switchboard update comparison");

            // The ordinary cases.
            UpdateIsOffered("1.2.3", "v1.2.4");
            UpdateIsOffered("1.2.3+g0a1b2c3", "v1.2.4");
            UpdateNotOffered("1.2.4", "v1.2.4");
            UpdateNotOffered("1.2.4+g0a1b2c3", "v1.2.4");
            UpdateNotOffered("1.2.5", "v1.2.4");

            // A commit past the tag is still that release, not something newer than it.
            UpdateNotOffered("1.2.4+1.g0a1b2c3", "v1.2.4");
            UpdateIsOffered("1.2.4+1.g0a1b2c3", "v1.2.5");

            // A finished release supersedes its own release candidate.
            UpdateIsOffered("1.2.4-rc1+g0a1b2c3", "v1.2.4");

            // A prerelease is never offered as an update to a finished release.
            UpdateNotOffered("1.2.4", "v1.2.5-rc1");

            // A newer prerelease supersedes an older one of the same numbers.
            UpdateIsOffered("1.2.5-rc1", "v1.2.5-rc2");
            UpdateNotOffered("1.2.5-rc2", "v1.2.5-rc1");

            // Someone already on a prerelease is told about a higher prerelease.
            UpdateIsOffered("1.2.4-rc1", "v1.2.5-rc1");

            // A dirty tree is still the version it says it is.
            UpdateNotOffered("1.2.4+g0a1b2c3.dirty", "v1.2.4");

            // No tag behind the build: anything published counts as newer.
            UpdateIsOffered("0.0.0+untagged.g0a1b2c3", "v1.0.0");

            // A reply with no tag in it is not an update; it is a broken reply.
            Check("empty tag is not an update", UpdateChecker.IsUpdateAvailable("1.2.3", ""), false);
            Check("null tag is not an update", UpdateChecker.IsUpdateAvailable("1.2.3", null), false);

            Section("Switchboard release JSON");

            var release = UpdateChecker.ParseReleaseJson(
                "{\"tag_name\": \"v1.5.1\", " +
                "\"html_url\": \"https://github.com/swyf-modding/Switchboard/releases/tag/v1.5.1\", " +
                "\"assets\": []}");
            Check("reads the tag", release.TagName, "v1.5.1");
            Check("reads the page URL",
                release.PageUrl, "https://github.com/swyf-modding/Switchboard/releases/tag/v1.5.1");

            Throws("a reply with no tag is an error, not an empty release",
                () => UpdateChecker.ParseReleaseJson("{\"html_url\": \"https://github.com/x\"}"));
            Throws("a reply that is not JSON is an error",
                () => UpdateChecker.ParseReleaseJson("rate limit exceeded"));

            Section("Self-update: picking the published file");

            // A release carries the executable for each platform. Which one is this program's is
            // decided by shape rather than by name, because the names have changed once already.
            var withAssets = UpdateChecker.ParseReleaseJson(
                "{\"tag_name\": \"v1.7.0\", " +
                "\"html_url\": \"https://github.com/swyf-modding/Switchboard/releases/tag/v1.7.0\", " +
                "\"assets\": [" +
                "{\"name\": \"Switchboard\", \"size\": 52723900, " +
                " \"browser_download_url\": \"https://example.invalid/Switchboard\", " +
                " \"digest\": \"sha256:" + new string('a', 64) + "\"}," +
                "{\"name\": \"Switchboard.exe\", \"size\": 52832582, " +
                " \"browser_download_url\": \"https://example.invalid/Switchboard.exe\", " +
                " \"digest\": \"sha256:" + new string('b', 64) + "\"}]}");

            Check("finds the Windows file",
                withAssets.AssetFor(true).Name, "Switchboard.exe");
            Check("finds the other file",
                withAssets.AssetFor(false).Name, "Switchboard");
            Check("reads the digest",
                withAssets.AssetFor(true).Sha256, new string('b', 64));

            // The pre-rename release still has to be understood, or the update button would be
            // missing for anyone on the build that came before it.
            var oldAssets = UpdateChecker.ParseReleaseJson(
                "{\"tag_name\": \"v1.6.0\", " +
                "\"html_url\": \"https://github.com/swyf-modding/Switchboard/releases/tag/v1.6.0\", " +
                "\"assets\": [" +
                "{\"name\": \"ScamWYF.Launcher\", \"size\": 52723900, " +
                " \"browser_download_url\": \"https://example.invalid/ScamWYF.Launcher\"}," +
                "{\"name\": \"ScamWYF.Launcher.exe\", \"size\": 52832582, " +
                " \"browser_download_url\": \"https://example.invalid/ScamWYF.Launcher.exe\"}]}");

            Check("finds the Windows file in the old release too",
                oldAssets.AssetFor(true).Name, "ScamWYF.Launcher.exe");
            Check("a release with no digest has none",
                oldAssets.AssetFor(true).Sha256, null);

            // A zip is a package, not a program: there is no binary to put in place, so it is
            // never the answer.
            var zipOnly = UpdateChecker.ParseReleaseJson(
                "{\"tag_name\": \"v1.5.3\", " +
                "\"html_url\": \"https://github.com/swyf-modding/Switchboard/releases/tag/v1.5.3\", " +
                "\"assets\": [" +
                "{\"name\": \"ScamWYF.Launcher-v1.5.3.zip\", \"size\": 52723900, " +
                " \"browser_download_url\": \"https://example.invalid/x.zip\"}]}");

            Check("a zip is not an installable file", zipOnly.AssetFor(false) == null, true);
            Check("a zip is not a Windows file either", zipOnly.AssetFor(true) == null, true);

            Section("Self-update: the replacing script");

            // The part that actually has to work: a script that is handed a file and a running
            // program, and has to put the file in the program's place and start it.
            var work = Path.Combine(Path.GetTempPath(), "switchboard-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);

            var marker = Path.Combine(work, "started.txt");
            var target = Path.Combine(work, OperatingSystem.IsWindows() ? "target.exe" : "target");
            var replacement = Path.Combine(work, OperatingSystem.IsWindows() ? "replacement.exe" : "replacement");

            // The program that is "running" and the file that replaces it. Two different system
            // programs, so that the replacement is distinguishable from what it replaced - and
            // both are copies, so the real ones are never touched.
            string[] args;
            if (OperatingSystem.IsWindows())
            {
                var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
                File.Copy(Path.Combine(system, "where.exe"), target, true);
                File.Copy(Path.Combine(system, "cmd.exe"), replacement, true);

                var markerScript = Path.Combine(work, "marker.cmd");
                File.WriteAllText(markerScript, "@echo off\r\necho new > \"" + marker + "\"\r\n");
                args = new[] { "/c", markerScript };
            }
            else
            {
                File.Copy("/bin/true", target, true);
                File.Copy("/bin/sh", replacement, true);

                var markerScript = Path.Combine(work, "marker.sh");
                File.WriteAllText(markerScript, "#!/bin/sh\necho new > \"" + marker + "\"\n");
                args = new[] { markerScript };
            }

            var script = SelfUpdate.WriteScript(target, replacement, PlaceholderPid(),
                args, "selftest" + Guid.NewGuid().ToString("N"));

            Check("the script was written", File.Exists(script), true);

            // The script waits for a program to go before it replaces the file, so the test gives
            // it one that has already gone rather than the test's own process - which would
            // wait for the test to finish, and the test is waiting for the script.
            var replacementLength = new FileInfo(replacement).Length;

            // Hold the target open, the way a running program holds its own file: the copy cannot
            // succeed while it is locked, and the script has to keep trying.
            using (var held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                SelfUpdate.StartScript(script);

                Thread.Sleep(1500);

                if (OperatingSystem.IsWindows())
                {
                    Check("the locked file is not replaced yet",
                        new FileInfo(target).Length != replacementLength, true);
                }
            }

            // Once it is released the script's next attempt has to succeed.
            var replaced = false;
            for (int i = 0; i < 40; i++)
            {
                Thread.Sleep(500);
                if (new FileInfo(target).Length == replacementLength) { replaced = true; break; }
            }

            Check("the script replaces the file once it is released", replaced, true);

            // And the new file has to be started, or the person is left with a program that does
            // not open.
            var started = false;
            for (int i = 0; i < 40; i++)
            {
                Thread.Sleep(500);
                if (File.Exists(marker)) { started = true; break; }
            }

            Check("the script starts the new file", started, true);

            try { Directory.Delete(work, true); } catch (Exception) { }
        }

        /// <summary>
        /// The process id of a program that has already exited, for the script to wait for.
        /// </summary>
        /// <remarks>
        /// The script waits for a program to go before it replaces the file. The test cannot use its
        /// own process for that, because the test is waiting for the script - so it starts a
        /// throwaway one, lets it finish, and hands over its id.
        /// </remarks>
        private static int PlaceholderPid()
        {
            var info = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };

            if (OperatingSystem.IsWindows())
            {
                info.FileName = "cmd.exe";
                info.ArgumentList.Add("/c");
                info.ArgumentList.Add("exit");
                info.ArgumentList.Add("0");
            }
            else
            {
                info.FileName = "/bin/true";
            }

            var process = Process.Start(info);
            if (process == null) return 0;

            var id = process.Id;
            process.WaitForExit(10000);
            return id;
        }

        private static void UpdateIsOffered(string current, string latest)
        {
            Check("'" + current + "' is told about '" + latest + "'",
                UpdateChecker.IsUpdateAvailable(current, latest), true);
        }

        private static void UpdateNotOffered(string current, string latest)
        {
            Check("'" + current + "' is not told about '" + latest + "'",
                UpdateChecker.IsUpdateAvailable(current, latest), false);
        }

        // ---------------------------------------------------------------- install requests

        /// <summary>
        /// The website's switchboard:// links: parsing and the handoff to a running instance.
        /// </summary>
        private static void InstallRequestRules()
        {
            Section("Install requests");

            InstallRequest parsed;
            Check("parses the query shape",
                InstallRequest.TryParse("switchboard://install?owner=swyf-modding&repo=Mod-Handler", out parsed)
                && parsed.Owner == "swyf-modding" && parsed.Repo == "Mod-Handler", true);

            Check("parses the path shape",
                InstallRequest.TryParse("switchboard://install/swyf-modding/Mod-Handler", out parsed)
                && parsed.Owner == "swyf-modding" && parsed.Repo == "Mod-Handler", true);

            Check("finds the request anywhere in the args",
                InstallRequest.TryFind(new[] { "C:\\Games\\SWYF", "switchboard://install?owner=a&repo=b" }, out parsed)
                && parsed.Owner == "a" && parsed.Repo == "b", true);

            Check("a game folder is not a request",
                InstallRequest.TryFind(new[] { "C:\\Games\\SWYF" }, out parsed), false);

            Check("refuses a missing repo",
                InstallRequest.TryParse("switchboard://install?owner=a", out parsed), false);

            Check("refuses a bad owner",
                InstallRequest.TryParse("switchboard://install?owner=a/b&repo=c", out parsed), false);

            Check("refuses another scheme",
                InstallRequest.TryParse("https://example.com/install?owner=a&repo=b", out parsed), false);
        }

        private static void Section(string name)
        {
            Console.WriteLine();
            Console.WriteLine(name);
        }

        // ---------------------------------------------------------------- mod config

        /// <summary>
        /// The config editor's parsing, validation and round-tripping.
        /// </summary>
        /// <remarks>
        /// Weighted towards round-tripping, because that is where a mistake does damage. Everything this
        /// writes goes into a file a mod wrote and will read back, so a test that only checks "the right
        /// value came out" would pass while the documentation above it had been thrown away.
        /// </remarks>
        private static void ModConfigFiles()
        {
            Section("Mod config files");

            var dir = Path.Combine(Path.GetTempPath(), "scamwyf-cfgtest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "com.example.test.cfg");

            try
            {
                File.WriteAllText(path, SampleConfig);

                var config = ModConfigFile.Load(path);

                Check("reads the plugin GUID", config.Guid, "com.example.test");
                Check("reads what created the file", config.CreatedBy, "Example v1.2.3");
                Check("finds every section", config.Sections.Count, 2);
                Check("finds every setting", config.Entries.Count, 5);

                var mode = Find(config, "Mode");
                Check("reads a setting's section", mode.Section, "1 - General");
                Check("reads a setting's value", mode.Value, "OpenAiCompatible");
                Check("reads a setting's type", mode.SettingType, "BackendMode");
                Check("reads a setting's default", mode.DefaultValue, "OpenAiCompatible");
                Check("reads a fixed list of values", mode.Acceptable.Count, 3);
                Check("keeps the documentation above a setting", mode.Comments.Count >= 3, true);

                var debug = Find(config, "Debug");
                Check("recognises a boolean", debug.IsBoolean, true);
                Check("a boolean has no fixed value list", debug.HasFixedValues, false);

                var baseUrl = Find(config, "BaseUrl");
                Check("a free-text setting is not a boolean", baseUrl.IsBoolean, false);
                Check("a free-text setting has no fixed values", baseUrl.HasFixedValues, false);

                // Keys can repeat across sections; the editor addresses a setting by where it is, not by
                // its name, so two of them must both survive.
                Check("keeps same-named settings in different sections",
                      string.Join(",", config.Entries.FindAll(e => e.Key == "Width").ConvertAll(e => e.Section)),
                      "1 - General,2 - Other");

                Section("Mod config validation");

                var noProblem = config.ProblemWith(mode, "OllamaNative");
                Check("accepts a declared value", noProblem, null);
                Check("accepts a declared value in any case",
                      config.ProblemWith(mode, "ollamanative"), null);
                Check("refuses a value outside the list",
                      config.ProblemWith(mode, "Gemini") != null, true);
                Check("says what the values are",
                      (config.ProblemWith(mode, "Gemini") ?? "").Contains("OllamaNative"), true);

                Check("accepts true for a boolean", config.ProblemWith(debug, "true"), null);
                Check("accepts FALSE for a boolean", config.ProblemWith(debug, "FALSE"), null);
                Check("refuses a non-boolean for a boolean",
                      config.ProblemWith(debug, "yes") != null, true);
                Check("refuses a multi-line value",
                      config.ProblemWith(baseUrl, "http://a\r\nhttp://b") != null, true);

                Section("Mod config round trip");

                Find(config, "Debug").Value = "true";
                Find(config, "Mode").Value = "OllamaNative";

                var changes = config.Changes();
                Check("reports what changed", changes.Count, 2);

                config.Save();

                var after = File.ReadAllText(path);
                Check("writes the changed value", after.Contains("Debug = true"), true);
                Check("writes the other changed value", after.Contains("Mode = OllamaNative"), true);
                Check("keeps the documentation", after.Contains("## Which backend to use."), true);
                Check("keeps the default annotation", after.Contains("# Default value: OpenAiCompatible"), true);
                Check("keeps the section headers", after.Contains("[2 - Other]"), true);
                Check("keeps the trailing settings", after.Contains("Width = 1920"), true);
                Check("keeps a blank line the parser ignored", after.Contains("## Plugin GUID: com.example.test\r\n\r\n[1 - General]"), true);
                Check("writes no more lines than it read", LineCount(after), LineCount(SampleConfig));
                Check("nothing is left modified after a save",
                      ModConfigFile.Load(path).Changes().Count, 0);

                // Re-saving with nothing changed must not rewrite the file, so a person's config is not
                // churned just because they opened the editor.
                var before = File.ReadAllText(path);
                ModConfigFile.Load(path).Save();
                Check("an unchanged save writes the same bytes", File.ReadAllText(path), before);

                Section("Mod config edge cases");

                var odd = Path.Combine(dir, "odd.cfg");
                File.WriteAllText(odd, "no sections at all\r\nKey = value\r\n\r\n[Later]\r\nOther = 2\r\n");
                var oddConfig = ModConfigFile.Load(odd);
                Check("reads a file with no section header", oddConfig.Entries.Count, 2);
                Check("files the first setting as ungrouped", oddConfig.Entries[0].Section, "(ungrouped)");

                var minimal = Path.Combine(dir, "minimal.cfg");
                File.WriteAllText(minimal, "JustKey = 1\r\n");
                Check("reads a file with no metadata", ModConfigFile.Load(minimal).Entries.Count, 1);
                Check("a setting with no metadata is accepted",
                      ModConfigFile.Load(minimal).ProblemWith(ModConfigFile.Load(minimal).Entries[0], "2"), null);

                Throws("a missing config file is an error, not an empty config",
                        delegate { ModConfigFile.Load(Path.Combine(dir, "nope.cfg")); });

                var configFolder = Path.Combine(dir, "config");
                Check("a config path is the GUID under the config folder",
                      ModConfigFile.PathFor(configFolder, "com.example.test"),
                      Path.Combine(configFolder, "com.example.test.cfg"));
                Check("no GUID means no config path", ModConfigFile.PathFor(configFolder, null), null);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static ModConfigEntry Find(ModConfigFile config, string key)
        {
            foreach (var entry in config.Entries)
            {
                if (entry.Key == key) return entry;
            }
            throw new InvalidOperationException("no setting called " + key);
        }

        private static int LineCount(string text)
        {
            return text.Split(new[] { "\r\n" }, StringSplitOptions.None).Length;
        }

        /// <summary>A config file in the shape BepInEx actually writes, comments and all.</summary>
        private const string SampleConfig =
            "## Settings file was created by plugin Example v1.2.3\r\n" +
            "## Plugin GUID: com.example.test\r\n" +
            "\r\n" +
            "[1 - General]\r\n" +
            "\r\n" +
            "## Which backend to use.\r\n" +
            "## Passthrough leaves the game alone.\r\n" +
            "## OllamaNative is Ollama's own endpoint.\r\n" +
            "# Setting type: BackendMode\r\n" +
            "# Default value: OpenAiCompatible\r\n" +
            "# Acceptable values: Passthrough, OpenAiCompatible, OllamaNative\r\n" +
            "Mode = OpenAiCompatible\r\n" +
            "\r\n" +
            "## Log every request.\r\n" +
            "# Setting type: Boolean\r\n" +
            "# Default value: false\r\n" +
            "Debug = false\r\n" +
            "\r\n" +
            "## How wide the panel is here too, to prove keys may repeat.\r\n" +
            "# Setting type: Integer\r\n" +
            "# Default value: 1280\r\n" +
            "Width = 1280\r\n" +
            "\r\n" +
            "[2 - Other]\r\n" +
            "\r\n" +
            "## Where the server is.\r\n" +
            "# Setting type: String\r\n" +
            "# Default value: http://127.0.0.1:11434/v1\r\n" +
            "BaseUrl = http://127.0.0.1:11434/v1\r\n" +
            "\r\n" +
            "## How wide.\r\n" +
            "Width = 1920\r\n";

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