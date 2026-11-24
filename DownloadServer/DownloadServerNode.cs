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

using System.Net.Sockets;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Norn.NTS;

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Web;
using cloud.charging.open.protocols.WWCP.Node.Logging;
using cloud.charging.open.protocols.WWCP.Node.Certificates;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.SecureShell;

using cloud.charging.open.DownloadServer.CommandLine;

#endregion

namespace cloud.charging.open.DownloadServer
{

    /// <summary>
    /// One download server: a WWCP node that offers files - firmware images
    /// first of all, and whatever else charging stations and local
    /// controllers are pointed to - by HTTP GET on a port of its own, and over
    /// SFTP on its SSH server; records who fetched what, and shows both in its
    /// web interface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What is this kind's own is the files: the areas they lie in - one
    /// directory each, "firmware" unless the configuration names others -,
    /// the port that serves them by HTTP, with ranges, so that a download
    /// that stopped is taken up where it stopped, the same areas in the SFTP
    /// tree, the record of the downloads (see <see cref="DownloadStore"/>), and
    /// the accounts they are fetched under - charging stations, local
    /// controllers - in the "downloader" role. Files are published over SFTP,
    /// through the web interface, or by putting them in an area's directory.
    /// </para>
    /// <para>
    /// What every program of this family has - the sign-in, the name servers
    /// it asks itself, the time servers, the certificate store, the SSH server
    /// with its command line, and the log - is the node below.
    /// </para>
    /// </remarks>
    public partial class DownloadServerNode : WWCPNode
    {

        #region Data

        /// <summary>
        /// The TCP port the web interface listens on, unless another is given.
        /// </summary>
        /// <remarks>
        /// Beside the PKI's 2360, the NTS server's 2361, the DNS server's 2362,
        /// the log server's 2363 and the PKI's registration authority's 2364,
        /// so that it can run on the same bench as all of them. Its SSH server
        /// - and with it SFTP - is then on 22365.
        /// </remarks>
        public static new readonly  IPPort    DefaultHTTPPort        = IPPort.Parse(2365);

        /// <summary>
        /// What kind of node a download server is.
        /// </summary>
        /// <remarks>
        /// "DownloadServer" as the organization its accounts are in - which a
        /// first start writes into the accounts file and every start after it
        /// reads back, so it is the one that must never change.
        /// </remarks>
        public static readonly      NodeKind  DownloadServerKind     = new (
                                                                           Name:           "download server",
                                                                           Tag:            "downloadserver",
                                                                           Product:        "DownloadServer",
                                                                           Organization:   "DownloadServer",
                                                                           LogFilePrefix:  "downloadserver"
                                                                       );

        /// <summary>
        /// The kinds of certificate the node's store keeps: the four of TLS -
        /// the identity its web interface and its download port may show, and
        /// the roots of the name and time servers it asks itself.
        /// </summary>
        public static readonly      IReadOnlyList<CertificateKind>  CertificateKinds  = [ .. CertificateKindExtensions.TLS ];

        /// <summary>
        /// The port HTTP downloads are served on, as a start that cannot have it says.
        /// </summary>
        public static readonly      NodePort  HTTPDownloadPort       = new ("HTTP downloads");

        /// <summary>
        /// The manifest resource prefix of the embedded web interface (see the
        /// EmbedFrontend target of DownloadServer.csproj).
        /// </summary>
        public const                String    HTTPRoot               = "cloud.charging.open.DownloadServer.HTTPRoot.";

        private Int64 httpDownloadsRefused;

        #endregion

        #region Properties

        /// <summary>
        /// What the "downloadServer" section and the switches of this start said.
        /// </summary>
        public DownloadServerSettings     Settings             { get; }

        /// <summary>
        /// The directory the areas lie in, given in full.
        /// </summary>
        public String                     FilesDirectory       { get; }

        /// <summary>
        /// The record of the downloads.
        /// </summary>
        public DownloadStore              Downloads            { get; }

        /// <summary>
        /// The JSON API.
        /// </summary>
        public DownloadServerHTTPAPI      API                  { get; }

        /// <summary>
        /// The HTTP server that serves the files by GET, or null where the
        /// section says "http": false.
        /// </summary>
        public HTTPServer?                DownloadPort         { get; }

        /// <summary>
        /// The areas of the SFTP tree the files are offered in - one an area of
        /// the section -, or none where it says "sftp": false.
        /// </summary>
        public IReadOnlyList<SftpArea>    SftpFileAreas        { get; } = [];

