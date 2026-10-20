using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Switchboard
{
    /// <summary>
    /// Runs a mod's optional setup script after installing, and streams what it says.
    /// </summary>
    /// <remarks>
    /// This is the half of the old switchboard SetupRunner that still ran PowerShell: the setup flow
    /// itself now runs in-process through ScamWYF.Setup.Core, but a mod can still ship a .ps1 that
    /// configures something per machine, and that is still a script rather than a library call.
    ///
    /// UI-free on purpose, like the rest of src\: the tests compile this file, and the caller is the
    /// one that decides what to do with a line or an exit code. Per-invocation -NoProfile and
    /// -ExecutionPolicy Bypass, like the old runner: nothing about the machine's own settings changes.
    ///
    /// Cross-platform: Windows PowerShell on Windows, PowerShell 7 (pwsh) everywhere else. -ExecutionPolicy
    /// is Windows-only and is not passed where the runtime would reject it.
    /// </remarks>
    internal static class ScriptRunner
    {
        /// <summary>
        /// Run a mod's .ps1 with the game folder as -GameDir, reporting each line as it appears.
        /// </summary>
        /// <param name="scriptPath">Full path to the script, out of the unpacked download.</param>
        /// <param name="gameDir">The game install, passed as -GameDir.</param>
        /// <param name="onLine">Called for each line of output. May be called from another thread.</param>
        /// <param name="onFinished">Called with the exit code, or -1 when the script could not run.</param>
        public static void RunScript(string scriptPath, string gameDir, Action<string> onLine,
                                     Action<int> onFinished)
        {
            var info = new ProcessStartInfo
            {
                FileName = PowerShellPath(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            // ArgumentList rather than a hand-quoted command line: a path with a space, on any platform,
            // is then passed as one argument rather than split by the shell.
            info.ArgumentList.Add("-NoProfile");

            // Windows PowerShell needs the bypass for a script the user just downloaded; PowerShell 7 on
            // Linux rejects the parameter, so it is only added where it is understood.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                info.ArgumentList.Add("-ExecutionPolicy");
                info.ArgumentList.Add("Bypass");
            }

            info.ArgumentList.Add("-File");
            info.ArgumentList.Add(scriptPath);
            info.ArgumentList.Add("-GameDir");
            info.ArgumentList.Add(gameDir);

            info.WorkingDirectory = Path.GetDirectoryName(scriptPath);

            Run(info, scriptPath, onLine, onFinished);
        }

        private static void Run(ProcessStartInfo info, string scriptPath, Action<string> onLine,
                                Action<int> onFinished)
        {
            try
            {
                using (var process = new Process { StartInfo = info })
                {
                    // Both streams fan into one callback: a script that writes a warning to stderr should
                    // read as a line of its output, not as a stray failure.
                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (e.Data != null && onLine != null) onLine(e.Data);
                    };
                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (e.Data != null && onLine != null) onLine(e.Data);
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    process.WaitForExit();

                    // WaitForExit with no timeout guarantees the async readers have drained. Called
                    // explicitly because it is the overload that makes that guarantee, and the last few
                    // lines are usually the verdict.
                    process.WaitForExit();

                    if (onFinished != null) onFinished(process.ExitCode);
                }
            }
            catch (Exception ex)
            {
                var name = scriptPath == null ? "the script" : Path.GetFileName(scriptPath);
                if (onLine != null) onLine("Could not run " + name + ": " + ex.Message);
                if (onFinished != null) onFinished(-1);
            }
        }

        /// <summary>
        /// PowerShell to run a script with.
        /// </summary>
        /// <remarks>
        /// Windows PowerShell first on Windows, because every Windows machine has it without installing
        /// anything, then PowerShell 7. On Linux and macOS only pwsh exists. If neither is present,
        /// Process.Start throws and the caller reports it rather than the switchboard failing to start.
        /// </remarks>
        private static string PowerShellPath()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return "pwsh";
            }

            var system = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");

            return File.Exists(system) ? system : "powershell.exe";
        }
    }
}
