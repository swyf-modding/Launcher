using System;
using System.Collections.Generic;
using System.IO;

namespace Switchboard
{
    /// <summary>
    /// Hands a website install request to the Switchboard that is already running.
    /// </summary>
    /// <remarks>
    /// A protocol click starts a second process even when the app is open: the OS maps the
    /// scheme to the exe and runs it again with the URL. The second process writes one small
    /// file here and exits; the first one watches the folder and installs from it. Files
    /// rather than a pipe because there is nothing to keep connected, both sides are short
    /// lived or already looping on a UI thread, and a file survives the race where the first
    /// window is still starting while the second one already leaves.
    /// </remarks>
    internal static class PendingInstalls
    {
        public static string Folder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Switchboard", "install-requests");
            }
        }

        /// <summary>Leave a request for the running instance. Never throws.</summary>
        public static void WriteRequest(InstallRequest request)
        {
            try
            {
                Directory.CreateDirectory(Folder);

                var name = Guid.NewGuid().ToString("N") + ".req";
                File.WriteAllText(
                    Path.Combine(Folder, name),
                    request.Owner + "/" + request.Repo + Environment.NewLine +
                    DateTime.UtcNow.Ticks.ToString());
            }
            catch (Exception)
            {
                // Best effort. The second process exits either way; the running one keeps
                // whatever it already had rather than failing over a handoff file.
            }
        }

        /// <summary>Take every waiting request, oldest first. Never throws.</summary>
        public static List<InstallRequest> TakeRequests()
        {
            var taken = new List<InstallRequest>();

            string[] files;
            try
            {
                if (!Directory.Exists(Folder)) return taken;
                files = Directory.GetFiles(Folder, "*.req");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return taken;
            }

            foreach (var file in files)
            {
                try
                {
                    var first = "";
                    using (var reader = new StreamReader(file))
                    {
                        first = (reader.ReadLine() ?? "").Trim();
                    }

                    var slash = first.IndexOf('/');
                    if (slash > 0)
                    {
                        var owner = first.Substring(0, slash).Trim();
                        var repo = first.Substring(slash + 1).Trim();

                        if (ModCatalogue.IsAccountName(owner) && ModCatalogue.IsAccountName(repo))
                        {
                            taken.Add(new InstallRequest(owner, repo));
                        }
                    }
                }
                catch (Exception)
                {
                    // A file that cannot be read is not a request.
                }

                try { File.Delete(file); }
                catch (Exception) { }
            }

            return taken;
        }
    }
}