        /// <summary>
        /// Whether the download port speaks TLS - with the web interface's
        /// certificate, where the node shows one there.
        /// </summary>
        public Boolean                    DownloadsOverHTTPS   { get; }

        /// <summary>
        /// How many HTTP downloads were turned away since the start: without
        /// credentials, by an account that may not download, or of a file that
        /// is not there.
        /// </summary>
        public Int64                      HTTPDownloadsRefused
            => Interlocked.Read(ref httpDownloadsRefused);

        #endregion

        #region Constructor(s)

        /// <summary>
        /// One download server, with its web interface and its command line over SSH.
        /// </summary>
        /// <param name="HTTPHostname">The address the web interface listens on; 127.0.0.1 by default.</param>
        /// <param name="HTTPPort">The TCP port it listens on; <see cref="DefaultHTTPPort"/> by default.</param>
        /// <param name="HTTPServer">An HTTP server to register within, or null to make one.</param>
        /// <param name="BasePath">What everything of this node sits below; the root by default.</param>
        /// <param name="HTTPRootPath">Where the JSON API sits; "/api" below <paramref name="BasePath"/> by default.</param>
        /// <param name="ExtAPI">An HTTPExt API to sign in against, or null for one of this node's own.</param>
        /// <param name="AccountsPath">The directory the accounts live in between starts.</param>
        /// <param name="ConfigFile">Where the configuration lives between starts.</param>
        /// <param name="DNSClient">How this node resolves names itself, or null to make a client.</param>
        /// <param name="NTSClient">Where the node reads the time for its own check, or null to make a client.</param>
        /// <param name="Frontend">Where the web interface comes from, or null for the embedded bundle.</param>
        /// <param name="CertificatesPath">The directory the node's certificate store lives in between starts.</param>
        /// <param name="Log">Where everything that happens is written, or null to make a log.</param>
        /// <param name="LogToConsole">Whether the log is also written to the console.</param>
        /// <param name="ConsoleLogLevel">How much of it reaches the console.</param>
        /// <param name="LogPath">The directory the log files are written to, or null to write none.</param>
        /// <param name="BridgeDebugLog">Whether what the libraries below write with DebugX is picked up.</param>
        /// <param name="SSH">What the program says about the SSH server; SFTP is served by default on a download server.</param>
        /// <param name="Web">What the program says of HTTPS and of the reverse proxies in front.</param>
        /// <param name="CommandLine">What the switches of this start say over the "downloadServer" section, or null for nothing.</param>
        public DownloadServerNode(IPAddress?                                          HTTPHostname       = null,
                                  IPPort?                                             HTTPPort           = null,
                                  HTTPServer?                                         HTTPServer         = null,
                                  HTTPPath?                                           BasePath           = null,
                                  HTTPPath?                                           HTTPRootPath       = null,
                                  HTTPExtAPI?                                         ExtAPI             = null,
                                  String?                                             AccountsPath       = null,
                                  WWCPConfigFile?                                     ConfigFile         = null,
                                  DNSClient?                                          DNSClient          = null,
                                  NTSClient?                                          NTSClient          = null,
                                  IStaticContentSource?                               Frontend           = null,
                                  String?                                             CertificatesPath   = null,
                                  EventLog?                                           Log                = null,
                                  Boolean                                             LogToConsole       = true,
                                  LogLevel                                            ConsoleLogLevel    = LogLevel.Info,
                                  String?                                             LogPath            = null,
                                  Boolean                                             BridgeDebugLog     = true,
                                  SSHSettings?                                        SSH                = null,
                                  WebConfiguration?                                   Web                = null,
                                  Func<DownloadServerSettings, DownloadServerSettings>?  CommandLine     = null)

            : base(Kind:                  DownloadServerKind,
                   Version:               typeof(DownloadServerNode).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                   HTTPPort:              HTTPPort ?? DefaultHTTPPort,
                   HTTPHostname:          HTTPHostname,
                   HTTPServer:            HTTPServer,
                   BasePath:              BasePath,
                   HTTPRootPath:          HTTPRootPath,
                   ExtAPI:                ExtAPI,
                   AccountsPath:          AccountsPath,
                   Resources:             DownloadServerAccess.Resources,
                   RoleDefinitions:       DownloadServerAccess.Roles,
                   ConfigFile:            ConfigFile,
                   DNSClient:             DNSClient,
                   NTSClient:             NTSClient,
                   Frontend:              Frontend ?? new EmbeddedContentSource(HTTPRoot, typeof(DownloadServerNode).Assembly),
                   CertificatesPath:      CertificatesPath,
                   CertificateKinds:      CertificateKinds,
                   Log:                   Log,
                   LogToConsole:          LogToConsole,
                   ConsoleLogLevel:       ConsoleLogLevel,
                   LogPath:               LogPath,
                   BridgeDebugLog:        BridgeDebugLog,
                   // Files over SFTP are what a download server is there for
                   // as much as over HTTP: on, unless the "ssh" section says
                   // "sftp": false.
                   SSH:                   SSH is not null
                                              ? SSH with { SftpByDefault = true }
                                              : null,
                   Web:                   Web)

