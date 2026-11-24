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

using System.Reflection;

using org.GraphDefined.Vanaheimr.CLI;

using cloud.charging.open.protocols.WWCP.Node.CommandLine;

#endregion

namespace cloud.charging.open.DownloadServer.CommandLine
{

    /// <summary>
    /// The command line of a running download server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Commands are not listed anywhere. The commands every node has are the
    /// node's, and NodeCLI, which this derives from, finds them in WWCP_Node.
    /// The constructor registers this type as well, so that this assembly is
    /// walked for anything that implements ICLICommand and can be built from a
    /// DownloadServerCLI: a command of the download server's own is a new file
    /// and nothing else - 'downloadServer', 'files', 'downloads' and
    /// 'downloaders' among them.
    /// </para>
    /// <para>
    /// Here, beside the files, and not in a program that runs it: what a
    /// download server can be told is the download server's, and is told over
    /// SSH as well as at the console. The node hands it to whoever signs in
    /// over SSH by itself (see DownloadServerNode.cs), so that a download
    /// server run by another program - the tests, a test environment - has the
    /// same commands as one run by a program of its own.
    /// </para>
    /// </remarks>
    public class DownloadServerCLI : NodeCLI
    {

        #region Properties

        /// <summary>
        /// The download server these commands are about.
        /// </summary>
        public DownloadServerNode  DownloadServer  { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create the command line of the given download server, at the console.
        /// </summary>
        public DownloadServerCLI(DownloadServerNode  DownloadServer,
                                 params Assembly[]   AssembliesWithCLICommands)

            : base(DownloadServer, AssembliesWithCLICommands)

        {

            this.DownloadServer = DownloadServer;

            RegisterCLIType(typeof(DownloadServerCLI));

        }

        /// <summary>
        /// Create the command line of the given download server on the given
        /// terminal, for the given caller - a session over SSH.
        /// </summary>
        public DownloadServerCLI(DownloadServerNode  DownloadServer,
                                 ICLITerminal        Terminal,
                                 CLICaller           Caller,
                                 params Assembly[]   AssembliesWithCLICommands)

            : base(DownloadServer, Terminal, Caller, AssembliesWithCLICommands)

        {

            this.DownloadServer = DownloadServer;

            RegisterCLIType(typeof(DownloadServerCLI));

        }

        #endregion


        #region (protected override) GetPrompt()

        /// <summary>
        /// Which port this download server answers its web interface on,
        /// because a machine that is one of several on a bench should say which
        /// one it is before it asks for a command.
        /// </summary>
        protected override String GetPrompt()

            => $"downloads:{DownloadServer.HTTPPort}> ";

        #endregion

    }

}
