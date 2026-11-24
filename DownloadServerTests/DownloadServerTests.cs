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
using System.Security.Cryptography;

using Newtonsoft.Json.Linq;

using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.DownloadServer.Tests
{

    /// <summary>
    /// What a download server is made of, asked without the conformance suite
    /// around it: the "downloadServer" section, the ranges a GET may ask for,
    /// the record of the downloads, and a download server of a section of the
    /// test's own.
    /// </summary>
    public class DownloadServerTests
    {

        #region Data

        private String               directory  = "";
        private DownloadServerNode?  node;

        #endregion

        #region Setup / TearDown

        [SetUp]
        public void Setup()
        {
            directory = Path.Combine(Path.GetTempPath(), "downloadserver-" + Guid.NewGuid().ToString("N")[..12]);
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

        #region (private) Started(Section)

        /// <summary>
        /// A download server of the given "downloadServer" section, on the
        /// loopback address and ports of its own.
        /// </summary>
        private async Task<DownloadServerNode> Started(JObject Section)
        {

            node = await TestPorts.StartedOnFreshPorts(() => {

                       Section["httpListen"] = "127.0.0.1";
                       Section["httpPort"]   = TestPorts.Free().ToUInt16();

                       File.WriteAllText(Path.Combine(directory, WWCPConfigFile.DefaultFileName),
                                         new JObject(
                                             new JProperty("nts",  new JObject(new JProperty("groups", new JObject(new JProperty("legal", new JObject(new JProperty("enabled", false))))))),
                                             new JProperty("ssh",  new JObject(new JProperty("enabled", false))),
                                             new JProperty(DownloadServerSettings.SectionName, Section)
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

        #endregion


        #region TheSection_IsReadWithItsDefaults_AndWhatIsWrongIsSaid()

        [Test]
        public void TheSection_IsReadWithItsDefaults_AndWhatIsWrongIsSaid()
        {

            Assert.That(DownloadServerSettings.TryParse(null, out var defaults, out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(defaults!.Directory,          Is.EqualTo("files"));
                Assert.That(defaults.Areas,               Is.EqualTo(new[] { "firmware" }));
                Assert.That(defaults.HTTPPort.ToUInt16(), Is.EqualTo(8365));
                Assert.That(defaults.AnonymousHTTP,       Is.False);
                Assert.That(defaults.SFTP,                Is.True);
            });

            Assert.That(DownloadServerSettings.TryParse(JObject.Parse("""{ "areas": [ "firmware", "packages" ], "anonymousHTTP": true }"""), out var read, out error), Is.True, error);
            Assert.That(read!.Areas,          Is.EqualTo(new[] { "firmware", "packages" }));
            Assert.That(read.AnonymousHTTP,   Is.True);

            foreach (var wrong in new[] { """{ "areas": "firmware" }""",
                                          """{ "areas": [ "Firmware" ] }""",
                                          """{ "areas": [ "firm/ware" ] }""",
                                          """{ "areas": [ "firmware", "firmware" ] }""",
                                          """{ "areas": [ "" ] }""",
                                          """{ "areas": [ "logs" ] }""",
                                          """{ "httpPort": 0 }""",
                                          """{ "maxFileSize": 0 }""",
                                          """{ "sftp": "yes" }""" })
                Assert.That(DownloadServerSettings.TryParse(JObject.Parse(wrong), out _, out error), Is.False, wrong);

        }

        #endregion

        #region ARange_IsReadAsRFC9110Says()

        [TestCase("bytes=0-",          1000, 0,   999,  false)]
        [TestCase("bytes=100-",        1000, 100, 999,  false)]
        [TestCase("bytes=100-199",     1000, 100, 199,  false)]
        [TestCase("bytes=100-5000",    1000, 100, 999,  false)]
        [TestCase("bytes=-100",        1000, 900, 999,  false)]
        [TestCase("bytes=-5000",       1000, 0,   999,  false)]
        [TestCase("BYTES=10-20",       1000, 10,  20,   false)]
        [TestCase("bytes=1000-",       1000, -1,  -1,   true)]
        [TestCase("bytes=-0",          1000, -1,  -1,   true)]
        [TestCase("bytes=0-",          0,    -1,  -1,   true)]
        public void ARange_IsReadAsRFC9110Says(String Range, Int64 Size, Int64 First, Int64 Last, Boolean Unsatisfiable)
        {

            Assert.That(DownloadServerNode.TryParseRange(Range, Size, out var range, out var unsatisfiable), Is.True);
            Assert.That(unsatisfiable, Is.EqualTo(Unsatisfiable));

            if (!Unsatisfiable)
                Assert.That(range, Is.EqualTo(new ByteRange(First, Last)));

        }

        [TestCase("items=0-10")]
        [TestCase("bytes=0-1,5-6")]
        [TestCase("bytes=20-10")]
        [TestCase("bytes=abc-")]
        [TestCase("bytes=")]
        [TestCase("bytes=-")]
        public void ARangeThatIsNoneToServe_IsPassedOver(String Range)

            => Assert.That(DownloadServerNode.TryParseRange(Range, 1000, out _, out _), Is.False);

        #endregion

        #region TheRecord_IsReadBack_AndALineThatIsNoDownloadIsPassedOver()

        [Test]
        public void TheRecord_IsReadBack_AndALineThatIsNoDownloadIsPassedOver()
        {

            var said   = new List<String>();
            var store  = new DownloadStore(directory, Warn: said.Add);

            Assert.That(store.Load(), Is.Zero);

            store.Record(DownloadProtocol.HTTP, "CS001", "firmware", "a.bin", 200, 0,     1000, 1000, "127.0.0.1:5000", "WWCP-Node/1.0");
            store.Record(DownloadProtocol.SFTP, "CS002", "firmware", "a.bin", 206, 400,   600,  1000, "127.0.0.1:5001");
            store.Record(DownloadProtocol.HTTP, "CS001", "firmware", "b.bin", 206, 10,    5,    1000, "127.0.0.1:5002");

            File.AppendAllText(store.RecordFile, "no download\n");

            var again  = new DownloadStore(directory, Warn: said.Add);

            Assert.That(again.Load(),                                               Is.EqualTo(3));
            Assert.That(said,                                                       Has.Exactly(1).Contains("Line 4"));
            Assert.That(again.All.Select(download => download.ToTheEnd),            Is.EqualTo(new[] { true, true, false }));
            Assert.That(again.Query(new DownloadQuery(Downloader: "cs001"), out var total).Select(download => download.FileName),
                                                                                    Is.EqualTo(new[] { "b.bin", "a.bin" }));
            Assert.That(total,                                                      Is.EqualTo(2));
            Assert.That(again.Files.Select(file => (file.Path, file.Downloads)),    Is.EqualTo(new[] { ("/firmware/a.bin", 2), ("/firmware/b.bin", 1) }));

            // Numbers go on from the last one read.
            Assert.That(again.Record(DownloadProtocol.HTTP, "CS003", "firmware", "a.bin", 200, 0, 1, 1, "x").Id, Is.EqualTo(4UL));

        }

        #endregion

        #region AFilePutInItsAreaByHand_IsOffered_AndWithAnonymousHTTPFetchedWithoutCredentials()

        /// <summary>
        /// The disk is what is offered: a file put in an area's directory by
        /// hand is fetched at once. Where the section says "anonymousHTTP":
        /// true, without credentials - written down as "anonymous".
        /// </summary>
        [Test]
        public async Task AFilePutInItsAreaByHand_IsOffered_AndWithAnonymousHTTPFetchedWithoutCredentials()
        {

            var server   = await Started(new JObject(new JProperty("anonymousHTTP", true)));

            var image    = RandomNumberGenerator.GetBytes(4000);

            File.WriteAllBytes(Path.Combine(server.DirectoryOf("firmware"), "image.bin"), image);

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Settings.HTTPPort}/") };

            var response = await http.GetAsync("firmware/image.bin");

            Assert.That(response.StatusCode,                         Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(image));
            Assert.That(server.Downloads.Latest!.Downloader,         Is.EqualTo(DownloadServerNode.AnonymousDownloader));

            // Changed on the disk: the new one, with a new tag.
            var tagBefore = response.Headers.ETag?.Tag;

            File.WriteAllBytes(Path.Combine(server.DirectoryOf("firmware"), "image.bin"), [ 1, 2, 3 ]);
            File.SetLastWriteTimeUtc(Path.Combine(server.DirectoryOf("firmware"), "image.bin"), DateTime.UtcNow.AddMinutes(1));

            var changed  = await http.GetAsync("firmware/image.bin");

            Assert.That(await changed.Content.ReadAsByteArrayAsync(), Is.EqualTo(new Byte[] { 1, 2, 3 }));
            Assert.That(changed.Headers.ETag?.Tag,                    Is.Not.EqualTo(tagBefore));

        }

        #endregion

        #region WhatAPublicationThatNeverFinishedLeft_IsTakenAwayAtTheStart()

        [Test]
        public async Task WhatAPublicationThatNeverFinishedLeft_IsTakenAwayAtTheStart()
        {

            var firmware = Path.Combine(directory, "files", "firmware");

            Directory.CreateDirectory(firmware);
            File.WriteAllBytes(Path.Combine(firmware, DownloadServerNode.PublishPrefix + "0123"), [ 1 ]);
            File.WriteAllBytes(Path.Combine(firmware, "kept.bin"),                                 [ 2 ]);

            var server = await Started(new JObject());

            Assert.That(Directory.GetFiles(firmware).Select(Path.GetFileName), Is.EqualTo(new[] { "kept.bin" }));
            Assert.That(server.Files(null).Select(file => file.Path),           Is.EqualTo(new[] { "/firmware/kept.bin" }));

        }

        #endregion

    }

}