        {

            #region The "downloadServer" section, and what the switches say over it

            if (!DownloadServerSettings.TryParse(ConfigurationDocument[DownloadServerSettings.SectionName], out var settings, out var settingsError))
                throw new InvalidOperationException($"'{this.ConfigFile.Path}': {settingsError} Repair or remove '{this.ConfigFile.Path}' and start again.");

            this.Settings = CommandLine is not null
                                ? CommandLine(settings)
                                : settings;

            #endregion

            #region The areas, and the record of the downloads

            this.FilesDirectory = BesideTheConfiguration(Settings.Directory);

            foreach (var area in Settings.Areas)
                Directory.CreateDirectory(DirectoryOf(area));

            ForgetUnfinishedPublications();

            this.Downloads = new DownloadStore(FilesDirectory,
                                               TimeProvider,
                                               problem => this.Log.Warning(problem, "downloadserver", "downloads"));

            var recorded = this.Downloads.Load();

            this.Log.Info($"{Files(null).Count} file(s) are offered in {Settings.Areas.Count} area(s) of '{FilesDirectory}'; {recorded} download(s) are recorded.",
                          "downloadserver", "files");

            #endregion

            #region The port HTTP downloads are served on

            if (Settings.HTTP)
            {

                // With the web interface's certificate, where it shows one: a
                // charging station believes what it installs only as far as it
                // believes where it came from, and one certificate to renew is
                // one thing to forget less.
                var certificate           = WebCertificate;

                this.DownloadsOverHTTPS   = certificate is not null;

                this.DownloadPort         = new HTTPServer(
                                                IPAddress:                  Settings.HTTPListen,
                                                TCPPort:                    Settings.HTTPPort,
                                                HTTPServerName:             $"OpenChargingCloud {Kind.Product} v{this.Version}",
                                                ServerCertificateSelector:  certificate is not null
                                                                                ? (server, client) => certificate.ChainFor(server, client).Certificate
                                                                                : null
                                            );

                if (certificate is not null)
                    this.DownloadPort.ServerCertificateChainSelector = certificate.ChainFor;

                RegisterHTTPDownloads(this.DownloadPort);

            }

            #endregion

            #region The areas of the SFTP tree

            if (Settings.SFTP)
            {

                var areas = new List<SftpArea>();

                foreach (var name in Settings.Areas)
                {

                    var area = new SftpArea(
                                   name,
                                   DirectoryOf(name),
                                   IsFileName,
                                   Read:   Permission.Run (DownloadServerAccess.Download),
                                   Write:  Permission.Edit(DownloadServerAccess.Files)
                               ) {
                                   MaxFileSize  = Settings.MaxFileSize,
                                   Tags         = [ "downloadserver", "files" ]
                               };

                    area.Changed    += SftpChanged;
                    area.Downloaded += SftpDownloaded;

                    AddSftpArea(area);
                    areas.Add(area);

                }

                this.SftpFileAreas = areas;

            }

            #endregion

            #region The JSON API

            this.API = new DownloadServerHTTPAPI(
                           HTTPServer:  this.HTTPServer,
                           Node:        this,
                           ExtAPI:      this.ExtAPI,
                           Log:         this.Log,
                           APIPath:     this.HTTPRootPath,
                           Version:     Version
                       );

            #endregion

            #region What somebody signed in over SSH gets

            // The download server's command line, with its commands beside the
            // node's - whichever program runs this download server. Before the
            // node starts, as the node asks.
            CommandLines = (terminal, caller) => new DownloadServerCLI(this, terminal, caller);

            #endregion

        }

        #endregion


        #region (private) BesideTheConfiguration(Path)

        /// <summary>
        /// A path as the section gives it: as it is where it is given in full,
        /// and beside the configuration file otherwise.
        /// </summary>
        private String BesideTheConfiguration(String Path)

            => System.IO.Path.IsPathRooted(Path)
                   ? System.IO.Path.GetFullPath(Path)
                   : System.IO.Path.GetFullPath(System.IO.Path.Combine(
                         System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(ConfigFile.Path)) ?? ".",
                         Path
                     ));

