using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FOEReLauncher
{
    /// <summary>
    /// Downloads one file with resume: the bytes go to &lt;target&gt;.part, a later call continues it with an
    /// HTTP Range request, and the file becomes &lt;target&gt; only when its SHA-256 matches. A wrong hash
    /// throws away the part and downloads the whole file once more. Redirects (release assets go to a
    /// CDN) are followed by the HttpClient handler.
    /// </summary>
    public sealed class Downloader
    {
        private const int BufferSize = 1 << 16;

        private readonly HttpClient _http;

        public Downloader(HttpClient http) => _http = http;

        /// <summary>Bytes received from the network by this instance (not counting resumed bytes on disk).</summary>
        public long NetworkBytes { get; private set; }

        /// <summary>Requests that continued a part file with a Range header and got 206 back.</summary>
        public int ResumedRequests { get; private set; }

        /// <param name="onBytes">Called with the number of new bytes on disk (resumed bytes are reported once at start).</param>
        public async Task DownloadVerifiedAsync(
            string url,
            string target,
            long expectedSize,
            string expectedSha256,
            Action<long>? onBytes,
            CancellationToken ct)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await DownloadToPartAsync(url, target + ".part", expectedSize, onBytes, ct).ConfigureAwait(false);
                var sha = Hashing.Sha256OfFile(target + ".part");
                if (string.Equals(sha, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }

                    File.Move(target + ".part", target);
                    return;
                }

                // A part from another file version or a damaged transfer: start from zero once more.
                var length = new FileInfo(target + ".part").Length;
                File.Delete(target + ".part");
                onBytes?.Invoke(-length);
            }

            throw new InvalidDataException("hash mismatch after a full download: " + url);
        }

        private async Task DownloadToPartAsync(string url, string part, long expectedSize, Action<long>? onBytes, CancellationToken ct)
        {
            var existing = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (expectedSize > 0 && existing > expectedSize)
            {
                File.Delete(part);
                existing = 0;
            }

            if (existing > 0)
            {
                onBytes?.Invoke(existing);
            }

            if (expectedSize > 0 && existing == expectedSize)
            {
                return;
            }

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                if (existing > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(existing, null);
                }

                using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (existing > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                    {
                        // The part is as long as the file or longer: the hash check decides.
                        return;
                    }

                    response.EnsureSuccessStatusCode();
                    var append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
                    if (existing > 0 && !append)
                    {
                        // The server ignored the Range header and sends everything again.
                        onBytes?.Invoke(-existing);
                    }

                    if (append)
                    {
                        ResumedRequests++;
                    }

                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, BufferSize))
                    {
                        var buffer = new byte[BufferSize];
                        while (true)
                        {
                            var read = await input.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                            if (read <= 0)
                            {
                                break;
                            }

                            await output.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                            NetworkBytes += read;
                            onBytes?.Invoke(read);
                        }
                    }
                }
            }
        }
    }

    public static class Hashing
    {
        public static string Sha256OfFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                return Hex(sha.ComputeHash(stream));
            }
        }

        public static string Hex(byte[] bytes)
        {
            var chars = new char[bytes.Length * 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                var b = bytes[i];
                chars[i * 2] = "0123456789abcdef"[b >> 4];
                chars[(i * 2) + 1] = "0123456789abcdef"[b & 15];
            }

            return new string(chars);
        }
    }
}
