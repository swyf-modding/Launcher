using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ScamWYF.Launcher
{
    /// <summary>
    /// The only place this tool touches the network.
    /// </summary>
    /// <remarks>
    /// Everything downloaded here ends up in BepInEx\plugins, which means it gets loaded and run by the
    /// game on the next launch. So the rules are not negotiable and they are all enforced here rather
    /// than at the call site:
    ///
    ///  - HTTPS only. A mod fetched over plaintext can be swapped by anyone on the path, and there is
    ///    no signature to catch it afterwards.
    ///  - Redirects are followed by hand, so a redirect from https to http is refused rather than
    ///    quietly obeyed. The built-in auto-redirect would follow it, which would make the scheme check
    ///    above theatre.
    ///  - A size cap, because the largest thing anyone should be fetching here is a 60KB zip and a
    ///    server that says otherwise is either broken or hostile.
    ///  - A User-Agent, because the GitHub API rejects requests without one.
    ///
    /// Nothing here executes what it downloads. The bytes go to a temp file and are inspected with Cecil
    /// before anything is copied into the game folder; see <see cref="ModInstaller"/>.
    /// </remarks>
    internal static class Http
    {
        /// <summary>Refused rather than truncated, so a huge file fails instead of half-installing.</summary>
        internal const long MaxDownloadBytes = 32L * 1024 * 1024;

        /// <summary>A JSON release listing is a few KB; this is only here to stop a runaway response.</summary>
        internal const int MaxTextBytes = 2 * 1024 * 1024;

        private const int MaxRedirects = 5;

        private const string UserAgent = "ScamWYF.Launcher";

        /// <summary>
        /// Whether a URL is one this tool will fetch at all.
        /// </summary>
        /// <remarks>
        /// Kept separate from the fetch so a bad URL is refused with an explanation before anything
        /// happens, rather than as an exception from inside a socket.
        /// </remarks>
        public static bool IsAcceptable(string url)
        {
            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed)) return false;

            if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

            // A UNC path or file:// would be a local file read dressed up as a download, and the game
            // folder is not the only thing on this machine worth reading.
            return string.IsNullOrEmpty(parsed.UserInfo);
        }

        /// <summary>Why a URL was refused, in a sentence fit to show.</summary>
        public static string ExplainRefusal(string url)
        {
            Uri parsed;
            if (string.IsNullOrWhiteSpace(url)) return "enter a URL first";
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed)) return "that is not a full URL";

            if (string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                return "only https is accepted. A mod is code the game will run, and plain http can be " +
                       "replaced in transit by anyone on the network";
            }

            if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return "only https is accepted, not " + parsed.Scheme;
            }

            if (!string.IsNullOrEmpty(parsed.UserInfo)) return "remove the username and password from the URL";

            return "that URL cannot be fetched";
        }

        /// <summary>Fetch a small text resource, such as a release listing.</summary>
        public static string GetText(string url)
        {
            if (!IsAcceptable(url)) throw new InvalidOperationException(ExplainRefusal(url));

            var request = Open(url);
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            {
                if (stream == null) throw new IOException("the server sent no content");

                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var text = reader.ReadToEnd();
                    if (text.Length > MaxTextBytes) throw new IOException("the response was implausibly large");
                    return text;
                }
            }
        }

        /// <summary>
        /// Fetch a file to disk, reporting progress as it goes.
        /// </summary>
        /// <param name="url">https only.</param>
        /// <param name="destination">Full path. Overwritten if it exists.</param>
        /// <param name="progress">Called with (bytesSoFar, totalBytes); totalBytes is 0 if unsent.</param>
        /// <returns>The SHA-256 of what was written, lower-case hex.</returns>
        public static string Download(string url, string destination, Action<long, long> progress)
        {
            if (!IsAcceptable(url)) throw new InvalidOperationException(ExplainRefusal(url));

            var request = Open(url);
            long total;

            using (var response = (HttpWebResponse)request.GetResponse())
            {
                total = response.ContentLength;

                // Checked from the header, so an oversized file is refused before a single byte is
                // written rather than after filling the temp folder.
                if (total > MaxDownloadBytes)
                {
                    throw new IOException("that file is " + Describe(total) + ", which is far larger than any " +
                                          "mod should be. Download aborted");
                }

                using (var input = response.GetResponseStream())
                {
                    if (input == null) throw new IOException("the server sent no content");

                    using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var sha = SHA256.Create())
                    {
                        var buffer = new byte[81920];
                        long written = 0;
                        int read;

                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            written += read;
                            if (written > MaxDownloadBytes)
                            {
                                throw new IOException("the download passed the size limit partway through, so it " +
                                                      "was abandoned. The file is incomplete");
                            }

                            output.Write(buffer, 0, read);
                            sha.TransformBlock(buffer, 0, read, null, 0);

                            if (progress != null) progress(written, total);
                        }

                        sha.TransformFinalBlock(new byte[0], 0, 0);

                        if (progress != null) progress(written, written);

                        return ToHex(sha.Hash);
                    }
                }
            }
        }

        /// <summary>A request with redirects refused, so each hop can be checked.</summary>
        private static WebRequest Open(string url)
        {
            // .NET Framework defaults ServicePointManager to SSL3 and TLS 1.0, and GitHub refuses both, so
            // without this every request fails with a handshake error that reads like a certificate
            // problem. Set once, at the first use, rather than per request.
            //
            // Tls13 is not in the .NET Framework 4.8 enum on every patch level, so it is added by value
            // (12288) inside a guard: Tls12 alone is enough to talk to GitHub, and this is only so the
            // connection is not left on the older protocol once the runtime knows about the newer one.
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                if (Enum.IsDefined(typeof(SecurityProtocolType), 12288))
                {
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288;
                }
            }
            catch (NotSupportedException)
            {
                // An older runtime that does not know TLS 1.2 at all. The request below will fail with a
                // handshake error, which is clearer than refusing to try.
            }

            var current = url;

            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                if (!IsAcceptable(current))
                {
                    var refused = ExplainRefusal(current);
                    throw new InvalidOperationException(
                        hop == 0 ? refused : "the server redirected to a URL this tool will not fetch: " + refused);
                }

                var request = (HttpWebRequest)WebRequest.Create(current);
                request.AllowAutoRedirect = false;
                request.UserAgent = UserAgent;
                request.Timeout = 30000;
                request.ReadWriteTimeout = 60000;
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

                try
                {
                    var response = (HttpWebResponse)request.GetResponse();
                    var location = response.Headers["Location"];

                    // Any 3xx carrying a Location is a redirect. Enumerating the named codes would miss
                    // 308, because HttpStatusCode.PermanentRedirect does not exist in .NET Framework.
                    var isRedirect = (int)response.StatusCode >= 300 && (int)response.StatusCode < 400;

                    if (isRedirect && !string.IsNullOrWhiteSpace(location))
                    {
                        response.Close();

                        // Resolve against the current URL so a relative Location works, then loop to have
                        // the scheme checked again.
                        current = new Uri(new Uri(current), location).AbsoluteUri;
                        continue;
                    }

                    return request;
                }
                catch (WebException ex)
                {
                    throw Explain(ex, current);
                }
            }

            throw new IOException("too many redirects");
        }

        /// <summary>Turn a WebException into something a person can act on.</summary>
        private static Exception Explain(WebException ex, string url)
        {
            var response = ex.Response as HttpWebResponse;
            if (response == null)
            {
                return new IOException("could not reach " + Host(url) + ". " + Root(ex), ex);
            }

            using (response)
            {
                var status = (int)response.StatusCode;

                if (status == 403 || status == 429)
                {
                    return new IOException("GitHub refused the request (HTTP " + status + "). The public API " +
                                           "allows 60 requests an hour per address; wait a moment and try again");
                }

                if (status == 404)
                {
                    return new IOException("nothing there (HTTP 404). If this is a repository, it may be " +
                                           "private, renamed, or have no published release");
                }

                return new IOException("the server returned HTTP " + status + " " + response.StatusDescription, ex);
            }
        }

        private static string Root(WebException ex)
        {
            switch (ex.Status)
            {
                case WebExceptionStatus.NameResolutionFailure:
                    return "The host name could not be resolved.";
                case WebExceptionStatus.ConnectFailure:
                    return "The connection was refused.";
                case WebExceptionStatus.Timeout:
                    return "It timed out.";
                case WebExceptionStatus.TrustFailure:
                case WebExceptionStatus.SecureChannelFailure:
                    // Two quite different causes land here: a certificate that genuinely does not
                    // validate, which is the alarming one, and a protocol the server will not speak,
                    // which is mundane and was the actual cause here before Tls12 was set. Saying only
                    // "the certificate did not validate" sends someone hunting for an attack.
                    return "The secure connection could not be established (" + ex.Status + "). Either this " +
                           "machine's root certificates are out of date, or the server and this build of " +
                           ".NET cannot agree on a TLS version. A download that cannot be authenticated " +
                           "is not one to accept.";
                default:
                    return ex.Message;
            }
        }

        private static string Host(string url)
        {
            Uri parsed;
            return Uri.TryCreate(url, UriKind.Absolute, out parsed) ? parsed.Host : url;
        }

        internal static string ToHex(byte[] bytes)
        {
            var text = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) text.Append(b.ToString("x2"));
            return text.ToString();
        }

        /// <summary>A size in the units a person would use.</summary>
        public static string Describe(long bytes)
        {
            if (bytes < 0) return "unknown size";
            if (bytes < 1024) return bytes + " bytes";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.#") + " MB";
        }
    }
}