        #endregion

        #region (private) SftpChanged(Changed) / SftpDownloaded(Downloaded)

        /// <summary>
        /// A file published or taken away over SFTP: said, and told whoever
        /// listens - the Files page among them.
        /// </summary>
        private void SftpChanged(SftpAreaChanged Changed)
        {

            var by = Changed.Account.Id.ToString();

            if (Changed.Change == SftpAreaChange.PutInPlace && TryGetFile(Changed.Area.Path, Changed.Name, out var file, out _))
            {
                Log.Notice($"'{by}' published '{file.Path}' over SFTP from {Changed.From}: {SizeOf(file.Size)}.", "downloadserver", "files", "sftp");
                RaisePublished(file, by);
            }

            else if (Changed.Change == SftpAreaChange.Removed)
            {
                Log.Notice($"'{by}' took '/{Changed.Area.Path}/{Changed.Name}' away over SFTP from {Changed.From}.", "downloadserver", "files", "sftp");
                RaiseRemoved(Changed.Area.Path, Changed.Name, by);
            }

        }

        /// <summary>
        /// A file - or the rest of one - fetched over SFTP: recorded.
        /// </summary>
        private void SftpDownloaded(SftpAreaDownloaded Downloaded)
        {

            var download = Downloads.Record(DownloadProtocol.SFTP,
                                            Downloaded.Account.Id.ToString(),
                                            Downloaded.Area.Path,
                                            Downloaded.Name,
                                            Downloaded.FirstByte > 0 ? 206 : 200,
                                            Downloaded.FirstByte,
                                            Downloaded.BytesRead,
                                            Downloaded.Length,
                                            Unmapped(Downloaded.From));

            Log.Notice($"'{download.Downloader}' downloaded '{download.Path}' over SFTP from {download.RemoteAddress}: " +
                       $"{SizeOf(download.Bytes)}{(download.FirstByte > 0 ? $" from byte {download.FirstByte} on" : "")}{(download.ToTheEnd ? "" : ", not to its end")}.",
                       "downloadserver", "downloads", "sftp");

        }

        #endregion


        #region (protected override) OnListening()

        /// <summary>
        /// The download port opened, before the node calls itself started.
        /// </summary>
        protected override async Task OnListening()
        {

            if (DownloadPort is null)
                return;

            try
            {
                await DownloadPort.Start();
            }
            catch (SocketException problem)
            {
                throw new PortUnavailableException(Settings.HTTPPort, problem, HTTPDownloadPort);
            }

        }

        #endregion

        #region (protected override) OnStarted()

        protected override Task OnStarted()
        {

            Log.Info($"The JSON API of the download server is at {APIURL}v1/downloadServer", "web", "http");

            Log.Notice($"Files are offered {ListenersText()}; {Files(null).Count} lie in {String.Join(", ", Settings.Areas.Select(area => $"/{area}"))} of '{FilesDirectory}'.", "downloadserver");

            if (DownloadPort is not null && !DownloadsOverHTTPS)
                Log.Warning($"HTTP downloads are served over plain HTTP: what a charging station fetches can be changed on its way, and the password it signs in with crosses the network in the clear. " +
                            $"Give the web interface a certificate (\"web.tls\"), which the download port then shows as well, or put a proxy that speaks TLS in front.",
                            "downloadserver", "http", "security");

            if (DownloadPort is not null && Settings.AnonymousHTTP)
                Log.Warning($"HTTP downloads are served without credentials as well (\"anonymousHTTP\": true): whoever reaches the port fetches every file offered.",
                            "downloadserver", "http", "security");

            if (Settings.SFTP && !SFTPEnabled)
                Log.Warning($"SFTP downloads are switched on in the \"{DownloadServerSettings.SectionName}\" section, but the node serves no SFTP: " +
                             "the SSH server is off, or the \"ssh\" section says \"sftp\": false.",
                            "downloadserver", "sftp");

            return Task.CompletedTask;

        }

        #endregion

        #region (protected override) OnStopping()

        protected override async Task OnStopping()
        {

            if (DownloadPort is not null)
                await DownloadPort.Stop();

            await base.OnStopping();

        }

        #endregion


        #region WebMaxFileSize

        /// <summary>
        /// The largest file that may be published through the web interface:
        /// what the section allows, and no more than the web interface's HTTP
        /// server takes as the body of one request. A larger one is published
        /// over SFTP, or put in its area's directory.
        /// </summary>
        public Int64 WebMaxFileSize

