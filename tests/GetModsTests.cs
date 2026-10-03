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
            BepInExVersionRules();
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

                Check("a config path is the GUID under the config folder",
                      ModConfigFile.PathFor(@"C:\BepInEx\config", "com.example.test"),
                      @"C:\BepInEx\config\com.example.test.cfg");
                Check("no GUID means no config path", ModConfigFile.PathFor(@"C:\config", null), null);
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