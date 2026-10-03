using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ScamWYF.Launcher
{
    /// <summary>
    /// One editable setting in a mod's config file.
    /// </summary>
    /// <remarks>
    /// A setting is identified by where it came from rather than by its key alone, because a BepInEx
    /// config file can legitimately repeat a key across sections - <c>window.Mods.X</c> under [Menu] and
    /// something else entirely under [Windows] - and an editor that silently changed the wrong one would
    /// be worse than no editor.
    /// </remarks>
    public sealed class ModConfigEntry
    {
        public ModConfigEntry(string section, string key, string value, int line)
        {
            Section = section;
            Key = key;
            Value = value;
            Line = line;
        }

        public string Section { get; private set; }
        public string Key { get; private set; }

        /// <summary>The value as it stands, which the editor changes and <see cref="ModConfigFile"/> writes back.</summary>
        public string Value { get; set; }

        /// <summary>Zero-based index of the line this came from, so saving rewrites that line and nothing else.</summary>
        public int Line { get; private set; }

        /// <summary>What the mod says this setting is: Boolean, String, Key, or one of its own enums.</summary>
        public string SettingType { get; set; }

        public string DefaultValue { get; set; }

        /// <summary>The values the mod will accept, when it declares them. Empty when it does not.</summary>
        public List<string> Acceptable { get; private set; }

        /// <summary>The '##' block the mod wrote above this setting, which is the only documentation it has.</summary>
        public List<string> Comments { get; private set; }

        /// <summary>Set while loading, so the editor can tell an untouched setting from a changed one.</summary>
        public string OriginalValue { get; internal set; }

        public bool IsBoolean
        {
            get { return string.Equals(SettingType, "Boolean", StringComparison.OrdinalIgnoreCase); }
        }

        public bool Modified
        {
            get { return !string.Equals(Value, OriginalValue, StringComparison.Ordinal); }
        }

        /// <summary>True when the mod restricts this setting to a fixed set of values.</summary>
        public bool HasFixedValues
        {
            get { return Acceptable != null && Acceptable.Count > 0; }
        }

        internal void SetAcceptable(List<string> values) { Acceptable = values; }
        internal void SetComments(List<string> comments) { Comments = comments; }
    }

    /// <summary>
    /// A mod's BepInEx config file: read, checked, edited, written back.
    /// </summary>
    /// <remarks>
    /// Round-tripping is the whole design. BepInEx writes a file that is mostly comment - the type, the
    /// default and often a long explanation sit above every value - and that comment is the only
    /// documentation the setting has. So this keeps the original lines and remembers which line each
    /// setting came from; saving rewrites those lines and leaves every other byte alone. Regenerating
    /// the file from a model would be tidier and would throw the documentation away.
    ///
    /// Only <c>Key = Value</c> lines are treated as settings. Anything it does not recognise is carried
    /// through untouched, which means an unrecognised line in someone's config is preserved rather than
    /// dropped on the next save.
    /// </remarks>
    public sealed class ModConfigFile
    {
        private static readonly Regex SectionLine =
            new Regex(@"^\s*\[(?<name>.+)\]\s*$", RegexOptions.Compiled);

        private static readonly Regex SettingLine =
            new Regex(@"^\s*(?<key>[^#\[\]][^=]*?)\s*=\s*(?<value>.*?)\s*$", RegexOptions.Compiled);

        private static readonly Regex TypeLine =
            new Regex(@"^#\s*Setting type:\s*(?<v>.+?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex DefaultLine =
            new Regex(@"^#\s*Default value:\s*(?<v>.*?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex AcceptableLine =
            new Regex(@"^#\s*Acceptable values:\s*(?<v>.+?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly List<string> _lines;
        private readonly Encoding _encoding;
        private readonly bool _hasBom;

        private ModConfigFile(string path, List<string> lines, Encoding encoding, bool hasBom)
        {
            Path = path;
            _lines = lines;
            _encoding = encoding;
            _hasBom = hasBom;

            Entries = new List<ModConfigEntry>();
            Sections = new List<string>();
        }

        public string Path { get; private set; }

        /// <summary>The plugin GUID from the file header, which is also its name.</summary>
        public string Guid { get; private set; }

        /// <summary>The "created by plugin X v1.2.3" line, shown so the file can be identified at a glance.</summary>
        public string CreatedBy { get; private set; }

        /// <summary>Section names in the order they appear.</summary>
        public List<string> Sections { get; private set; }

        public List<ModConfigEntry> Entries { get; private set; }

        public bool IsEmpty { get { return Entries.Count == 0; } }

        /// <summary>
        /// Read a config file.
        /// </summary>
        /// <exception cref="IOException">The file could not be read.</exception>
        public static ModConfigFile Load(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");
            if (!File.Exists(path)) throw new FileNotFoundException("No config file at that path.", path);

            // Encoding detected rather than assumed: BepInEx writes UTF-8, but a file a person has edited
            // in Notepad may carry a byte order mark, and rewriting it without one is a small gratuitous
            // change to someone's file.
            var bytes = File.ReadAllBytes(path);
            var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var encoding = new UTF8Encoding(hasBom);
            var text = encoding.GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));

            var lines = new List<string>(text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));
            var config = new ModConfigFile(path, lines, encoding, hasBom);
            config.Parse();
            return config;
        }

        private void Parse()
        {
            var section = "(ungrouped)";
            var pendingComments = new List<string>();
            string type = null, fallback = null;
            List<string> acceptable = null;

            for (int i = 0; i < _lines.Count; i++)
            {
                var line = _lines[i];

                var sectionMatch = SectionLine.Match(line);
                if (sectionMatch.Success)
                {
                    section = sectionMatch.Groups["name"].Value.Trim();
                    if (!Sections.Contains(section)) Sections.Add(section);
                    pendingComments.Clear();
                    type = null; fallback = null; acceptable = null;
                    continue;
                }

                // The header, which names the plugin the file belongs to.
                if (Guid == null && line.StartsWith("##", StringComparison.Ordinal))
                {
                    var body = line.Substring(2).Trim();
                    if (body.StartsWith("Plugin GUID:", StringComparison.OrdinalIgnoreCase))
                    {
                        Guid = body.Substring("Plugin GUID:".Length).Trim();
                    }
                    else if (body.StartsWith("Settings file was created by", StringComparison.OrdinalIgnoreCase))
                    {
                        var rest = body.Substring("Settings file was created by".Length).Trim();

                        // "Settings file was created by plugin Example v1.2.3" describes the plugin as
                        // "Example v1.2.3". The word "plugin" is part of the sentence, not of the name, and
                        // this is shown in the editor's header as the thing that identifies the file.
                        if (rest.StartsWith("plugin ", StringComparison.OrdinalIgnoreCase))
                        {
                            rest = rest.Substring("plugin ".Length).Trim();
                        }

                        CreatedBy = rest;
                    }
                }

                if (line.StartsWith("##", StringComparison.Ordinal))
                {
                    pendingComments.Add(line.Substring(2).Trim());
                    continue;
                }

                var typeMatch = TypeLine.Match(line);
                if (typeMatch.Success) { type = typeMatch.Groups["v"].Value.Trim(); continue; }

                var defaultMatch = DefaultLine.Match(line);
                if (defaultMatch.Success) { fallback = defaultMatch.Groups["v"].Value; continue; }

                var acceptableMatch = AcceptableLine.Match(line);
                if (acceptableMatch.Success)
                {
                    acceptable = new List<string>();
                    foreach (var piece in acceptableMatch.Groups["v"].Value.Split(','))
                    {
                        var value = piece.Trim();
                        if (value.Length > 0) acceptable.Add(value);
                    }
                    continue;
                }

                var settingMatch = SettingLine.Match(line);
                if (!settingMatch.Success) continue;

                var entry = new ModConfigEntry(
                    section,
                    settingMatch.Groups["key"].Value.Trim(),
                    settingMatch.Groups["value"].Value,
                    i);

                entry.OriginalValue = entry.Value;
                entry.SettingType = type;
                entry.DefaultValue = fallback;
                entry.SetAcceptable(acceptable);
                entry.SetComments(new List<string>(pendingComments));

                Entries.Add(entry);

                pendingComments.Clear();
                type = null; fallback = null; acceptable = null;
            }
        }

        /// <summary>
        /// Whether a proposed value is one the mod will accept.
        /// </summary>
        /// <remarks>
        /// Checked here rather than left to the game, because the game's rejection is a log line nobody
        /// reads and the setting silently keeps its old value. The rules are the ones the file itself
        /// states: a fixed list when it declares one, and true/false for a Boolean.
        ///
        /// Deliberately permissive about case. BepInEx enums are case-insensitive when it parses them, so
        /// refusing "ollama" for "Ollama" would be this tool being stricter than the thing it edits.
        /// </remarks>
        public string ProblemWith(ModConfigEntry entry, string proposed)
        {
            if (entry == null) return "no setting";

            if (proposed == null) proposed = "";
            proposed = proposed.Trim();

            if (entry.HasFixedValues)
            {
                foreach (var allowed in entry.Acceptable)
                {
                    if (string.Equals(allowed, proposed, StringComparison.OrdinalIgnoreCase)) return null;
                }

                return "'" + proposed + "' is not one of: " + string.Join(", ", entry.Acceptable.ToArray());
            }

            if (entry.IsBoolean)
            {
                if (string.Equals(proposed, "true", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(proposed, "false", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return "this is a yes/no setting, so it takes true or false - not '" + proposed + "'";
            }

            // A value containing a newline would silently become two settings when written back.
            if (proposed.IndexOf('\n') >= 0 || proposed.IndexOf('\r') >= 0)
            {
                return "a value cannot span more than one line";
            }

            return null;
        }

        /// <summary>Entries the editor has changed.</summary>
        public List<ModConfigEntry> Changes()
        {
            var changed = new List<ModConfigEntry>();
            foreach (var entry in Entries)
            {
                if (entry.Modified) changed.Add(entry);
            }
            return changed;
        }

        /// <summary>
        /// Write the changed settings back, leaving everything else exactly as it was.
        /// </summary>
        /// <remarks>
        /// Rewrites only the lines the settings came from, so comments, blank lines, section order and
        /// any line this parser did not recognise survive untouched. That is the difference between an
        /// editor and a generator, and it is why the file keeps its documentation after being edited.
        /// </remarks>
        public void Save()
        {
            foreach (var entry in Entries)
            {
                if (!entry.Modified) continue;

                _lines[entry.Line] = entry.Key + " = " + entry.Value;

                // If the new value no longer matches what the file declared, the old annotation is now a
                // lie sitting directly above the value it describes. Dropping it is better than leaving a
                // comment that contradicts the setting; the mod rewrites the block on its next save.
                if (!entry.HasFixedValues && !entry.IsBoolean)
                {
                    _lines[entry.Line - 1] = "";
                }
            }

            var text = string.Join("\r\n", _lines.ToArray());
            File.WriteAllText(Path, text, _encoding);

            foreach (var entry in Entries) entry.OriginalValue = entry.Value;
        }

        /// <summary>The path a mod's config lives at, which is its GUID plus BepInEx's own naming.</summary>
        public static string PathFor(string configFolder, string guid)
        {
            if (string.IsNullOrEmpty(configFolder) || string.IsNullOrEmpty(guid)) return null;
            return System.IO.Path.Combine(configFolder, guid + ".cfg");
        }

        /// <summary>The config folder for an install, or null when BepInEx is not there.</summary>
        public static string FolderFor(string bepinexRoot)
        {
            if (string.IsNullOrEmpty(bepinexRoot)) return null;
            var folder = System.IO.Path.Combine(bepinexRoot, "config");
            return Directory.Exists(folder) ? folder : null;
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0} ({1} setting(s) in {2} section(s))", System.IO.Path.GetFileName(Path),
                Entries.Count, Sections.Count);
        }
    }
}