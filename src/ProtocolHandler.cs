using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace Switchboard
{
    /// <summary>
    /// Registers the switchboard:// protocol so the website's install buttons open this app.
    /// </summary>
    /// <remarks>
    /// Per-user on both platforms, so no admin prompt: HKCU on Windows, a .desktop file on
    /// Linux. Runs on every startup and only writes when the registration is missing or
    /// points elsewhere, so moving the exe re-registers the new path. Never throws: a
    /// machine that refuses the registration still runs; only the website buttons miss.
    /// </remarks>
    internal static class ProtocolHandler
    {
        internal const string Scheme = "switchboard";

        public static void EnsureRegistered()
        {
            try
            {
                if (OperatingSystem.IsWindows()) EnsureWindows();
                else if (OperatingSystem.IsLinux()) EnsureLinux();
            }
            catch (Exception)
            {
                // Best effort by design.
            }
        }

        private static string ExePath()
        {
            try
            {
                var main = Process.GetCurrentProcess().MainModule;
                if (main != null && !string.IsNullOrEmpty(main.FileName) && File.Exists(main.FileName))
                {
                    return main.FileName;
                }
            }
            catch (Exception) { }

            try
            {
                var location = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(location) && File.Exists(location)) return location;
            }
            catch (Exception) { }

            return null;
        }

        private static void EnsureWindows()
        {
            var exe = ExePath();
            if (string.IsNullOrEmpty(exe)) return;

            var command = "\"" + exe + "\" \"%1\"";

            var current = ReadRegistry(@"Software\Classes\" + Scheme + @"\shell\open\command", "");
            if (string.Equals(current, command, StringComparison.OrdinalIgnoreCase)) return;

            WriteRegistry(@"Software\Classes\" + Scheme, "", "URL:Switchboard");
            WriteRegistry(@"Software\Classes\" + Scheme, "URL Protocol", "");
            WriteRegistry(@"Software\Classes\" + Scheme + @"\shell\open\command", "", command);
        }

        private static string ReadRegistry(string path, string name)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path, false))
                {
                    if (key == null) return null;
                    return key.GetValue(name) as string;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void WriteRegistry(string path, string name, string value)
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(path))
            {
                if (key == null) return;
                key.SetValue(name, value, Microsoft.Win32.RegistryValueKind.String);
            }
        }

        private static void EnsureLinux()
        {
            var exe = ExePath();
            if (string.IsNullOrEmpty(exe)) return;

            string home;
            try
            {
                home = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(home)) return;
            }
            catch (Exception) { return; }

            var dir = Path.Combine(home, "applications");
            var desktop = Path.Combine(dir, "switchboard.desktop");
            var entry = "[Desktop Entry]\n" +
                        "Name=Switchboard\n" +
                        "Comment=Install mods from the catalogue website\n" +
                        "Exec=\"" + exe.Replace("\"", "") + "\" %u\n" +
                        "Type=Application\n" +
                        "Terminal=false\n" +
                        "MimeType=x-scheme-handler/" + Scheme + ";\n" +
                        "NoDisplay=true\n";

            try
            {
                Directory.CreateDirectory(dir);

                string existing = null;
                if (File.Exists(desktop)) existing = File.ReadAllText(desktop);

                if (!string.Equals(existing, entry, StringComparison.Ordinal))
                {
                    File.WriteAllText(desktop, entry);
                }
            }
            catch (Exception) { return; }

            try
            {
                Process.Start(new ProcessStartInfo("update-desktop-database", "\"" + dir + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch (Exception) { }
        }
    }
}
