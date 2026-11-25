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
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Newtonsoft.Json.Linq;

using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.DownloadServer.Tests
{

    /// <summary>
    /// The download port as a port of its own: a file sent from the disk as
    /// the client reads it and never held in memory, for as long as the client
    /// takes; a certificate of its own and not the web interface's; and the
    /// URL charging stations are told.
    /// </summary>
    public class DownloadPortTests
    {

        #region Data

        private String               directory  = "";
        private DownloadServerNode?  node;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void Setup()
        {
            directory = Path.Combine(Path.GetTempPath(), "downloadserver-port-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public async Task TearDown()
        {

            if (node is not null)
            {
                await node.Stop();
                await node.DisposeAsync();
                node = null;
            }

            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (Exception)
            { }

        }

        #endregion


        #region (private) Started(Section) / Downloader(Node, Id, Scheme, Shown) / Offered(Node, Name, Size)

        /// <summary>
        /// A download server of the given "downloadServer" section - on the
        /// loopback address, its download port one of the tests' - and no NTS
        /// and SSH, which these tests do not ask.
        /// </summary>
        private async Task<DownloadServerNode> Started(JObject? Section = null)
        {

            var section = Section ?? new JObject();

            node = await TestPorts.StartedOnFreshPorts(() => {

                       section["httpListen"] = "127.0.0.1";
                       section["httpPort"]   = TestPorts.Free().ToUInt16();

                       File.WriteAllText(Path.Combine(directory, WWCPConfigFile.DefaultFileName),
                                         new JObject(
                                             new JProperty("nts",  JObject.Parse("""{ "groups": { "legal": { "enabled": false } } }""")),
                                             new JProperty("ssh",  JObject.Parse("""{ "enabled": false }""")),
                                             new JProperty(DownloadServerSettings.SectionName, section)
                                         ).ToString());

                       return new DownloadServerNode(HTTPPort:          TestPorts.Free(),
                                                     AccountsPath:      Path.Combine(directory, "accounts"),
                                                     ConfigFile:        new WWCPConfigFile(Path.Combine(directory, WWCPConfigFile.DefaultFileName)),
                                                     CertificatesPath:  Path.Combine(directory, "certificates"),
                                                     LogToConsole:      false,
                                                     BridgeDebugLog:    false);

                   });

            return node;

        }

        /// <summary>
        /// A client of the download port signed in as a new downloader.
        /// </summary>
        private static async Task<HttpClient> Downloader(DownloadServerNode         Node,
                                                         String                     Id,
                                                         String                     Scheme  = "http",
                                                         Action<X509Certificate?>?  Shown   = null)
        {

            var made = await Node.AddDownloader(Id, null, "test");

            Assert.That(made.Outcome, Is.EqualTo(DownloaderChangeOutcome.Changed), made.Said);

            var handler = new SocketsHttpHandler();

            if (Scheme == "https")
                handler.SslOptions.RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => {
                    Shown?.Invoke(certificate);
                    return true;
                };

            var http = new HttpClient(handler) {
                           BaseAddress  = new Uri($"{Scheme}://127.0.0.1:{Node.Settings.HTTPPort}/"),
                           Timeout      = TimeSpan.FromMinutes(2)
                       };

            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                                                                                     Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Id}:{made.Password}")));

            return http;

        }

        /// <summary>
        /// A file of the given size put into the area "firmware" by hand, made
        /// up as it is written and never held whole; its SHA-256.
        /// </summary>
        private static String Offered(DownloadServerNode Node, String Name, Int64 Size)
        {

            using var hash    = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var file    = File.Create(Path.Combine(Node.DirectoryOf("firmware"), Name));
            var       buffer  = new Byte[64 * 1024];

            for (var written = 0L; written < Size; )
            {

                var count = (Int32) Math.Min(buffer.Length, Size - written);

                for (var i = 0; i < count; i++)
                    buffer[i] = (Byte) ((written + i) * 31 % 251);

                file.Write(buffer, 0, count);
                hash.AppendData(buffer, 0, count);
                written += count;

            }

            return Convert.ToHexStringLower(hash.GetHashAndReset());

        }

        /// <summary>
        /// What a body reads to, hashed as it is read and never held whole -
        /// with a pause after the given number of bytes, where one is asked,
        /// and what is to be looked at at its end.
        /// </summary>
        private static async Task<(Int64 Bytes, String SHA256)> ReadOut(Stream     Body,
                                                                        Int64      PauseAfter  = -1,
                                                                        TimeSpan?  Pause       = null,
                                                                        Action?    AfterPause  = null)
        {

            using var hash    = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var       buffer  = new Byte[64 * 1024];
            var       bytes   = 0L;
            var       paused  = false;

            while (true)
            {

                var room  = PauseAfter >= 0 && !paused
                                ? (Int32) Math.Min(buffer.Length, PauseAfter - bytes)
                                : buffer.Length;

                if (room == 0)
                {
                    paused = true;
                    await Task.Delay(Pause ?? TimeSpan.Zero);
                    AfterPause?.Invoke();
                    continue;
                }

                var read  = await Body.ReadAsync(buffer.AsMemory(0, room));

                if (read == 0)
                    break;

                hash.AppendData(buffer, 0, read);
                bytes += read;

            }

            return (bytes, Convert.ToHexStringLower(hash.GetHashAndReset()));

        }

        /// <summary>
        /// Wait until the download server recorded so many downloads: it does
        /// once the last byte of one is read.
        /// </summary>
        private static async Task Recorded(DownloadServerNode Node, Int32 Count)
        {

            for (var waited = 0; Node.Downloads.Count < Count && waited < 200; waited++)
                await Task.Delay(50);

            Assert.That(Node.Downloads.Count, Is.EqualTo(Count), "the downloads were recorded");

        }

        #endregion


        #region ALargeDownload_IsSentFromTheDisk_AndNeverHeldInMemory()

        /// <summary>
        /// 64 MiB fetched by HTTP GET - whole, and from byte 40 MiB on - arrive
        /// as they lie on the disk, and the download server took far less
        /// memory for them than they are large: a file read whole into memory
        /// would have taken all of it.
        /// </summary>
        [Test]
        public async Task ALargeDownload_IsSentFromTheDisk_AndNeverHeldInMemory()
        {

            const Int64 size   = 64L * 1024 * 1024;
            const Int64 from   = 40L * 1024 * 1024;

            var downloadServer = await Started();
            var sha256         = Offered(downloadServer, "large.bin", size);
            using var http     = await Downloader(downloadServer, "CS001");

            // The file's SHA-256 is its entity tag, worked out once: not part
            // of what a download costs.
            Assert.That(downloadServer.TryGetFile("firmware", "large.bin", out var offered, out _), Is.True);
            Assert.That(offered!.SHA256, Is.EqualTo(sha256));

            GC.Collect();

            var before         = GC.GetTotalAllocatedBytes(precise: true);

            using (var response = await http.GetAsync("firmware/large.bin", HttpCompletionOption.ResponseHeadersRead))
            {

                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

                var (bytes, hash) = await ReadOut(await response.Content.ReadAsStreamAsync());

                Assert.Multiple(() => {
                    Assert.That(bytes, Is.EqualTo(size));
                    Assert.That(hash,  Is.EqualTo(sha256));
                });

            }

            var allocated      = GC.GetTotalAllocatedBytes(precise: true) - before;

            Assert.That(allocated, Is.LessThan(size / 4),
                        $"{allocated / 1024 / 1024} MiB were allocated for a download of {size / 1024 / 1024} MiB.");

            var rangeRequest   = new HttpRequestMessage(HttpMethod.Get, "firmware/large.bin");
            rangeRequest.Headers.Range = new RangeHeaderValue(from, null);

            before             = GC.GetTotalAllocatedBytes(precise: true);

            using (var response = await http.SendAsync(rangeRequest, HttpCompletionOption.ResponseHeadersRead))
            {

                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));

                var (bytes, _) = await ReadOut(await response.Content.ReadAsStreamAsync());

                Assert.That(bytes, Is.EqualTo(size - from));

            }

            allocated          = GC.GetTotalAllocatedBytes(precise: true) - before;

            Assert.That(allocated, Is.LessThan((size - from) / 4),
                        $"{allocated / 1024 / 1024} MiB were allocated for a range of {(size - from) / 1024 / 1024} MiB.");

            await Recorded(downloadServer, 2);

            Assert.That(downloadServer.Downloads.All.Select(download => (download.FirstByte, download.Bytes)),
                        Is.EqualTo(new[] { (0L, size), (from, size - from) }));

        }

        #endregion

        #region ADownloadThatStopsLongerThanHermodsHalfMinute_IsServedToItsEnd()

        /// <summary>
        /// A charging station that stops reading for longer than the half
        /// minute Hermod's server waits for a socket by default: the download
        /// goes on where it stopped once it reads again, to the last byte -
        /// there is no time set for the whole of a response, nor for one write
        /// of it, that a slow line would run into.
        /// </summary>
        [Test]
        public async Task ADownloadThatStopsLongerThanHermodsHalfMinute_IsServedToItsEnd()
        {

            // More than the socket buffers on both ends hold, so that the
            // server is left waiting in the middle of a write.
            const Int64 size   = 32L * 1024 * 1024;

            var downloadServer = await Started();
            var sha256         = Offered(downloadServer, "slow.bin", size);
            using var http     = await Downloader(downloadServer, "CS001");

            using var response = await http.GetAsync("firmware/slow.bin", HttpCompletionOption.ResponseHeadersRead);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var (bytes, hash)  = await ReadOut(await response.Content.ReadAsStreamAsync(),
                                               PauseAfter:  1024 * 1024,
                                               Pause:       TimeSpan.FromSeconds(35),
                                               // Recorded once the last byte is read from the disk: not
                                               // yet, so the server was in the middle of sending it.
                                               AfterPause:  () => Assert.That(downloadServer.Downloads.Count, Is.Zero,
                                                                              "the server was left waiting with the file not yet sent"));

            Assert.Multiple(() => {
                Assert.That(bytes, Is.EqualTo(size));
                Assert.That(hash,  Is.EqualTo(sha256));
            });

            await Recorded(downloadServer, 1);

            Assert.That(downloadServer.Downloads.All.Single().ToTheEnd, Is.True);

        }

        #endregion

        #region TheDownloadPortShowsACertificateOfItsOwn_WhileTheWebInterfaceSpeaksPlainHTTP()

        /// <summary>
        /// "tls" of the section, with a PEM file: the download port shows that
        /// certificate, a download over HTTPS arrives whole, the URL charging
        /// stations are told says "https" - and the web interface, which was
        /// given no certificate, still speaks plain HTTP.
        /// </summary>
        [Test]
        public async Task TheDownloadPortShowsACertificateOfItsOwn_WhileTheWebInterfaceSpeaksPlainHTTP()
        {

            using var key          = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request            = new CertificateRequest("CN=downloads.test", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ new Oid("1.3.6.1.5.5.7.3.1") ], false));
            var subjectNames       = new SubjectAlternativeNameBuilder();
            subjectNames.AddDnsName("downloads.test");
            subjectNames.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(subjectNames.Build());

            using var certificate  = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

            File.WriteAllText(Path.Combine(directory, "download.pem"),
                              certificate.ExportCertificatePem() + "\n" + key.ExportPkcs8PrivateKeyPem() + "\n");

            var downloadServer     = await Started(new JObject(
                                                       new JProperty("tls", new JObject(
                                                           new JProperty("certificate", new JObject(
                                                               new JProperty("fullchain", "download.pem")
                                                           ))
                                                       ))
                                                   ));

            var sha256             = Offered(downloadServer, "station-4.2.1.bin", 100 * 1024);

            X509Certificate? shown = null;

            using var http         = await Downloader(downloadServer, "CS001", "https", certificate => shown = certificate);

            var response           = await http.GetAsync("firmware/station-4.2.1.bin");

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var status             = downloadServer.StatusJSON()["http"]!;
            var body               = await response.Content.ReadAsByteArrayAsync();

            Assert.Multiple(() => {
                Assert.That(Convert.ToHexStringLower(SHA256.HashData(body)),   Is.EqualTo(sha256));
                Assert.That(shown?.GetCertHashString(),                        Is.EqualTo(certificate.GetCertHashString()));
                Assert.That(downloadServer.DownloadsOverHTTPS,                 Is.True);
                Assert.That(downloadServer.DownloadURL.Scheme,                 Is.EqualTo("https"));
                Assert.That(status["tls"]!.Value<Boolean>(),                   Is.True);
                Assert.That(status["certificate"]!["shows"]!.Value<String>(),  Is.EqualTo("CN=downloads.test"));
                Assert.That(downloadServer.WebCertificate,                     Is.Null, "the web interface was given no certificate");
            });

            using var web          = new HttpClient();
            var page               = await web.GetAsync($"http://127.0.0.1:{downloadServer.HTTPPort}/");

            Assert.That(page.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the web interface speaks plain HTTP still");

        }

        #endregion

        #region WithNothingInTheStoreForTheDownloadPort_AConnectionIsRefused_AndWhySaid()

        /// <summary>
        /// "tls": { "certificate": "store" } with nothing in the store for the
        /// listener "download": a connection is refused rather than served in
        /// the clear, and the status says why.
        /// </summary>
        [Test]
        public async Task WithNothingInTheStoreForTheDownloadPort_AConnectionIsRefused_AndWhySaid()
        {

            var downloadServer  = await Started(JObject.Parse("""{ "tls": { "certificate": "store" } }"""));

            Offered(downloadServer, "station-4.2.1.bin", 1024);

            using var https     = await Downloader(downloadServer, "CS001", "https");

            await Assert.ThrowsAsync<HttpRequestException>(() => https.GetAsync("firmware/station-4.2.1.bin"));

            using var plain     = new HttpClient();

            await Assert.ThrowsAsync<HttpRequestException>(() => plain.GetAsync($"http://127.0.0.1:{downloadServer.Settings.HTTPPort}/firmware/station-4.2.1.bin"),
                                                     "and not served in the clear either");

            var certificate     = downloadServer.StatusJSON()["http"]!["certificate"]!;

            Assert.Multiple(() => {
                Assert.That(certificate["certificate"]!.Value<String>(),  Is.EqualTo("store"));
                Assert.That(certificate["shows"]!.Type,                   Is.EqualTo(JTokenType.Null));
                Assert.That(certificate["problem"]!.Value<String>(),      Does.Contain("'download'"));
                Assert.That(downloadServer.Downloads.Count,               Is.Zero);
            });

        }

        #endregion

        #region TheURLChargingStationsAreTold_IsTheSections_OrGuessed()

        [Test]
        public async Task TheURLChargingStationsAreTold_IsTheSections_OrGuessed()
        {

            var guessed = await Started();

            Assert.Multiple(() => {
                Assert.That(guessed.DownloadURL.ToString(),                        Is.EqualTo($"http://127.0.0.1:{guessed.Settings.HTTPPort}/"));
                Assert.That(guessed.StatusJSON()["http"]!["publicURL"]!.Type,      Is.EqualTo(JTokenType.Null));
                Assert.That(guessed.StatusJSON()["http"]!["listen"]!.Value<String>(), Is.EqualTo($"http://127.0.0.1:{guessed.Settings.HTTPPort}/"));
                Assert.That(guessed.ListenersText(),                               Does.Not.Contain("listening on"));
            });

            await guessed.Stop();
            await guessed.DisposeAsync();
            node = null;

            var told = await Started(new JObject(new JProperty("publicURL", "https://downloads.example.org/charging")));

            Assert.Multiple(() => {
                Assert.That(told.DownloadURL.ToString(),                           Is.EqualTo("https://downloads.example.org/charging/"));
                Assert.That(told.StatusJSON()["http"]!["url"]!.Value<String>(),    Is.EqualTo("https://downloads.example.org/charging/"));
                Assert.That(told.StatusJSON()["http"]!["publicURL"]!.Value<String>(), Is.EqualTo("https://downloads.example.org/charging/"));
                Assert.That(told.ListenersText(),                                  Does.Contain("https://downloads.example.org/charging/<area>/<file name>"));
                Assert.That(told.ListenersText(),                                  Does.Contain($"listening on http://127.0.0.1:{told.Settings.HTTPPort}/"));
                Assert.That(told.Log.Recent(200).Select(entry => entry.Message),   Has.None.Contains("plain HTTP"),
                            "a proxy that speaks TLS is in front where the URL says \"https\"");
            });

        }

        #endregion

        #region TheNewKeysOfTheSection_AreReadAndWhatIsWrongInThemIsSaid()

        [Test]
        public void TheNewKeysOfTheSection_AreReadAndWhatIsWrongInThemIsSaid()
        {

            Assert.That(DownloadServerSettings.TryParse(null, out var defaults, out _), Is.True);

            Assert.Multiple(() => {
                Assert.That(defaults!.TLS,        Is.Null);
                Assert.That(defaults.PublicURL,   Is.Null);
            });

            Assert.That(DownloadServerSettings.TryParse(JObject.Parse("""{ "tls": { "certificate": "store" }, "publicURL": "http://downloads.example.org:8365" }"""),
                                                        out var given, out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(given!.TLS?.Certificate?.IsStore,                          Is.True);
                Assert.That(given.PublicURL?.ToString(),                               Is.EqualTo("http://downloads.example.org:8365/"));
                Assert.That(given.ToJSON()["tls"]!["certificate"]!.Value<String>(),    Is.EqualTo("store"));
                Assert.That(given.ToJSON()["publicURL"]!.Value<String>(),              Is.EqualTo("http://downloads.example.org:8365/"));
            });

            Assert.That(DownloadServerSettings.TryParse(JObject.Parse("""{ "tls": { "certificate": { "fullchain": "cert.pem", "privateKey": "key.pem" } } }"""),
                                                        out var files, out error), Is.True, error);
            Assert.That(files!.TLS?.Certificate?.FullChain, Is.EqualTo("cert.pem"));

            Assert.That(DownloadServerSettings.TryParse(JObject.Parse("""{ "tls": { "certificate": "store", "redirectPort": 80 } }"""), out _, out error), Is.False);
            Assert.That(error, Does.Contain("redirectPort"));

            Assert.That(DownloadServerSettings.TryParse(JObject.Parse("""{ "tls": "store" }"""),                                        out _, out error), Is.False);
            Assert.That(error, Does.Contain("downloadServer.tls"));

            Assert.That(DownloadServerSettings.TryParse(JObject.Parse("""{ "tls": { "certificate": "elsewhere" } }"""),                 out _, out error), Is.False);
            Assert.That(error, Does.Contain("downloadServer.tls.certificate"));

            Assert.That(DownloadServerSettings.TryParse(JObject.Parse("""{ "publicURL": "ftp://downloads.example.org/" }"""),           out _, out error), Is.False);
            Assert.That(error, Does.Contain("publicURL"));

            Assert.That(DownloadServerSettings.TryParse(JObject.Parse("""{ "publicURL": "https://CS001:secret@downloads.example.org/" }"""), out _, out error), Is.False);
            Assert.That(error, Does.Contain("credentials"));

            Assert.That(DownloadServerSettings.TryParse(JObject.Parse("""{ "publicURL": "https://downloads.example.org/?area=firmware" }"""), out _, out error), Is.False);
            Assert.That(error, Does.Contain("query"));

            Assert.That(DownloadServerSettings.TryParse(JObject.Parse("""{ "publicURL": 8365 }"""),                                      out _, out error), Is.False);
            Assert.That(error, Does.Contain("publicURL"));

        }

        #endregion

    }

}
