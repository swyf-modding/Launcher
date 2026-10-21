using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Switchboard
{
    /// <summary>
    /// The only place this tool touches the network.
    /// </summary>
    /// <remarks>
    /// Everything downloaded here ends up in BepInEx\plugins (or BepInEx\core), which means it gets
    /// loaded and run by the game on the next launch. So the rules are not negotiable and they are all
    /// enforced here rather than at the call site:
    ///
    ///  - HTTPS only. A mod fetched over plaintext can be swapped by anyone on the path, and there is
    ///    no signature to catch it afterwards.
    ///  - Redirects are followed by hand, so a redirect from https to http is refused rather than
    ///    quietly obeyed. HttpClient's own auto-redirect would follow it, which would make the scheme
    ///    check above theatre.
    ///  - A size cap, because the largest thing anyone should be fetching here is a few-hundred-KB zip
    ///    and a server that says otherwise is either broken or hostile.
    ///  - A User-Agent, because the GitHub API rejects requests without one.
    ///
    /// Nothing here executes what it downloads. The bytes go to a temp file and are inspected with Cecil
    /// before anything is copied into the game folder; see <see cref="ModInstaller"/>.
    ///
    /// This is the Avalonia/.NET 10 port of a .NET Framework <see cref="HttpWebRequest"/> client. The
    /// calls stay synchronous, because the callers are the same worker threads they always were, and
    /// <c>HttpClient.Send</c> is genuinely synchronous - the hand-rolled redirect loop and the exact
    /// wording of every refusal are what the tests pin, and they are unchanged.
    /// </remarks>
    internal static class Http
    {
        /// <summary>Refused rather than truncated, so a huge file fails instead of half-installing.</summary>
        internal const long MaxDownloadBytes = 32L * 1024 * 1024;

        /// <summary>
        /// The cap for the self-update, which is a whole executable rather than a mod.
        /// </summary>
        /// <remarks>
        /// The mod cap is deliberately tight, because the largest thing a mod should be is a few
        /// hundred kilobytes. This download is the tool itself - tens of megabytes - so it needs a
        /// cap of its own rather than a larger version of the same one.
        /// </remarks>
        internal const long MaxUpdateBytes = 256L * 1024 * 1024;

        /// <summary>A JSON release listing is a few KB; this is only here to stop a runaway response.</summary>
        internal const int MaxTextBytes = 2 * 1024 * 1024;

        private const int MaxRedirects = 5;

        private const string UserAgent = "Switchboard";

        /// <summary>
        /// One client for the process.
        /// </summary>
        /// <remarks>
        /// A single <see cref="HttpClient"/> owns a connection pool, so building one per request would
        /// open a fresh TLS handshake to GitHub every time and exhaust the sockets under a handful of
        /// requests. Auto-redirect is off because the redirect loop below is the point.
        ///
        /// The timeout is generous rather than tight: it bounds a genuinely hung connection, and at the
        /// size cap even a slow link finishes well inside it.
        /// </remarks>
        private static readonly HttpClient Client = BuildClient();

        /// <summary>
        /// A client of its own for the self-update download.
        /// </summary>
        /// <remarks>
        /// The shared client's timeout is two minutes. That is right for a mod and wrong for the
        /// tool itself: the update is tens of megabytes, and on a slow link that takes longer
        /// than two minutes, so the timeout would abort it partway through and leave the person
        /// with nothing. A separate client with a longer leash keeps the mod path's bound intact
        /// rather than loosening it for everything.
        /// </remarks>
        private static readonly HttpClient UpdateClient = BuildUpdateClient();

        private static HttpClient BuildClient()
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(30),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10)
            };

            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
        }

        private static HttpClient BuildUpdateClient()
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(30)
            };

            return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        }

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

            using (var response = Send(url))
            {
                // Checked from the header first, so a runaway response is refused before it is read.
                var declared = response.Content.Headers.ContentLength;
                if (declared.HasValue && declared.Value > MaxTextBytes)
                {
                    throw new IOException("the response was implausibly large");
                }

                using (var stream = response.Content.ReadAsStream())
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
            return Download(url, destination, MaxDownloadBytes, Client, progress);
        }

        /// <summary>
        /// Fetch the self-update, which is a whole executable rather than a mod.
        /// </summary>
        /// <param name="url">https only.</param>
        /// <param name="destination">Full path. Overwritten if it exists.</param>
        /// <param name="progress">Called with (bytesSoFar, totalBytes); totalBytes is 0 if unsent.</param>
        /// <returns>The SHA-256 of what was written, lower-case hex.</returns>
        public static string DownloadUpdate(string url, string destination, Action<long, long> progress)
        {
            return Download(url, destination, MaxUpdateBytes, UpdateClient, progress);
        }

        /// <summary>
        /// Fetch a file to disk with a cap of its own, reporting progress as it goes.
        /// </summary>
        /// <param name="url">https only.</param>
        /// <param name="destination">Full path. Overwritten if it exists.</param>
        /// <param name="maxBytes">Refused rather than truncated, so a huge file fails instead of half-installing.</param>
        /// <param name="client">The client to use. Its timeout has to cover the whole download.</param>
        /// <param name="progress">Called with (bytesSoFar, totalBytes); totalBytes is 0 if unsent.</param>
        /// <returns>The SHA-256 of what was written, lower-case hex.</returns>
        public static string Download(string url, string destination, long maxBytes, HttpClient client, Action<long, long> progress)
        {
            if (!IsAcceptable(url)) throw new InvalidOperationException(ExplainRefusal(url));

            using (var response = Send(client, url))
            {
                var total = response.Content.Headers.ContentLength ?? 0;

                // Checked from the header, so an oversized file is refused before a single byte is
                // written rather than after filling the temp folder.
                if (total > maxBytes)
                {
                    throw new IOException("that file is " + Describe(total) + ", which is far larger than any " +
                                          "mod should be. Download aborted");
                }

                using (var input = response.Content.ReadAsStream())
                using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var sha = SHA256.Create())
                {
                    var buffer = new byte[81920];
                    long written = 0;
                    int read;

                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        written += read;
                        if (written > maxBytes)
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

        /// <summary>
        /// Fetch a URL, following redirects by hand so each hop's scheme can be checked.
        /// </summary>
        /// <remarks>
        /// The response body is left unread (<see cref="HttpCompletionOption.ResponseHeadersRead"/>),
        /// so a redirect is rejected before its body is downloaded and a large download can be checked
        /// against the cap before it is written.
        /// </remarks>
        private static HttpResponseMessage Send(string url)
        {
            return Send(Client, url);
        }

        /// <summary>
        /// Fetch a URL with a particular client, following redirects by hand so each hop's scheme can be checked.
        /// </summary>
        /// <remarks>
        /// The response body is left unread (<see cref="HttpCompletionOption.ResponseHeadersRead"/>),
        /// so a redirect is rejected before its body is downloaded and a large download can be checked
        /// against the cap before it is written.
        /// </remarks>
        private static HttpResponseMessage Send(HttpClient client, string url)
        {
            var current = url;

            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                if (!IsAcceptable(current))
                {
                    var refused = ExplainRefusal(current);
                    throw new InvalidOperationException(
                        hop == 0 ? refused : "the server redirected to a URL this tool will not fetch: " + refused);
                }

                HttpResponseMessage response;
                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, current);
                    request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                    response = client.Send(request, HttpCompletionOption.ResponseHeadersRead);
                }
                catch (Exception ex)
                {
                    throw ExplainTransport(ex, current);
                }

                var status = (int)response.StatusCode;
                var location = response.Headers.Location;

                // Any 3xx carrying a Location is a redirect. Enumerating the named codes would be
                // easy to get wrong; the range is exactly right.
                if (status >= 300 && status < 400 && location != null)
                {
                    response.Dispose();

                    // Resolve against the current URL so a relative Location works, then loop to have
                    // the scheme checked again.
                    current = new Uri(new Uri(current), location).AbsoluteUri;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var reason = response.ReasonPhrase;
                    response.Dispose();
                    throw StatusError(status, reason);
                }

                return response;
            }

            throw new IOException("too many redirects");
        }

        /// <summary>A non-success status as something a person can act on.</summary>
        private static Exception StatusError(int status, string reason)
        {
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

            var text = string.IsNullOrWhiteSpace(reason) ? "" : " " + reason;
            return new IOException("the server returned HTTP " + status + text);
        }

        /// <summary>Turn a transport failure into something a person can act on.</summary>
        private static Exception ExplainTransport(Exception ex, string url)
        {
            var inner = ex;

            // HttpClient wraps everything in HttpRequestException, and a timeout arrives as a
            // cancellation, so the useful cause is always further in.
            while (inner is HttpRequestException && inner.InnerException != null) inner = inner.InnerException;

            if (inner is SocketException socket)
            {
                switch (socket.SocketErrorCode)
                {
                    case SocketError.HostNotFound:
                    case SocketError.NoData:
                        return new IOException("could not reach " + Host(url) + ". The host name could not " +
                                               "be resolved.", ex);
                    case SocketError.ConnectionRefused:
                        return new IOException("could not reach " + Host(url) + ". The connection was refused.", ex);
                    case SocketError.TimedOut:
                        return new IOException("could not reach " + Host(url) + ". It timed out.", ex);
                }

                return new IOException("could not reach " + Host(url) + ". " + socket.Message, ex);
            }

            if (inner is AuthenticationException)
            {
                // Two quite different causes land here: a certificate that genuinely does not validate,
                // which is the alarming one, and a protocol the server will not speak, which is mundane.
                // Saying only "the certificate did not validate" sends someone hunting for an attack.
                return new IOException("could not reach " + Host(url) + ". The secure connection could not " +
                                       "be established. Either this machine's root certificates are out of " +
                                       "date, or the server and this build of .NET cannot agree on a TLS " +
                                       "version. A download that cannot be authenticated is not one to accept.", ex);
            }

            if (inner is TaskCanceledException || inner is OperationCanceledException || inner is TimeoutException)
            {
                return new IOException("could not reach " + Host(url) + ". It timed out.", ex);
            }

            return new IOException("could not reach " + Host(url) + ". " + inner.Message, ex);
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
