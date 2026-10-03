using System;
using System.Collections.Generic;
using System.IO;

namespace ScamWYF.Launcher
{
    /// <summary>
    /// One mod BepInEx found on disk: where it came from, and what it says it is.
    /// </summary>
    /// <remarks>
    /// The file on disk is the truth about whether a mod is active, not anything the loader thinks.
    /// BepInEx loads whatever is in plugins\ and ignores plugins_disabled\, so "enabled" here means
    /// exactly that: which of the two folders the file is sitting in.
    ///
    /// The identity fields come from the [BepInPlugin] attribute, read as metadata rather than by
    /// loading the assembly. Loading would run the mod's static constructors, and a mod about to be
    /// deleted should not get to run code first. It would also lock the file, so it could not be
    /// deleted afterwards.
    /// </remarks>
    public sealed class PluginEntry
    {
        public PluginEntry(string path, bool enabled, string guid, string name, string version,
            string loadError, bool isAssembly)
        {
            Path = path;
            Enabled = enabled;
            Guid = guid;
            Name = name;
            Version = version;
            LoadError = loadError;

            // Whether Cecil could read the file at all. Kept apart from LoadError because "read it, and it
            // has no [BepInPlugin]" is not the same kind of problem as "this is not a .NET assembly", and
            // conflating them means the shared library - which deliberately has no plugin attribute, so
            // that the chainloader does not try to load it as one - is reported as broken.
            IsAssembly = isAssembly;
        }

        /// <summary>True when the file is a .NET assembly Cecil could read.</summary>
        public bool IsAssembly { get; private set; }

        /// <summary>Where the file is now. Moving it is how a mod is enabled or disabled.</summary>
        public string Path { get; private set; }

        public bool Enabled { get; private set; }

        /// <summary>The [BepInPlugin] guid, or null when the file carries no such attribute.</summary>
        public string Guid { get; private set; }

        /// <summary>The display name, or null.</summary>
        public string Name { get; private set; }

        /// <summary>The declared version, or null.</summary>
        public string Version { get; private set; }

        /// <summary>
        /// Why this file was not understood, or null. A dll with no [BepInPlugin] is not an error -
        /// the shared library is one, for instance - but it is worth saying so, because a plugin
        /// someone just copied in that quietly never loads is exactly the sort of thing that wastes
        /// an evening.
        /// </summary>
        public string LoadError { get; private set; }

        /// <summary>Just the file name, which is what a person recognises.</summary>
        public string FileName
        {
            get { return System.IO.Path.GetFileName(Path); }
        }

        /// <summary>What to show in the list. Falls back to the file name when nothing claimed it.</summary>
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(Name)) return Name;
                return FileName;
            }
        }

        /// <summary>The version with something to show, or an empty string.</summary>
        public string DisplayVersion
        {
            get { return Version ?? ""; }
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }
}
