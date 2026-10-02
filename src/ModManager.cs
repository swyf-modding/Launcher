using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ScamWYF.Launcher
{
    /// <summary>
    /// Turning mods on and off, and removing them.
    /// </summary>
    /// <remarks>
    /// BepInEx has no enable/disable of its own. Its own convention is the folder: anything in
    /// plugins\ loads, anything in plugins_disabled\ does not. So a toggle is a file move, and this
    /// class does the move and nothing else - no bookkeeping of its own to disagree with what the
    /// loader will actually do.
    ///
    /// A name collision is refused rather than resolved. Two mods called the same thing is a real
    /// situation - a downgrade, or someone extracting twice - and silently picking a winner would hide
    /// it. The tool says so and leaves the files alone.
    /// </remarks>
    public sealed class ModManager
    {
        private readonly string _plugins;
        private readonly string _disabled;

        public ModManager(string bepinexCore)
        {
            if (string.IsNullOrEmpty(bepinexCore)) throw new ArgumentNullException("bepinexCore");

            var root = Directory.GetParent(bepinexCore);
            if (root == null) throw new ArgumentException("BepInEx\\core has no parent folder.", "bepinexCore");

            _plugins = Path.Combine(root.FullName, "plugins");
            _disabled = Path.Combine(root.FullName, "plugins_disabled");
        }

        public string PluginsFolder { get { return _plugins; } }
        public string DisabledFolder { get { return _disabled; } }

        /// <summary>Everything on disk, enabled first.</summary>
        public List<PluginEntry> Scan()
        {
            return PluginScanner.Scan(_plugins, _disabled);
        }

        /// <summary>
        /// The state a toggle should move to, and why it cannot right now if it cannot.
        /// </summary>
        /// <remarks>
        /// Checked rather than assumed, so a button can be disabled before it is pressed instead of
        /// throwing after it. The game holding the file is the usual reason: BepInEx does not release
        /// plugin assemblies when it unloads, so a mod cannot be moved while the game is running.
        /// </remarks>
        public Result CanToggle(PluginEntry entry)
        {
            if (entry == null) return Result.Fail("no mod selected");
            return CanToggle(entry.Path, !entry.Enabled);
        }

        public Result CanToggle(string path, bool wantEnabled)
        {
            var target = DirectoryOf(wantEnabled);
            var source = DirectoryOf(!wantEnabled);

            if (!File.Exists(path)) return Result.Fail("that file is gone; rescan first");

            var name = Path.GetFileName(path);
            var clash = Path.Combine(target, name);
            if (File.Exists(clash))
            {
                return Result.Fail("there is already a " + name + " in " +
                    (wantEnabled ? "plugins" : "plugins_disabled"));
            }

            return CheckGameNotRunning();
        }

        /// <summary>Move a mod between the two folders. Throws only if the precondition changed.</summary>
        public Result Toggle(PluginEntry entry)
        {
            var wantEnabled = !entry.Enabled;
            var check = CanToggle(entry);
            if (!check.Ok) return check;

            var target = Path.Combine(DirectoryOf(wantEnabled), Path.GetFileName(entry.Path));
            Directory.CreateDirectory(DirectoryOf(wantEnabled));

            File.Move(entry.Path, target);
            return Result.Good((wantEnabled ? "enabled " : "disabled ") + Path.GetFileName(entry.Path));
        }

        /// <summary>
        /// Whether a mod can be deleted, and why not if it cannot.
        /// </summary>
        /// <remarks>
        /// Deliberately not <see cref="CanToggle"/>. A toggle is a move, so a name already sitting in the
        /// destination would be overwritten; a delete has no destination and does not care. Refusing
        /// here would make it impossible to clear out an old copy while a newer one of the same name
        /// is enabled - which is exactly what someone cleaning up after a downgrade needs to do.
        ///
        /// Only the file lock matters, and that check is shared with the toggle.
        /// </remarks>
        public Result CanDelete(PluginEntry entry)
        {
            if (entry == null) return Result.Fail("no mod selected");
            if (!File.Exists(entry.Path)) return Result.Fail("that file is gone; rescan first");

            return CheckGameNotRunning();
        }

        /// <summary>
        /// Delete a mod for good.
        /// </summary>
        /// <remarks>
        /// Deleted rather than disabled, so the caller must have confirmed. Anything else - a
        /// quarantine folder, an undo - would mean this tool quietly accumulated copies of mods, and a
        /// delete that does not delete is worse than no delete at all.
        /// </remarks>
        public Result Delete(PluginEntry entry)
        {
            var check = CanDelete(entry);
            if (!check.Ok) return check;

            File.Delete(entry.Path);
            return Result.Good("deleted " + Path.GetFileName(entry.Path));
        }

        /// <summary>
        /// The one precondition a toggle and a delete share.
        /// </summary>
        /// <remarks>
        /// BepInEx does not unload plugin assemblies when it shuts down, so a running game holds the
        /// files open and a move or delete would fail deep inside the framework with a message about
        /// being in use. Saying it plainly up front is the whole reason this check exists.
        /// </remarks>
        private static Result CheckGameNotRunning()
        {
            if (!GameRunning(out var name)) return Result.Good();

            return Result.Fail("the game is running (" + name + "); close it first. BepInEx keeps " +
                "plugin files locked until it exits");
        }

        /// <summary>Re-read the folders. Called after every change so the list cannot go stale.</summary>
        public List<PluginEntry> Rescan()
        {
            return Scan();
        }

        private string DirectoryOf(bool enabled)
        {
            return enabled ? _plugins : _disabled;
        }

        /// <summary>
        /// Whether the game is running. Checked by process name, which is a heuristic: it can be
        /// fooled and it can miss a renamed copy, but it is the difference between a clear message
        /// and a file-lock exception, and the exception path still refuses rather than corrupting.
        /// </summary>
        internal static bool GameRunning(out string name)
        {
            foreach (var candidate in new[] { "Scam With Your Friends", "Scam With Your Friends Playtest" })
            {
                var found = SafeProcesses(candidate);
                if (found.Count > 0)
                {
                    name = found[0];
                    return true;
                }
            }

            name = null;
            return false;
        }

        private static List<string> SafeProcesses(string name)
        {
            try
            {
                // Exe name without extension is what Process reports, hence the two names above.
                var wanted = Path.GetFileNameWithoutExtension(name);
                return ProcessNames(wanted);
            }
            catch (Exception)
            {
                // If the process list cannot be read at all, report not-running. The subsequent file
                // move still fails loudly if the game really is holding the file.
                return new List<string>();
            }
        }

        private static List<string> ProcessNames(string wanted)
        {
            var names = new List<string>();
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(wanted))
            {
                using (process)
                {
                    names.Add(process.ProcessName);
                }
            }
            return names;
        }

        /// <summary>
        /// The outcome of an operation. Carries a sentence fit to show, because the interesting case
        /// is always the refusal and the refusal is what needs explaining.
        /// </summary>
        public struct Result
        {
            /// <summary>True when the operation happened. False means nothing changed.</summary>
            public readonly bool Succeeded;

            /// <summary>A sentence fit to show the user. Always set, because refusals are the point.</summary>
            public readonly string Message;

            private Result(bool ok, string message)
            {
                Succeeded = ok;
                Message = message;
            }

            /// <summary>Named with the trailing underscore because a member cannot share the struct's.</summary>
            public static Result Good(string message) { return new Result(true, message); }
            public static Result Good() { return new Result(true, ""); }
            public static Result Fail(string message) { return new Result(false, message); }

            /// <summary>Whether the operation happened. The field is Succeeded; this reads better at a call site.</summary>
            public bool Ok
            {
                get { return Succeeded; }
            }

            public override string ToString()
            {
                return Message;
            }
        }

        /// <summary>Unused today, but a mod list wants it: does the file look like ours?</summary>
        internal static bool LooksLikeBepInExMod(PluginEntry entry)
        {
            return entry != null && !string.IsNullOrEmpty(entry.Guid);
        }
    }
}
