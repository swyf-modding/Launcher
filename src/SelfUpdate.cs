using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Switchboard
{
    /// <summary>
    /// Replacing this program with a newer published build of itself.
    /// </summary>
    /// <remarks>
    /// The release is one executable rather than a zip, so there is nothing for a person to unpack:
    /// the app can fetch the published file and put it where this one is running from. The
    /// awkward part is that a running executable cannot be overwritten - Windows locks the image -
    /// so the replacement is done by a script started just before this program exits.
    ///
    /// The script is the whole trick, and it is deliberately a script rather than a second copy of
    /// this program: a copy would be the very file being replaced, and would have the same problem.
    /// A short batch file or shell script is started detached, waits for this process to go, copies
    /// the new file into place, and starts it. It carries its settings in a file beside it rather
    /// than in its command line, because the paths have to survive being read by two different
    /// shells with two different ideas of quoting.
    /// </remarks>
    internal static class SelfUpdate
    {
        /// <summary>
        /// This program's own name, without its extension.
        /// </summary>
        /// <remarks>
        /// The name the project publishes under, which is also the name of the file that runs.
        /// </remarks>
        public static string ProgramName
        {
            get
            {
                var path = Environment.ProcessPath;
                if (string.IsNullOrEmpty(path)) return "Switchboard";
                return Path.GetFileNameWithoutExtension(path);
            }
        }

        /// <summary>
        /// Whether the published file is this program, and so may replace it.
        /// </summary>
        /// <remarks>
        /// The release assets are named after the project, and the project has been renamed once.
        /// A release published before the rename carries the old name, and installing that would
        /// replace this program with a different one - a build that calls itself something else
        /// and is laid out differently. So the name has to match. When it does not, the release
        /// page is still there and the person can still do it by hand.
        /// </remarks>
        public static bool IsThisProgram(ReleaseAsset asset)
        {
            if (asset == null) return false;

            var name = Path.GetFileNameWithoutExtension(asset.Name);
            return string.Equals(name, ProgramName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Download the published build, then hand the replacement to a script and exit.
        /// </summary>
        /// <param name="asset">The published file for this platform.</param>
        /// <param name="progress">Called with a line to show, for the download's progress.</param>
        /// <remarks>
        /// The download is checked against the SHA-256 GitHub published for the asset before
        /// anything is installed, so a corrupted or substituted file is discarded rather than run.
        /// That is integrity rather than a signature - it says the bytes are the ones GitHub
        /// published, not that anybody signed them - but it is the same guarantee a mod download
        /// gets, and it is the one that catches a broken download.
        /// </remarks>
        public static void Apply(ReleaseAsset asset, Action<string> progress)
        {
            var current = Environment.ProcessPath;
            if (string.IsNullOrEmpty(current))
            {
                throw new IOException("cannot tell where this program is running from");
            }

            if (!IsThisProgram(asset))
            {
                throw new IOException("that release publishes \"" + asset.Name + "\", which is a different " +
                                      "program from this one, so it was not installed");
            }

            if (asset.Sha256 == null)
            {
                throw new IOException("that release does not publish a checksum for its file, so it cannot " +
                                      "be installed automatically");
            }

            var download = Path.Combine(Path.GetTempPath(),
                "switchboard-update-" + Guid.NewGuid().ToString("N") + Path.GetExtension(current));

            string digest;
            try
            {
                digest = Http.DownloadUpdate(asset.DownloadUrl, download, (done, total) =>
                {
                    if (progress == null) return;
                    var line = total > 0
                        ? "Downloading " + ProgramName + " " + asset.Name + "... " +
                          Http.Describe(done) + " of " + Http.Describe(total)
                        : "Downloading " + ProgramName + " " + asset.Name + "... " + Http.Describe(done);
                    progress(line);
                });
            }
            catch
            {
                // A half-written file in the temp folder is worse than no file at all.
                TryDelete(download);
                throw;
            }

            if (!string.Equals(digest, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(download);
                throw new IOException("the download does not match the checksum GitHub published for it, so " +
                                      "it was discarded");
            }

            if (progress != null) progress("Starting the update...");

            var script = WriteScript(current, download, Environment.ProcessId, Arguments(), NewId());
            StartScript(script);

            // The script cannot replace a running executable, so this one has to be out of the way
            // first. The script waits for it and retries, so the order is not delicate.
            Thread.Sleep(600);
            Environment.Exit(0);
        }

        /// <summary>
        /// The arguments this program was started with, without the program's own path.
        /// </summary>
        /// <remarks>
        /// A game path is how a developer points the tool at an install, so the new build has to be
        /// started the same way rather than with no arguments at all.
        /// </remarks>
        internal static string[] Arguments()
        {
            var args = Environment.GetCommandLineArgs();
            var result = new string[Math.Max(0, args.Length - 1)];
            for (int i = 1; i < args.Length; i++) result[i - 1] = args[i];
            return result;
        }

        /// <summary>A name that will not collide with another update running at the same time.</summary>
        internal static string NewId()
        {
            return Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// Write the script that does the replacing, and the file it reads its settings from.
        /// </summary>
        /// <remarks>
        /// The settings are a file rather than arguments because the paths have to survive being
        /// read by two different shells: a Windows path cannot contain a quote, but it can contain
        /// a percent sign, which a batch file would otherwise try to expand.
        /// </remarks>
        internal static string WriteScript(string target, string download, int pid, string[] args, string id)
        {
            var folder = Path.GetTempPath();

            var config = Path.Combine(folder, "switchboard-update-" + id + ".txt");
            var script = Path.Combine(folder, "switchboard-update-" + id + (OperatingSystem.IsWindows() ? ".cmd" : ".sh"));

            var settings = new StringBuilder();
            settings.Append("TARGET|").Append(target).Append('\n');
            settings.Append("NEW|").Append(download).Append('\n');
            settings.Append("PID|").Append(pid).Append('\n');

            if (OperatingSystem.IsWindows())
            {
                // One line, already quoted the way a Windows command line wants it. The batch file
                // passes it straight to start, and a line per argument would need the arguments
                // accumulated - which a batch file cannot do, because a variable used inside a
                // for loop is expanded when the loop is parsed rather than when it runs.
                settings.Append("ARGS|").Append(WindowsArguments(args)).Append('\n');
            }
            else
            {
                // One line each: the shell passes them along as separate arguments, which is what
                // keeps an argument with a space in it as one argument.
                if (args != null)
                {
                    foreach (var arg in args)
                    {
                        settings.Append("ARG|").Append(arg).Append('\n');
                    }
                }
            }

            File.WriteAllText(config, settings.ToString());

            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(script, WindowsScript());
            }
            else
            {
                File.WriteAllText(script, UnixScript());
                TrySetExecutable(script);
            }

            return script;
        }

        /// <summary>
        /// The arguments as one Windows command line would take them.
        /// </summary>
        /// <remarks>
        /// The quoting is the rule Windows itself uses: a backslash is doubled only where it would
        /// otherwise escape the quote that follows it, and everything else is left alone.
        /// </remarks>
        internal static string WindowsArguments(string[] args)
        {
            var text = new StringBuilder();
            if (args == null) return "";

            foreach (var arg in args)
            {
                if (text.Length > 0) text.Append(' ');
                text.Append(Quote(arg));
            }

            return text.ToString();
        }

        private static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;

            var text = new StringBuilder();
            text.Append('"');

            for (int i = 0; i < arg.Length; i++)
            {
                int slashes = 0;
                while (i < arg.Length && arg[i] == '\\') { slashes++; i++; }

                if (i == arg.Length)
                {
                    // The run of backslashes is at the end, so it cannot escape anything.
                    text.Append('\\', slashes * 2);
                    break;
                }

                if (arg[i] == '"')
                {
                    // A quote has to be escaped, and the backslashes before it have to be doubled
                    // so they do not escape the escape.
                    text.Append('\\', slashes * 2 + 1).Append('"');
                }
                else
                {
                    text.Append('\\', slashes).Append(arg[i]);
                }
            }

            text.Append('"');
            return text.ToString();
        }

        /// <summary>
        /// The Windows half: a batch file that waits for this process to exit, then replaces the file.
        /// </summary>
        /// <remarks>
        /// It waits by retrying the copy rather than by asking about the process, because the copy
        /// fails with a sharing violation for exactly as long as the executable is running - so
        /// the retry is the wait, and it needs nothing from the system that might not be there.
        /// </remarks>
        private static string WindowsScript()
        {
            var lines = new StringBuilder();
            lines.Append("@echo off\r\n");
            lines.Append("rem Written by Switchboard. Replaces this program with a newer build and\r\n");
            lines.Append("rem starts it. The copy is retried because it fails for as long as the\r\n");
            lines.Append("rem running program holds the file open.\r\n");
            lines.Append("set \"CFG=%~dpn0.txt\"\r\n");
            lines.Append("for /f \"usebackq tokens=1,* delims=|\" %%a in (\"%CFG%\") do set \"%%a=%%b\"\r\n");
            lines.Append("set \"COUNT=0\"\r\n");
            lines.Append(":copy\r\n");
            lines.Append("copy /y \"%NEW%\" \"%TARGET%\" > nul 2>&1\r\n");
            lines.Append("if not errorlevel 1 goto done\r\n");
            lines.Append("set /a COUNT+=1\r\n");
            lines.Append("if %COUNT% GEQ 120 goto failed\r\n");
            lines.Append("timeout /t 1 /nobreak > nul\r\n");
            lines.Append("goto copy\r\n");
            lines.Append(":done\r\n");
            lines.Append("del /q \"%NEW%\" > nul 2>&1\r\n");
            lines.Append("if defined ARGS (\r\n");
            lines.Append("  start \"\" \"%TARGET%\" %ARGS%\r\n");
            lines.Append(") else (\r\n");
            lines.Append("  start \"\" \"%TARGET%\"\r\n");
            lines.Append(")\r\n");
            lines.Append("del /q \"%CFG%\" > nul 2>&1\r\n");
            lines.Append("del /q \"%~f0\" > nul 2>&1\r\n");
            lines.Append("exit /b 0\r\n");
            lines.Append(":failed\r\n");
            lines.Append("del /q \"%CFG%\" > nul 2>&1\r\n");
            lines.Append("exit /b 1\r\n");
            return lines.ToString();
        }

        /// <summary>
        /// The other half: a shell script that waits for this process to exit, then replaces the file.
        /// </summary>
        private static string UnixScript()
        {
            var lines = new StringBuilder();
            lines.Append("#!/bin/sh\n");
            lines.Append("# Written by Switchboard. Replaces this program with a newer build and starts it.\n");
            lines.Append("CFG=\"${0%.*}.txt\"\n");
            lines.Append("TARGET=\"\"\nNEW=\"\"\nPID=\"\"\n");
            lines.Append("while IFS='|' read -r key value; do\n");
            lines.Append("  case \"$key\" in\n");
            lines.Append("    TARGET) TARGET=\"$value\" ;;\n");
            lines.Append("    NEW) NEW=\"$value\" ;;\n");
            lines.Append("    PID) PID=\"$value\" ;;\n");
            lines.Append("  esac\n");
            lines.Append("done < \"$CFG\"\n");
            lines.Append("\n");
            lines.Append("# The arguments are collected separately so that one with a space in it\n");
            lines.Append("# stays one argument.\n");
            lines.Append("set --\n");
            lines.Append("while IFS='|' read -r key value; do\n");
            lines.Append("  if [ \"$key\" = \"ARG\" ]; then set -- \"$@\" \"$value\"; fi\n");
            lines.Append("done < \"$CFG\"\n");
            lines.Append("\n");
            lines.Append("# The copy fails while the running program holds the file open, so wait\n");
            lines.Append("# for it to go.\n");
            lines.Append("while kill -0 \"$PID\" 2>/dev/null; do sleep 1; done\n");
            lines.Append("cp -f \"$NEW\" \"$TARGET\" || exit 1\n");
            lines.Append("chmod +x \"$TARGET\" 2>/dev/null\n");
            lines.Append("rm -f \"$NEW\" \"$CFG\"\n");
            lines.Append("\"$TARGET\" \"$@\" >/dev/null 2>&1 &\n");
            lines.Append("rm -f \"$0\"\n");
            lines.Append("exit 0\n");
            return lines.ToString();
        }

        /// <summary>
        /// Start the script, detached, so that it outlives this program.
        /// </summary>
        /// <remarks>
        /// The script is a child process, and a child is not killed when its parent exits, so it
        /// keeps running after this program has gone. It is started with its own working
        /// directory and with its output piped nowhere, because it is a background job that
        /// nobody is waiting on - and because a child holding this program's standard output
        /// open would stop anything reading it from seeing the end of it.
        /// </remarks>
        internal static void StartScript(string script)
        {
            var info = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            if (OperatingSystem.IsWindows())
            {
                info.FileName = "cmd.exe";
                info.ArgumentList.Add("/c");
                info.ArgumentList.Add(script);
            }
            else
            {
                info.FileName = "/bin/sh";
                info.ArgumentList.Add(script);
            }

            var process = Process.Start(info);
            if (process == null) return;

            // Drained rather than ignored, so a pipe nobody reads cannot fill up and block the
            // script halfway through.
            process.OutputDataReceived += delegate (object s, DataReceivedEventArgs e) { };
            process.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e) { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        /// <summary>Make a file executable, where that is a thing.</summary>
        private static void TrySetExecutable(string path)
        {
            if (OperatingSystem.IsWindows()) return;

            try
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch (Exception)
            {
                // Without the execute bit the script cannot be run, and the update fails with a
                // clear message rather than silently doing nothing.
            }
        }

        /// <summary>Delete a file, ignoring the reason it could not be deleted.</summary>
        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
                // A leftover file in the temp folder is not worth a failed update.
            }
        }
    }
}
