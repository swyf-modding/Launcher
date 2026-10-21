using System.Text.Json;

namespace Switchboard
{
    /// <summary>A downloadable file attached to a GitHub release.</summary>
    internal sealed class ReleaseAsset
    {
        /// <summary>
        /// A public constructor, because the URL path builds one too: there is no GitHub listing behind
        /// that download, so the caller supplies what would otherwise have been read from the API.
        /// </summary>
        public ReleaseAsset(string name, long size, string downloadUrl, string sha256)
        {
            Name = name;
            Size = size;
            DownloadUrl = downloadUrl;
            Sha256 = sha256;
        }

        public string Name { get; private set; }
        public long Size { get; private set; }
        public string DownloadUrl { get; private set; }

        /// <summary>
        /// GitHub's own SHA-256 for the asset, when it publishes one.
        /// </summary>
        /// <remarks>
        /// Worth having, and worth being precise about what it proves: it catches a truncated or mangled
        /// download, and it catches an asset host that is not GitHub's. It does not make the mod
        /// trustworthy - the digest comes from the same place as the file - so it is an integrity check,
        /// not a signature, and nothing in the UI should imply otherwise.
        /// </remarks>
        public string Sha256 { get; private set; }

        /// <summary>
        /// Read one asset out of a release's "assets" array, or null when the entry is not usable.
        /// </summary>
        /// <remarks>
        /// Shared rather than written twice, for the reason given on <see cref="GitHubJson"/>: the mod
        /// catalogue and the self-update both read release assets, and they have to read them the same
        /// way.
        /// </remarks>
        public static ReleaseAsset FromJson(JsonElement entry)
        {
            if (entry.ValueKind != JsonValueKind.Object) return null;

            var name = GitHubJson.Text(entry, "name");
            if (string.IsNullOrEmpty(name)) return null;

            var url = GitHubJson.Text(entry, "browser_download_url");
            if (string.IsNullOrEmpty(url)) return null;

            return new ReleaseAsset(name, GitHubJson.Number(entry, "size"), url,
                GitHubJson.Digest(entry, "digest"));
        }
    }
}
