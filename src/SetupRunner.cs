using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ScamWYF.Launcher
{
    /// <summary>
    /// Runs the setup scripts and streams what they say into the window.
    /// </summary>
    /// <remarks>
    /// The scripts stay the thing that actually does the work. They are already idempotent, they are
    /// what CI runs, and their logic - restoring awaiters, finding vtable breaks - is Cecil work with
    /// real edge cases that has no business being rewritten in a GUI event handler. This class is
    /// plumbing: find PowerShell, run the script, forward output, report the exit code.
    ///
    /// Output is captured per line rather than dumped at the end, because the scripts take a while
    /// and a frozen window during a ten second corlib patch is indistinguishable from a hang.
    /// </remarks>
    internal sealed class SetupRunner
    {
        private readonly string _setupScript;

        public SetupRunner(string setupScript)
        {
            _setupScript = setupScript;
        }

        /// <summary>Whether the script this tool drives is actually there.</summary>
        public bool IsAvailable
        {
            get { return !string.IsNullOrEmpty(_setupScript) && File.Exists(_setupScript); }
        }

        /// <summary>
        /// Which Setup these scripts came from, as build.ps1 recorded it next to them.
        /// </summary>
        /// <remarks>
        /// The launcher and the Setup repo are released separately, and a launcher build pins the Setup
        /// tag it bundled. Reading that back is what makes "which scripts did this launcher ship with"
        /// answerable from the zip the user already has, rather than from a guess or a network call -
        /// which is the first question when a setup run does something unexpected.
        ///
        /// Null when the file is absent, which is a build made with -NoSetup rather than an error: the
        /// Setup tab is simply disabled, and saying so beats reporting an empty version.
        /// </remarks>
        public string BundledVersion
        {
            get
            {
                if (!IsAvailable) return null;

                try
                {
                    var stamp = Path.Combine(Path.GetDirectoryName(_setupScript), "SETUP-VERSION.txt");
                    if (!File.Exists(stamp)) return null;

                    // First non-blank line only. build.ps1 writes that one line as the contract and
                    // leaves explanatory prose after it, which is for a person reading the folder and
                    // would be noise in a table cell.
                    foreach (var line in File.ReadAllLines(stamp))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.Length > 0) return trimmed;
                    }
                }
                catch (IOException)
                {
                    // A version string is not worth failing a tab load over.
                }
                catch (UnauthorizedAccessException)
                {
                }

                return null;
            }
        }

        /// <summary>Where to tell the user to get it, when it is not there.</summary>
        /// <remarks>
        /// No longer "clone the Setup repo". The scripts ship inside the launcher's own folder now, so
        /// the only way to be in this state is a build that left them out - a source build with
        /// <c>-NoSetup</c>, or a download that was unpacked incompletely. Both are answered the same
        /// way: get the whole launcher again. Telling someone to clone a second repository was the
        /// thing being removed.
        /// </remarks>
        public string MissingReason
        {
            get
            {
                return "The setup scripts are missing from this folder.\n\n" +
                    "They are part of the download - setup.ps1 and a Setup\\ folder beside this exe - " +
                    "so this is an incomplete unzip rather than something to install.\n\n" +
                    "Get ScamWYF.Launcher again from\n" +
                    "  https://github.com/swyf-modding/Launcher/releases\n" +
                    "and keep the Setup\\ folder next to the exe.\n\n" +
                    "Building from source instead? See build.ps1 -SetupPath.";
            }
        }

        /// <summary>
        /// Run setup.ps1 against an install, reporting each line as it appears.
        /// </summary>
        /// <param name="install">Which game to set up.</param>
        /// <param name="offline">Do not download BepInEx; it must already be there.</param>
        /// <param name="onLine">Called for each line of output. May be called from another thread.</param>
        public void Run(GameInstall install, bool offline, Action<string> onLine, Action<int> onFinished)
        {
            var info = new ProcessStartInfo
            {
                FileName = PowerShellPath(),
                Arguments = BuildArguments(install, offline),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            // Run from the repo, so its relative vendor\ paths resolve.
            info.WorkingDirectory = Path.GetDirectoryName(_setupScript);

            try
            {
                using (var process = new Process { StartInfo = info, EnableRaisingEvents = true })
                {
                    var both = new Both(process, onLine);

                    process.OutputDataReceived += both.Line;
                    process.ErrorDataReceived += both.Line;

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    process.WaitForExit();

                    // Give the async readers a moment to drain what is left after WaitForExit, or the
                    // last few lines are lost - which is usually the verdict.
                    both.Drain();

                    if (onFinished != null) onFinished(process.ExitCode);
                }
            }
            catch (Exception ex)
            {
                if (onLine != null) onLine("Could not run the setup script: " + ex.Message);
                if (onFinished != null) onFinished(-1);
            }
        }

        private string BuildArguments(GameInstall install, bool offline)
        {
            // -NoProfile so a user profile with a custom prompt or a module that writes to the
            // console cannot change what this prints, and -ExecutionPolicy Bypass because a default
            // Windows policy blocks running a script the user just cloned. Both are per-invocation,
            // so nothing about the machine's own settings is changed.
            var args = new StringBuilder();
            args.Append("-NoProfile -ExecutionPolicy Bypass -File \"");
            args.Append(_setupScript);
            args.Append("\" -GameDir \"");
            args.Append(install.Path);
            args.Append('"');

            if (offline) args.Append(" -Offline");
            return args.ToString();
        }

        /// <summary>
        /// PowerShell to run it with.
        /// </summary>
        /// <remarks>
        /// Windows PowerShell first, then PowerShell 7. The scripts are written for 5.1 - no
        /// ternaries, no null-coalescing - because that is what every Windows machine has without
        /// installing anything, and a tool that needs a 40MB runtime to fix someone's game install is
        /// a tool nobody runs.
        /// </remarks>
        private static string PowerShellPath()
        {
            var system = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe");

            return File.Exists(system) ? system : "powershell.exe";
        }

        /// <summary>Fans both output streams into one callback, tagged so a warning is not a stray line.</summary>
        private sealed class Both
        {
            private readonly Process _process;
            private readonly Action<string> _onLine;
            private readonly object _gate = new object();

            public Both(Process process, Action<string> onLine)
            {
                _process = process;
                _onLine = onLine;
            }

            public void Line(object sender, DataReceivedEventArgs e)
            {
                if (e.Data == null || _onLine == null) return;
                _onLine(e.Data);
            }

            /// <summary>
            /// Wait for the readers to finish. WaitForExit with no timeout does this on its own in
            /// .NET 4; the parameterless overload is called explicitly because it is the one that
            /// guarantees the async output has been flushed, and that guarantee is the point.
            /// </summary>
            public void Drain()
            {
                lock (_gate)
                {
                    _process.WaitForExit();
                }
            }
        }
    }
}
