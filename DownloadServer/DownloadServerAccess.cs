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

using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.DownloadServer
{

    /// <summary>
    /// What a download server adds to the resources every node has, and the
    /// roles of the people - and charging stations - around it.
    /// </summary>
    /// <remarks>
    /// The node brings the viewer, who may look at everything, and the
    /// administrators, who may do everything. The configuration file may add
    /// roles and say differently what one of them may do - see the node's
    /// "roles" section.
    /// </remarks>
    public static class DownloadServerAccess
    {

        #region Resources

        /// <summary>
        /// The files offered: read, the list of them, what is known of each and
        /// each itself through the web interface; edited, one published or
        /// taken away.
        /// </summary>
        public const String  Files        = "files";

        /// <summary>
        /// What was downloaded: read, the record of every download.
        /// </summary>
        public const String  Downloads    = "downloads";

        /// <summary>
        /// The accounts files are downloaded under: read, who they are; edited,
        /// one made, given a new password or no longer let download.
        /// </summary>
        public const String  Downloaders  = "downloaders";

        /// <summary>
        /// Downloading itself: run, a file fetched by HTTP or over SFTP.
        /// </summary>
        public const String  Download     = "download";

        /// <summary>
        /// The resources of a download server's own.
        /// </summary>
        public static readonly IReadOnlyList<String>  Resources = [ Files, Downloads, Downloaders, Download ];

        #endregion

        #region Roles

        /// <summary>
        /// A charging station, a local controller - whatever fetches files:
        /// may download them, and nothing else.
        /// </summary>
        public static readonly Role  Downloader  = new ("downloader",
                                                        [ Permission.Run(Download) ],
                                                        "downloads files - a charging station, a local controller: fetches what it is pointed to, and sees nothing else");

        /// <summary>
        /// Whoever puts files on the download server - a build pipeline that
        /// publishes a firmware image: may see the files, put them in their
        /// areas over SFTP or the web interface, take them away, and download
        /// them, but may not see who downloaded what.
        /// </summary>
        public static readonly Role  Publisher   = new ("publisher",
                                                        [ Permission.Read(Files),
                                                          Permission.Edit(Files),
                                                          Permission.Run (Download) ],
                                                        "publishes files - a build pipeline: puts them in their areas and takes them away, over SFTP or the web interface");

        /// <summary>
        /// Whoever looks after the download server: may look at everything,
        /// publish and take away files, and ask whether the network works - but
        /// may not make or change downloaders.
        /// </summary>
        public static readonly Role  Operator    = new ("operator",
                                                        [ Permission.Read(Permission.AnyResource),
                                                          Permission.Edit(Files),
                                                          Permission.Run (NodeResources.DNS),
                                                          Permission.Run (NodeResources.NTS) ],
                                                        "looks after the download server: looks at everything, publishes and takes away files, and asks whether the network works");

        /// <summary>
        /// All of them, in the order a sentence naming them reads best.
        /// </summary>
        public static readonly IReadOnlyList<Role>  Roles = [ Operator, Publisher, Downloader ];

        #endregion

    }

}