            => Math.Min(Settings.MaxFileSize, (Int64) Math.Min(HTTPServer.MaxHTTPBodySize, (UInt64) Int64.MaxValue));

        #endregion

        #region DownloadURL / ListenersText()

        /// <summary>
        /// The root of the download port, as the banner and the web interface
        /// say it: "http://any:8365/".
        /// </summary>
        public String DownloadURL

            => $"{(DownloadsOverHTTPS ? "https" : "http")}://{DownloadServerSettings.TextOf(Settings.HTTPListen)}:{Settings.HTTPPort}/";

        /// <summary>
        /// Where files are offered, as the banner and the command line say it.
        /// </summary>
        public String ListenersText()
        {

            var listeners = new List<String>();

            if (DownloadPort is not null)
                listeners.Add($"by HTTP GET on {DownloadURL}<area>/<file name>");

            if (SftpFileAreas.Count > 0)
                listeners.Add(SFTPEnabled
                                  ? $"over SFTP on port {SSHPort} in /<area>"
                                  : $"over SFTP - which the node does not serve now");

            return listeners.Count > 0
                       ? String.Join(" and ", listeners)
                       : "nowhere - \"http\" and \"sftp\" are both false";

        }

        #endregion

        #region StatusJSON()

        /// <summary>
        /// How the download server stands.
        /// </summary>
        public JObject StatusJSON()
        {

            var files   = Files(null);
            var latest  = Downloads.Latest;

            return new (
                       new JProperty("directory",           FilesDirectory),
                       new JProperty("areas",               new JArray(Settings.Areas.Select(area => new JObject(
                                                                new JProperty("name",   area),
                                                                new JProperty("files",  files.Count(file => file.Area == area)),
                                                                new JProperty("size",   files.Where(file => file.Area == area).Sum(file => file.Size))
                                                            )))),
                       new JProperty("http",                DownloadPort is not null
                                                                ? new JObject(
                                                                      new JProperty("listening",  DownloadPort.IsRunning),
                                                                      new JProperty("url",        DownloadURL),
                                                                      new JProperty("tls",        DownloadsOverHTTPS),
                                                                      new JProperty("anonymous",  Settings.AnonymousHTTP),
                                                                      new JProperty("refused",    HTTPDownloadsRefused)
                                                                  )
                                                                : null),
                       new JProperty("sftp",                SftpFileAreas.Count > 0
                                                                ? new JObject(
                                                                      new JProperty("serving",    SFTPEnabled),
                                                                      new JProperty("port",       SSHPort?.ToUInt16()),
                                                                      new JProperty("passwords",  SSHPasswords)
                                                                  )
                                                                : null),
                       new JProperty("maxFileSize",         Settings.MaxFileSize),
                       new JProperty("webMaxFileSize",      WebMaxFileSize),
                       new JProperty("files",               files.Count),
                       new JProperty("totalSize",           files.Sum(file => file.Size)),
                       new JProperty("downloads",           Downloads.Count),
                       new JProperty("downloadedBytes",     Downloads.TotalBytes),
                       new JProperty("downloaders",         Downloads.Downloaders.Count),
                       new JProperty("latest",              latest?.ToJSON())
                   );

        }

        #endregion

        #region ConfigurationJSON()

        /// <summary>
        /// What this download server is, as the Configuration page reads it:
        /// what the node below says of itself, and on top the download server.
        /// </summary>
        public override JObject ConfigurationJSON()
        {

            var json = base.ConfigurationJSON();

            json.AddFirst(new JProperty("downloadServer", new JObject(
                              new JProperty("version",   Version),
                              new JProperty("createdAt", CreatedAt.ToString("o")),
                              new JProperty("machine",   Environment.MachineName),
                              new JProperty("runtime",   Environment.Version.ToString()),
                              new JProperty("os",        Environment.OSVersion.ToString()),
                              new JProperty("settings",  Settings.ToJSON()),
                              new JProperty("status",    StatusJSON())
                          )));

            return json;

        }

        #endregion

        #region (static) SizeOf(Bytes)

        /// <summary>
        /// A number of bytes as somebody reads it.
        /// </summary>
        public static String SizeOf(Int64 Bytes)

            => Bytes switch {
                   < 1024                => $"{Bytes} B",
                   < 1024 * 1024         => $"{Bytes / 1024.0:0.#} KiB",
                   < 1024L * 1024 * 1024 => $"{Bytes / (1024.0 * 1024):0.#} MiB",
                   _                     => $"{Bytes / (1024.0 * 1024 * 1024):0.##} GiB"
               };

        #endregion

    }

}
