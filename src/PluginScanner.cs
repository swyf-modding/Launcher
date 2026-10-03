using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace ScamWYF.Launcher
{
    /// <summary>
    /// Reads what each plugin file in BepInEx\plugins claims to be, without running any of it.
    /// </summary>
    /// <remarks>
    /// Cecil rather than Assembly.Load for three reasons that all matter here:
    ///
    ///  - loading a mod runs its static constructors, and this tool exists partly to delete mods;
    ///  - a loaded assembly is locked, so the file could not then be moved or deleted;
    ///  - a mod built against a different BepInEx would throw on load rather than simply telling us
    ///    its name, which is the question being asked.
    ///
    /// Metadata reading cannot throw because of what a mod does, only because a file is not an
    /// assembly at all, which is handled per file rather than failing the whole scan.
    /// </remarks>
    internal static class PluginScanner
    {
        private const string AttributeName = "BepInEx.BepInPlugin";

        /// <summary>
        /// Every plugin file in the two folders, enabled ones first and then alphabetical.
        /// </summary>
        /// <param name="plugins">BepInEx\plugins.</param>
        /// <param name="disabled">BepInEx\plugins_disabled. May not exist; that is fine.</param>
        public static List<PluginEntry> Scan(string plugins, string disabled)
        {
            var found = new List<PluginEntry>();

            Add(found, plugins, true);
            Add(found, disabled, false);

            // Enabled first, then by name: the list is read while deciding what to turn off, and
            // alphabetical within a state is what makes two runs look the same.
            return found
                .OrderByDescending(entry => entry.Enabled)
                .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void Add(List<PluginEntry> into, string folder, bool enabled)
        {
            if (!Directory.Exists(folder)) return;

            foreach (var file in Directory.GetFiles(folder, "*.dll"))
            {
                into.Add(Read(file, enabled));
            }
        }

        /// <summary>One file, described. Never throws: a bad file is reported, not raised.</summary>
        private static PluginEntry Read(string path, bool enabled)
        {
            string guid = null, name = null, version = null, problem = null;
            var isAssembly = false;

            try
            {
                using (var assembly = AssemblyDefinition.ReadAssembly(path))
                {
                    isAssembly = true;

                    // A file with no [BepInPlugin] - the shared library is the one that ships - still has a
                    // version, in the assembly's own attributes. Reading it means the Mods tab and the
                    // install confirmation can show a version for it rather than a blank, which otherwise
                    // reads as though something were missing.
                    version = InformationalVersion(assembly) ?? assembly.Name.Version.ToString();

                    foreach (var type in AllTypes(assembly.MainModule.Types))
                    {
                        foreach (var attribute in type.CustomAttributes)
                        {
                            if (attribute.AttributeType.FullName != AttributeName) continue;

                            var args = attribute.ConstructorArguments;
                            if (args.Count >= 1 && args[0].Value != null) guid = args[0].Value.ToString();
                            if (args.Count >= 2 && args[1].Value != null) name = args[1].Value.ToString();
                            if (args.Count >= 3 && args[2].Value != null) version = args[2].Value.ToString();
                            break;
                        }

                        // Only the first one: two [BepInPlugin] types in one dll means the second one
                        // never runs, which is worth knowing but not worth reporting twice over.
                        if (guid != null) break;
                    }
                }
            }
            catch (Exception ex)
            {
                problem = Describe(ex);
            }

            if (problem == null && guid == null)
            {
                // Accurate rather than reassuring: BepInEx genuinely will not load this, and for the shared
                // library that is the intent - it must not be loaded as a plugin. Saying so is more useful
                // than "unrecognised", and naming the case stops it reading as a fault.
                problem = "no [BepInPlugin] attribute, so BepInEx will not load it as a mod " +
                          "(the shared library is deliberately like this)";
            }

            return new PluginEntry(path, enabled, guid, name, version, problem, isAssembly);
        }

        /// <summary>
        /// AssemblyInformationalVersion, which is the field the build stamps the git tag into.
        /// </summary>
        /// <remarks>
        /// Preferred over AssemblyVersion because that one is numeric only: a prerelease tag loses its
        /// name there, so "1.2.0" and "1.2.0-rc1" would be indistinguishable.
        /// </remarks>
        private static string InformationalVersion(AssemblyDefinition assembly)
        {
            try
            {
                foreach (var attribute in assembly.CustomAttributes)
                {
                    if (attribute.AttributeType.FullName != "System.Reflection.AssemblyInformationalVersionAttribute")
                    {
                        continue;
                    }

                    if (attribute.ConstructorArguments.Count < 1) return null;

                    var value = attribute.ConstructorArguments[0].Value as string;
                    if (!string.IsNullOrEmpty(value)) return value;
                }
            }
            catch (Exception)
            {
                // A version is a nicety. Never let reading one turn a listed file into a failed scan.
            }

            return null;
        }

        /// <summary>Types in the module and in every namespace, since BepInEx looks in all of them.</summary>
        private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> roots)
        {
            foreach (var type in roots)
            {
                foreach (var nested in AllTypes(type.NestedTypes)) yield return nested;
                yield return type;
            }
        }

        /// <summary>
        /// A short reason. Cecil's exceptions name an offset, which tells a person nothing useful;
        /// the common cause is a file that is not a .NET assembly at all.
        /// </summary>
        private static string Describe(Exception ex)
        {
            var message = ex.Message ?? "";
            if (message.IndexOf("BadImageFormat", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("not a valid Win32", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "not a .NET assembly, so it cannot be a plugin";
            }

            return message.Length > 0 ? message : ex.GetType().Name;
        }
    }
}
