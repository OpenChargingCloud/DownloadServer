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

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.DownloadServer.Tests
{

    /// <summary>
    /// What every node has to answer, asked of a download server - the
    /// conformance suite of WWCP_Node_TestKit - and the download server's
    /// own: files published through the JSON API, fetched by HTTP GET whole
    /// or from a byte on, the record of the downloads, and who may do what.
    /// </summary>
    /// <remarks>
    /// HTTP downloads are served on the loopback address only, on a port of
    /// their own the tests chose, so that the suite's nodes do not fight over
    /// 8365.
    /// </remarks>
    public class DownloadServerConformance : NodeConformanceTests
    {

        protected override WWCPNode NewNode(String   Directory,
                                            JObject  Configuration)
            => Make(Directory, Configuration, null);

        protected override WWCPNode? NewNodeWritingLogFiles(String   Directory,
                                                            JObject  Configuration,
                                                            String   LogPath)
            => Make(Directory, Configuration, LogPath);

        private static DownloadServerNode Make(String   Directory,
                                               JObject  Configuration,
                                               String?  LogPath)
        {

            if (Configuration[DownloadServerSettings.SectionName] is not JObject)
                Configuration[DownloadServerSettings.SectionName] = new JObject(
                                                                        new JProperty("httpListen", "127.0.0.1"),
                                                                        new JProperty("areas",      new JArray("firmware", "packages"))
                                                                    );

            var file = Path.Combine(Directory, WWCPConfigFile.DefaultFileName);
            File.WriteAllText(file, Configuration.ToString());

            var downloadPort = TestPorts.Free();

            return new DownloadServerNode(
                       HTTPPort:          TestPorts.Free(),
                       AccountsPath:      Path.Combine(Directory, "accounts"),
                       ConfigFile:        new WWCPConfigFile(file),
                       CertificatesPath:  Path.Combine(Directory, "certificates"),
                       LogToConsole:      false,
                       LogPath:           LogPath,
                       BridgeDebugLog:    false,
                       CommandLine:       settings => settings with { HTTPPort = downloadPort }
                   );

        }


        #region (private) DownloadServer / DownloadClient(Login, Password) / Published(...) / ADownloader()

        private DownloadServerNode DownloadServer
            => (DownloadServerNode) Node;

        /// <summary>
        /// A client of the download port, signed in with HTTP Basic Auth where
        /// a login is given - as a charging station downloads.
        /// </summary>
        private HttpClient DownloadClient(String?  Login     = null,
                                          String?  Password  = null)
        {

            var http = new HttpClient {
                           BaseAddress  = new Uri($"http://127.0.0.1:{DownloadServer.Settings.HTTPPort}/"),
                           Timeout      = TimeSpan.FromSeconds(20)
                       };

            if (Login is not null && Password is not null)
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                                                                                         Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Login}:{Password}")));

            return http;

        }

        /// <summary>
        /// The given bytes published through the JSON API as the given file.
        /// </summary>
        private static async Task<JObject> Published(HttpClient  HTTP,
                                                     String      Area,
                                                     String      Name,
                                                     Byte[]      Content)
        {

            var response = await HTTP.PutAsync($"api/v1/downloadServer/files/{Area}/{Name}", new ByteArrayContent(Content));
            var text     = await response.Content.ReadAsStringAsync();

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), text);

            return JObject.Parse(text);

        }

        /// <summary>
        /// A downloader made over the JSON API: its name and its password.
        /// </summary>
        private static async Task<String> ADownloader(HttpClient  HTTP,
                                                      String      Id)
        {

            var (made, downloader) = await Send(HTTP, HttpMethod.Post, "api/v1/downloadServer/downloaders", new JObject(new JProperty("id", Id)));

            Assert.That(made, Is.EqualTo(HttpStatusCode.OK), downloader.ToString());

            return downloader["password"]!.Value<String>()!;

        }

        private static async Task<HttpResponseMessage> Get(HttpClient  HTTP,
                                                           String      Path,
                                                           String?     Range    = null,
                                                           String?     IfRange  = null,
                                                           HttpMethod? Method   = null)
        {

            using var request = new HttpRequestMessage(Method ?? HttpMethod.Get, Path);

            if (Range   is not null) request.Headers.TryAddWithoutValidation("Range",    Range);
            if (IfRange is not null) request.Headers.TryAddWithoutValidation("If-Range", IfRange);

            return await HTTP.SendAsync(request);

        }

        #endregion


        #region ADownloaderFetchesAFile_WholeOrFromAByteOn()

        /// <summary>
        /// A file published through the JSON API is fetched by a downloader by
        /// HTTP GET: whole (200), from a byte on (206), the last bytes (206), a
        /// range in the middle (206), nothing beyond its end (416, with its
        /// length), what a HEAD says, and the whole file again where several
        /// ranges are asked for or the If-Range is another file's. Every GET
        /// served is recorded, with the first byte it sent.
        /// </summary>
        [Test]
        public async Task ADownloaderFetchesAFile_WholeOrFromAByteOn()
        {

            using var http         = await SignedIn();

            var image              = RandomNumberGenerator.GetBytes(150_000);
            var published          = await Published(http, "firmware", "station-4.2.1.bin", image);
            var sha256             = Convert.ToHexStringLower(SHA256.HashData(image));

            Assert.That(published["file"]!["sha256"]!.Value<String>(),  Is.EqualTo(sha256));
            Assert.That(published["file"]!["size"]!.  Value<Int64>(),   Is.EqualTo(image.Length));

            var password           = await ADownloader(http, "CS001");

            using var station      = DownloadClient("CS001", password);

            // Whole.
            var whole              = await Get(station, "firmware/station-4.2.1.bin");

            Assert.That(whole.StatusCode,                                 Is.EqualTo(HttpStatusCode.OK), await whole.Content.ReadAsStringAsync());
            Assert.That(await whole.Content.ReadAsByteArrayAsync(),       Is.EqualTo(image));
            Assert.That(whole.Headers.ETag?.Tag,                          Is.EqualTo($"\"{sha256}\""));
            Assert.That(whole.Headers.AcceptRanges.ToString(),            Is.EqualTo("bytes"));

            // From a byte on.
            var rest               = await Get(station, "firmware/station-4.2.1.bin", Range: "bytes=100000-");

            Assert.That(rest.StatusCode,                                  Is.EqualTo(HttpStatusCode.PartialContent));
            Assert.That(rest.Content.Headers.ContentRange?.ToString(),    Is.EqualTo($"bytes 100000-{image.Length - 1}/{image.Length}"));
            Assert.That(await rest.Content.ReadAsByteArrayAsync(),        Is.EqualTo(image[100_000..]));

            // The last bytes, and a range in the middle.
            var last               = await Get(station, "firmware/station-4.2.1.bin", Range: "bytes=-500");
            var middle             = await Get(station, "firmware/station-4.2.1.bin", Range: "bytes=5000-5999");

            Assert.That(await last.  Content.ReadAsByteArrayAsync(),      Is.EqualTo(image[^500..]));
            Assert.That(await middle.Content.ReadAsByteArrayAsync(),      Is.EqualTo(image[5000..6000]));

            // Beyond its end: nothing, and how long it is.
            var beyond             = await Get(station, "firmware/station-4.2.1.bin", Range: $"bytes={image.Length}-");

            Assert.That(beyond.StatusCode,                                Is.EqualTo(HttpStatusCode.RequestedRangeNotSatisfiable));
            Assert.That(beyond.Content.Headers.ContentRange?.ToString(),  Is.EqualTo($"bytes */{image.Length}"));

            // What a GET would say, without the file.
            var head               = await Get(station, "firmware/station-4.2.1.bin", Method: HttpMethod.Head);

            Assert.That(head.StatusCode,                                  Is.EqualTo(HttpStatusCode.OK));
            Assert.That(head.Content.Headers.ContentLength,               Is.EqualTo(image.Length));

            // Several ranges, and a range of another file: the whole file.
            var several            = await Get(station, "firmware/station-4.2.1.bin", Range: "bytes=0-1,5-6");
            var another            = await Get(station, "firmware/station-4.2.1.bin", Range: "bytes=100000-", IfRange: "\"another\"");

            Assert.That(several.StatusCode,                               Is.EqualTo(HttpStatusCode.OK));
            Assert.That(another.StatusCode,                               Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await another.Content.ReadAsByteArrayAsync(),     Is.EqualTo(image));

            // Every GET that sent something is recorded - by whom, of what, from which byte on.
            var downloads          = await GetJSON(http, "api/v1/downloadServer/downloads?order=oldest&downloader=CS001");

            Assert.That(downloads["total"]!.Value<Int32>(),               Is.EqualTo(6));
            Assert.That(downloads["downloads"]!.Select(download => (download["status"]!.Value<Int32>(), download["firstByte"]!.Value<Int64>(), download["bytes"]!.Value<Int64>())),
                        Is.EqualTo(new[] { (200, 0L,       (Int64) image.Length),
                                           (206, 100_000L, (Int64) image.Length - 100_000),
                                           (206, (Int64) image.Length - 500, 500L),
                                           (206, 5_000L,   1_000L),
                                           (200, 0L,       (Int64) image.Length),
                                           (200, 0L,       (Int64) image.Length) }));
            Assert.That(downloads["files"]!.Values<String>(),             Is.EqualTo(new[] { "/firmware/station-4.2.1.bin" }));

            var files              = await GetJSON(http, "api/v1/downloadServer/files?area=firmware");

            Assert.That(files["areas"]!.Values<String>(),                 Is.EqualTo(new[] { "firmware", "packages" }));
            Assert.That(files["files"]![0]!["downloads"]!.Value<Int32>(), Is.EqualTo(6));

        }

        #endregion

        #region ADownloadIsRefused_WithoutCredentials_ToWhoMayNotDownload_AndOfWhatIsNotOffered()

        [Test]
        public async Task ADownloadIsRefused_WithoutCredentials_ToWhoMayNotDownload_AndOfWhatIsNotOffered()
        {

            using var http            = await SignedIn();

            await Published(http, "firmware", "station-4.2.1.bin", [ 1, 2, 3 ]);

            File.WriteAllBytes(Path.Combine(DownloadServer.DirectoryOf("firmware"), ".hidden.bin"), [ 4, 5, 6 ]);
            File.WriteAllBytes(Path.Combine(DownloadServer.FilesDirectory, "secret.bin"),            [ 7, 8, 9 ]);

            // Nobody.
            using var nobody          = DownloadClient();

            var anonymous             = await Get(nobody, "firmware/station-4.2.1.bin");

            Assert.That(anonymous.StatusCode,                              Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(anonymous.Headers.WwwAuthenticate.ToString(),      Does.StartWith("Basic"));

            // Somebody who may not download.
            var operatorPassword      = "An-Operator-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

            await AccountIn(User_Id.Parse("operator1"), DownloadServerAccess.Operator.Name, operatorPassword);

            using var operatorClient  = DownloadClient("operator1", operatorPassword);

            Assert.That((await Get(operatorClient, "firmware/station-4.2.1.bin")).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

            // A downloader, asking for what is not offered.
            var password              = await ADownloader(http, "CS001");

            using var station         = DownloadClient("CS001", password);

            foreach (var path in new[] { "firmware/missing.bin", "elsewhere/station-4.2.1.bin", "firmware/.hidden.bin", "secret.bin",
                                         "firmware/sub/station-4.2.1.bin", "firmware/" })
                Assert.That((await Get(station, path)).StatusCode, Is.EqualTo(HttpStatusCode.NotFound), path);

            // A way out of the area, spelled so that no client takes it apart:
            // refused by the HTTP server already, or not found - never served.
            Assert.That((await Get(station, "firmware/..%2F..%2Fsecret.bin")).StatusCode, Is.AnyOf(HttpStatusCode.BadRequest, HttpStatusCode.NotFound));

            // And nothing is taken on this port.
            var put                   = await station.PutAsync("firmware/new.bin", new ByteArrayContent([ 1 ]));

            Assert.That(put.StatusCode,                                    Is.EqualTo(HttpStatusCode.MethodNotAllowed));
            Assert.That(DownloadServer.Files("firmware").Select(file => file.Name), Is.EqualTo(new[] { "station-4.2.1.bin" }));
            Assert.That(DownloadServer.Downloads.Count,                    Is.Zero, "nothing refused is recorded as a download");
            Assert.That(DownloadServer.HTTPDownloadsRefused,               Is.GreaterThanOrEqualTo(8));

        }

        #endregion

        #region APublisherPublishesAndTakesAway_ButSeesNoDownloads()

        /// <summary>
        /// A publisher - a build pipeline - puts a file in its area, in the
        /// place of the one of that name, and takes it away; it may not see
        /// who downloaded what, and may not make downloaders. A downloader may
        /// not publish.
        /// </summary>
        [Test]
        public async Task APublisherPublishesAndTakesAway_ButSeesNoDownloads()
        {

            var password              = "A-Publisher-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

            await AccountIn(User_Id.Parse("pipeline"), DownloadServerAccess.Publisher.Name, password);

            using var pipeline        = await SignedInAs("pipeline", password);

            await Published(pipeline, "firmware", "station.bin", [ 1, 2, 3 ]);
            var replaced              = await Published(pipeline, "firmware", "station.bin", [ 4, 5, 6, 7 ]);

            Assert.That(replaced["file"]!["size"]!.Value<Int64>(),         Is.EqualTo(4));
            Assert.That(File.ReadAllBytes(Path.Combine(DownloadServer.DirectoryOf("firmware"), "station.bin")), Is.EqualTo(new Byte[] { 4, 5, 6, 7 }));
            Assert.That(System.IO.Directory.GetFiles(DownloadServer.DirectoryOf("firmware")), Has.Length.EqualTo(1), "nothing of a publication is left beside it");

            Assert.That((await pipeline.GetAsync("api/v1/downloadServer/downloads")).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

            var (madeOne, _)          = await Send(pipeline, HttpMethod.Post, "api/v1/downloadServer/downloaders", new JObject(new JProperty("id", "CS001")));

            Assert.That(madeOne,                                           Is.EqualTo(HttpStatusCode.Forbidden));

            // What cannot be published.
            Assert.That((await pipeline.PutAsync("api/v1/downloadServer/files/elsewhere/station.bin", new ByteArrayContent([ 1 ]))).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await pipeline.PutAsync("api/v1/downloadServer/files/firmware/.hidden",      new ByteArrayContent([ 1 ]))).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That((await pipeline.PutAsync("api/v1/downloadServer/files/firmware/empty.bin",    new ByteArrayContent([   ]))).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

            var (removed, said)       = await Send(pipeline, HttpMethod.Post, "api/v1/downloadServer/files/firmware/station.bin/remove");

            Assert.That(removed,                                           Is.EqualTo(HttpStatusCode.OK), said.ToString());
            Assert.That(DownloadServer.Files(null),                        Is.Empty);

            // A downloader may download, and nothing else.
            using var admin           = await SignedIn();
            var downloaderPassword    = await ADownloader(admin, "CS001");

            using var station         = await SignedInAs("CS001", downloaderPassword);

            Assert.That((await station.PutAsync("api/v1/downloadServer/files/firmware/station.bin", new ByteArrayContent([ 1 ]))).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That((await station.GetAsync("api/v1/downloadServer/files")).StatusCode,                                                   Is.EqualTo(HttpStatusCode.Forbidden));

        }

        #endregion

        #region ADownloaderGivenANewPasswordOrTakenAway_MayNoLongerDownloadWithTheOldOne()

        [Test]
        public async Task ADownloaderGivenANewPasswordOrTakenAway_MayNoLongerDownloadWithTheOldOne()
        {

            using var http            = await SignedIn();

            await Published(http, "firmware", "a.bin", [ 1, 2, 3 ]);

            var first                 = await ADownloader(http, "CS001");

            var (changed, renewed)    = await Send(http, HttpMethod.Post, "api/v1/downloadServer/downloaders/password", new JObject(new JProperty("id", "CS001")));

            Assert.That(changed,                                           Is.EqualTo(HttpStatusCode.OK), renewed.ToString());

            var second                = renewed["password"]!.Value<String>()!;

            using (var old = DownloadClient("CS001", first))
                Assert.That((await Get(old, "firmware/a.bin")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

            using (var now = DownloadClient("CS001", second))
                Assert.That((await Get(now, "firmware/a.bin")).StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var (removed, after)      = await Send(http, HttpMethod.Post, "api/v1/downloadServer/downloaders/remove", new JObject(new JProperty("id", "CS001")));

            Assert.That(removed,                                           Is.EqualTo(HttpStatusCode.OK), after.ToString());
            Assert.That(after["downloaders"]!.Count(),                     Is.Zero);

            using (var gone = DownloadClient("CS001", second))
                Assert.That((await Get(gone, "firmware/a.bin")).StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

            Assert.That(DownloadServer.Downloads.Count,                    Is.EqualTo(1), "what it downloaded stays recorded");

        }

        #endregion

    }

}
