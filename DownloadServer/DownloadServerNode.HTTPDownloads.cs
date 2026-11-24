/*
 * Copyright (c) 2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of DownloadServer <https://github.com/OpenChargingCloud/DownloadServer>
 *
 * Licensed under the Affero GPL license, Version 3.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.gnu.org/licenses/agpl.html
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using System.Text;
using System.Globalization;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.DownloadServer
{

    /// <summary>
    /// One byte range of a file, both ends included.
    /// </summary>
    /// <param name="First">The first byte.</param>
    /// <param name="Last">The last byte.</param>
    public readonly record struct ByteRange(Int64 First, Int64 Last)
    {

        /// <summary>
        /// How many bytes it covers.
        /// </summary>
        public Int64 Length
            => Last - First + 1;

    }


    /// <summary>
    /// The port that serves the files by HTTP GET.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>GET /&lt;area&gt;/&lt;file name&gt;</c>: the file as it is, or - with a
    /// "Range" of one range of bytes - the part of it asked for (206), so that
    /// a download that stopped is taken up where it stopped. A range that lies
    /// beyond the end is 416, with the length of the file; several ranges at
    /// once, and a range with an "If-Range" the file no longer matches, are
    /// answered with the whole file. HEAD says what a GET would, without the
    /// file. This is what a charging station is given as the location of
    /// OCPP's UpdateFirmware:
    /// <c>https://CS001:password@downloads.example.org:8365/firmware/station-4.2.1.bin</c>.
    /// </para>
    /// <para>
    /// Signed in with HTTP Basic Auth - or an API key, or a session - as an
    /// account that may download: the "downloader" role. Without credentials
    /// only where the section says "anonymousHTTP": true. A file that is not
    /// there is 404 to everybody, signed in or not. Nothing is taken on this
    /// port: no PUT, no page, no API.
    /// </para>
    /// </remarks>
    public partial class DownloadServerNode
    {

        #region Data

        /// <summary>
        /// The downloader a download without credentials is written down as.
        /// </summary>
        public const String  AnonymousDownloader  = "anonymous";

        /// <summary>
        /// The realm an HTTP client is asked to sign in to.
        /// </summary>
        public const String  DownloadRealm        = "OpenChargingCloud DownloadServer";

        #endregion


        #region (private) RegisterHTTPDownloads(Server)

        private void RegisterHTTPDownloads(HTTPServer Server)
        {

            var api = Server.AddHTTPAPI(HTTPPath.Root);

            foreach (var method in new[] { HTTPMethod.GET, HTTPMethod.HEAD })
            {
                api.AddHandler(HTTPPath.Root,               ServeDownload,  method);
                api.AddHandler(HTTPPath.Root + "{path..}",  ServeDownload,  method);
            }

            // What else is asked of this port is told what it is for.
            foreach (var method in new[] { HTTPMethod.PUT, HTTPMethod.POST, HTTPMethod.PATCH, HTTPMethod.DELETE })
            {
                api.AddHandler(HTTPPath.Root,               OnlyGet,  method);
                api.AddHandler(HTTPPath.Root + "{path..}",  OnlyGet,  method);
            }

        }

        #endregion

        #region (private) ServeDownload(Request)

        private Task<HTTPResponse> ServeDownload(HTTPRequest Request)
        {

            var from = RemoteOf(Request);

            #region Who downloads

            String downloader;

            if (ExtAPI.TryGetHTTPUser(Request, out var user) && user is not null)
            {

                if (!IsAllowed(user, [ Permission.Run(DownloadServerAccess.Download) ]))
                {

                    Interlocked.Increment(ref httpDownloadsRefused);

                    Log.Warning($"'{user.Id}' was refused an HTTP download of {Request.Path} from {from}: the account is not in the {DownloadServerAccess.Downloader.Name} role.",
                                "downloadserver", "downloads", "http", "auth");

                    return Task.FromResult(Answer(Request, HTTPStatusCode.Forbidden, $"'{user.Id}' may not download: the account is not in the {DownloadServerAccess.Downloader.Name} role."));

                }

                downloader = user.Id.ToString();

            }

            else if (Request.Authorization is null && Settings.AnonymousHTTP)
                downloader = AnonymousDownloader;

            else
            {

                Interlocked.Increment(ref httpDownloadsRefused);

                if (Request.Authorization is not null)
                    Log.Warning($"An HTTP download of {Request.Path} from {from} was refused: its credentials open no account.",
                                "downloadserver", "downloads", "http", "auth");

                return Task.FromResult(
                           new HTTPResponse.Builder(Request) {
                               HTTPStatusCode   = HTTPStatusCode.Unauthorized,
                               WWWAuthenticate  = WWWAuthenticate.Basic(DownloadRealm),
                               ContentType      = HTTPContentType.Application.JSON_UTF8,
                               Content          = Encoding.UTF8.GetBytes(new JObject(new JProperty("error", "Sign in with the account of a downloader.")).ToString(Formatting.None)),
                               CacheControl     = "no-store",
                               Connection       = ConnectionType.Close
                           }.AsImmutable
                       );

            }

            #endregion

            #region Which file

            var parts = Request.Path.ToString().
                                Split('/', StringSplitOptions.RemoveEmptyEntries).
                                Select(Uri.UnescapeDataString).
                                ToArray();

            if (parts.Length != 2 ||
                !TryGetFile(parts[0], parts[1], out var file, out var path))
            {

                Interlocked.Increment(ref httpDownloadsRefused);

                Log.Info($"'{downloader}' asked for {Request.Path} from {from}, which is no file offered here.", "downloadserver", "downloads", "http");

                return Task.FromResult(Answer(Request, HTTPStatusCode.NotFound, $"There is no file {Request.Path}: files are fetched as /<area>/<file name>."));

            }

            #endregion

            #region Which part of it

            var etag          = ETagOf(file);
            var lastModified  = new DateTimeOffset(file.Modified.UtcDateTime.Ticks - file.Modified.UtcDateTime.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);

            ByteRange? range  = null;

            if (Request.Range is String rangeHeader &&
                IfRangeHolds(Request.IfRange, etag, lastModified))
            {

                if (!TryParseRange(rangeHeader, file.Size, out range, out var unsatisfiable))
                    range = null;

                else if (unsatisfiable)
                    return Task.FromResult(
                               new HTTPResponse.Builder(Request) {
                                   HTTPStatusCode  = HTTPStatusCode.RequestedRangeNotSatisfiable,
                                   ContentRange    = $"bytes */{file.Size}",
                                   AcceptRanges    = "bytes",
                                   ETag            = etag,
                                   ContentType     = HTTPContentType.Application.JSON_UTF8,
                                   Content         = Encoding.UTF8.GetBytes(new JObject(new JProperty("error", $"{file.Path} has {file.Size} bytes: there is nothing from byte {rangeHeader.Split('=', 2).ElementAtOrDefault(1)} on.")).ToString(Formatting.None)),
                                   CacheControl    = "no-store"
                               }.AsImmutable
                           );

            }

            var first   = range?.First  ?? 0;
            var length  = range?.Length ?? file.Size;

            #endregion

            var response = new HTTPResponse.Builder(Request) {
                               HTTPStatusCode  = range is not null ? HTTPStatusCode.PartialContent : HTTPStatusCode.OK,
                               ContentType     = HTTPContentType.Application.OCTETSTREAM,
                               ContentLength   = (UInt64) length,
                               AcceptRanges    = "bytes",
                               ETag            = etag,
                               LastModified    = lastModified,
                               CacheControl    = "no-cache"
                           };

            if (range is ByteRange part)
                response.ContentRange = $"bytes {part.First}-{part.Last}/{file.Size}";

            response.Set("Content-Disposition",    $"attachment; filename=\"{AsciiOf(file.Name)}\"; filename*=UTF-8''{Uri.EscapeDataString(file.Name)}");
            response.Set("X-Content-Type-Options", "nosniff");

            if (Request.HTTPMethod == HTTPMethod.HEAD)
                return Task.FromResult(response.AsImmutable);

            try
            {

                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

                stream.Seek(first, SeekOrigin.Begin);

                response.ContentStream = new FileSlice(stream, length);

            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
            {

                Log.Error($"{file.Path} could not be read for '{downloader}': {problem.Message}", "downloadserver", "downloads", "http");

                return Task.FromResult(Answer(Request, HTTPStatusCode.InternalServerError, "The file could not be read. The log of the download server says why."));

            }

            var download = Downloads.Record(DownloadProtocol.HTTP,
                                            downloader,
                                            file.Area,
                                            file.Name,
                                            range is not null ? 206 : 200,
                                            first,
                                            length,
                                            file.Size,
                                            from,
                                            Request.UserAgent);

            Log.Notice($"'{downloader}' downloads '{file.Path}' by HTTP from {from}: {SizeOf(length)}{(range is not null ? $" from byte {first} on" : "")}.",
                       "downloadserver", "downloads", "http");

            return Task.FromResult(response.AsImmutable);

        }

        #endregion


        #region (static) TryParseRange(Range, Size, out ByteRange, out Unsatisfiable)

        /// <summary>
        /// The one range of bytes a "Range" asks for - "bytes=500-999",
        /// "bytes=500-", "bytes=-500" - within a file of the given size.
        /// </summary>
        /// <returns>
        /// False where the field is to be passed over, and the whole file
        /// sent: another unit, several ranges, or no range at all (RFC 9110,
        /// Section 14.2). True with Unsatisfiable where the range lies beyond
        /// the end of the file, which is 416.
        /// </returns>
        public static Boolean TryParseRange(String          Range,
                                            Int64           Size,
                                            out ByteRange?  Parsed,
                                            out Boolean     Unsatisfiable)
        {

            Parsed         = null;
            Unsatisfiable  = false;

            var text = Range.Trim();

            if (!text.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                return false;

            var spec = text[6..].Trim();

            if (spec.Contains(','))
                return false;

            var dash = spec.IndexOf('-');

            if (dash < 0)
                return false;

            var firstText  = spec[..dash].Trim();
            var lastText   = spec[(dash + 1)..].Trim();

            // "-500": the last 500 bytes.
            if (firstText.Length == 0)
            {

                if (!Int64.TryParse(lastText, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix))
                    return false;

                if (suffix == 0 || Size == 0)
                {
                    Unsatisfiable = true;
                    return true;
                }

                Parsed = new ByteRange(Math.Max(0, Size - suffix), Size - 1);
                return true;

            }

            if (!Int64.TryParse(firstText, NumberStyles.None, CultureInfo.InvariantCulture, out var first))
                return false;

            Int64? last = null;

            if (lastText.Length > 0)
            {

                if (!Int64.TryParse(lastText, NumberStyles.None, CultureInfo.InvariantCulture, out var lastByte) || lastByte < first)
                    return false;

                last = lastByte;

            }

            if (first >= Size)
            {
                Unsatisfiable = true;
                return true;
            }

            Parsed = new ByteRange(first, Math.Min(last ?? Size - 1, Size - 1));
            return true;

        }

        #endregion

        #region (static) ETagOf(File)

        /// <summary>
        /// The entity tag of a file: its SHA-256, so that the same file is the
        /// same tag however often it is published, and another one never is.
        /// </summary>
        public static String ETagOf(OfferedFile File)

            => $"\"{File.SHA256}\"";

        #endregion

        #region (private static) IfRangeHolds(IfRange, ETag, LastModified)

        /// <summary>
        /// Whether a range may be sent: where no "If-Range" is given, or where
        /// it names the file as it is - by its entity tag, or by the time it
        /// was last written (RFC 9110, Section 13.1.5).
        /// </summary>
        private static Boolean IfRangeHolds(String?         IfRange,
                                            String          ETag,
                                            DateTimeOffset  LastModified)
        {

            if (String.IsNullOrWhiteSpace(IfRange))
                return true;

            var text = IfRange.Trim();

            if (text.StartsWith('"') || text.StartsWith("W/", StringComparison.Ordinal))
                return text == ETag;

            return DateTimeOffset.TryParseExact(text, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) &&
                   date.ToUniversalTime() == LastModified;

        }

        #endregion

        #region (private) RemoteOf(Request) / (static) Unmapped(Socket)

        /// <summary>
        /// Who sent the request, as the node says it - behind a trusted proxy,
        /// the client the proxy names - with an IPv4 client that reached a
        /// socket of both families written as the IPv4 address it is.
        /// </summary>
        private String RemoteOf(HTTPRequest Request)

            => Unmapped(ClientOf(Request));

        /// <summary>
        /// A socket as Hermod writes it - "[0000:0000:0000:0000:0000:ffff:c000:020a]:50832" -
        /// with an IPv4 address mapped into IPv6 written as the IPv4 address it
        /// is: "192.0.2.10:50832". Everything else as it is.
        /// </summary>
        public static String Unmapped(String Socket)
        {

            var match = System.Text.RegularExpressions.Regex.Match(Socket, @"^\[([0-9A-Fa-f:.]+)\]:(\d+)(.*)$");

            return match.Success &&
                   System.Net.IPAddress.TryParse(match.Groups[1].Value, out var address) &&
                   address.IsIPv4MappedToIPv6
                       ? $"{address.MapToIPv4()}:{match.Groups[2].Value}{match.Groups[3].Value}"
                       : Socket;

        }

        #endregion

        #region (private) OnlyGet(Request)

        private Task<HTTPResponse> OnlyGet(HTTPRequest Request)

            => Task.FromResult(
                   new HTTPResponse.Builder(Request) {
                       HTTPStatusCode  = HTTPStatusCode.MethodNotAllowed,
                       Allow           = [ HTTPMethod.GET, HTTPMethod.HEAD ],
                       ContentType     = HTTPContentType.Application.JSON_UTF8,
                       Content         = Encoding.UTF8.GetBytes(new JObject(new JProperty("error", "This port serves files by GET /<area>/<file name>, and takes nothing.")).ToString(Formatting.None)),
                       CacheControl    = "no-store"
                   }.AsImmutable
               );

        #endregion

        #region (private static) Answer(Request, Status, Error) / AsciiOf(Name)

        private static HTTPResponse Answer(HTTPRequest     Request,
                                           HTTPStatusCode  Status,
                                           String          Error)

            => new HTTPResponse.Builder(Request) {
                   HTTPStatusCode  = Status,
                   ContentType     = HTTPContentType.Application.JSON_UTF8,
                   Content         = Encoding.UTF8.GetBytes(new JObject(new JProperty("error", Error)).ToString(Formatting.None)),
                   CacheControl    = "no-store"
               }.AsImmutable;

        /// <summary>
        /// A file name as the plain part of a Content-Disposition may carry it.
        /// </summary>
        internal static String AsciiOf(String Name)

            => new (Name.Select(c => c is >= ' ' and < (Char) 127 and not '"' and not '\\' ? c : '_').ToArray());

        #endregion


        #region (private sealed class) FileSlice

        /// <summary>
        /// The bytes of a file a response carries: from where the file stands,
        /// as many as the response says and no more - and the file closed once
        /// they are read, or the response is done with them.
        /// </summary>
        /// <remarks>
        /// It cannot seek, so that the server takes the length the response
        /// says rather than the length of the whole file.
        /// </remarks>
        private sealed class FileSlice(FileStream  File,
                                       Int64       Length) : Stream
        {

            private Int64 left = Length;

            public override Boolean  CanRead   => true;
            public override Boolean  CanSeek   => false;
            public override Boolean  CanWrite  => false;
            public override Int64    Length    => throw new NotSupportedException();

            public override Int64    Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override Int32 Read(Byte[] Buffer, Int32 Offset, Int32 Count)
                => Read(Buffer.AsSpan(Offset, Count));

            public override Int32 Read(Span<Byte> Buffer)
            {

                if (left <= 0)
                {
                    File.Dispose();
                    return 0;
                }

                var read = File.Read(Buffer[..(Int32) Math.Min(Buffer.Length, left)]);

                left -= read;

                if (read == 0)
                    File.Dispose();

                return read;

            }

            public override Task<Int32> ReadAsync(Byte[] Buffer, Int32 Offset, Int32 Count, CancellationToken CancellationToken)
                => ReadAsync(Buffer.AsMemory(Offset, Count), CancellationToken).AsTask();

            public override async ValueTask<Int32> ReadAsync(Memory<Byte> Buffer, CancellationToken CancellationToken = default)
            {

                if (left <= 0)
                {
                    await File.DisposeAsync();
                    return 0;
                }

                var read = await File.ReadAsync(Buffer[..(Int32) Math.Min(Buffer.Length, left)], CancellationToken);

                left -= read;

                if (read == 0)
                    await File.DisposeAsync();

                return read;

            }

            public override void   Flush()                                    { }
            public override Int64  Seek     (Int64 Offset, SeekOrigin Origin) => throw new NotSupportedException();
            public override void   SetLength(Int64 Value)                     => throw new NotSupportedException();
            public override void   Write    (Byte[] Buffer, Int32 Offset, Int32 Count) => throw new NotSupportedException();

            protected override void Dispose(Boolean Disposing)
            {
                if (Disposing)
                    File.Dispose();
                base.Dispose(Disposing);
            }

            public override async ValueTask DisposeAsync()
            {
                await File.DisposeAsync();
                await base.DisposeAsync();
            }

        }

        #endregion

    }

}
