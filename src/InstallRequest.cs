using System;

namespace Switchboard
{
    /// <summary>
    /// A mod the website asked Switchboard to install, carried on a protocol link.
    /// </summary>
    /// <remarks>
    /// The link looks like switchboard://install?owner=X&amp;repo=Y, which is what the
    /// mod pages on the site point at. The owner and repo name a catalogue entry, not a
    /// file: the install still goes through the catalogue, the pinned release, the hash
    /// checks and the confirmation dialog, exactly as if the row had been picked by hand.
    /// </remarks>
    internal sealed class InstallRequest
    {
        public string Owner { get; private set; }
        public string Repo { get; private set; }

        public InstallRequest(string owner, string repo)
        {
            Owner = owner;
            Repo = repo;
        }

        public string QualifiedName
        {
            get { return Owner + "/" + Repo; }
        }

        /// <summary>
        /// Whether this launch carries a website install request, and what it names.
        /// </summary>
        /// <remarks>
        /// Scans every argument rather than only the first: Windows passes the protocol URL
        /// as one argument, but quoting and wrappers vary, so position is not trusted.
        /// A game folder path never starts with the scheme, so the two cannot be confused.
        /// </remarks>
        public static bool TryFind(string[] args, out InstallRequest request)
        {
            request = null;

            if (args == null) return false;

            foreach (var arg in args)
            {
                if (TryParse(arg, out request)) return true;
            }

            return false;
        }

        /// <summary>One argument as a request, or null when it is something else.</summary>
        public static bool TryParse(string arg, out InstallRequest request)
        {
            request = null;

            if (string.IsNullOrWhiteSpace(arg)) return false;

            var text = arg.Trim().Trim('"');

            Uri uri;
            if (!Uri.TryCreate(text, UriKind.Absolute, out uri)) return false;
            if (!string.Equals(uri.Scheme, "switchboard", StringComparison.OrdinalIgnoreCase)) return false;

            // switchboard://install?owner=X&repo=Y is the shape the site writes.
            string owner = null;
            string repo = null;

            if (!string.IsNullOrEmpty(uri.Query))
            {
                var query = uri.Query.TrimStart('?');
                foreach (var pair in query.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var equals = pair.IndexOf('=');
                    if (equals < 0) continue;

                    var key = Uri.UnescapeDataString(pair.Substring(0, equals)).Trim();
                    var value = Uri.UnescapeDataString(pair.Substring(equals + 1)).Trim();

                    if (string.Equals(key, "owner", StringComparison.OrdinalIgnoreCase)) owner = value;
                    else if (string.Equals(key, "repo", StringComparison.OrdinalIgnoreCase)) repo = value;
                }
            }

            // switchboard://install/owner/repo also parses, so hand-typed links work.
            if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
            {
                var path = (uri.AbsolutePath ?? "").Trim('/');
                var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                var start = parts.Length > 0 && string.Equals(parts[0], "install", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

                if (owner == null && parts.Length > start) owner = Uri.UnescapeDataString(parts[start]).Trim();
                if (repo == null && parts.Length > start + 1) repo = Uri.UnescapeDataString(parts[start + 1]).Trim();
            }

            if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo)) return false;
            if (!ModCatalogue.IsAccountName(owner) || !ModCatalogue.IsAccountName(repo)) return false;

            request = new InstallRequest(owner, repo);
            return true;
        }
    }
}
