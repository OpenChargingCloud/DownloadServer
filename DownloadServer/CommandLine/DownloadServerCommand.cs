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

using org.GraphDefined.Vanaheimr.CLI;

using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.DownloadServer.CommandLine
{

    /// <summary>
    /// How the download server stands: where it offers files, how many, and
    /// the newest download.
    /// </summary>
    /// <param name="CLI">The command line of the download server to ask.</param>
    public class DownloadServerCommand(DownloadServerCLI CLI) : ACLICommand<DownloadServerCLI>(CLI),
                                                                ICLICommand
    {

        #region Data

        /// <summary>
        /// The name this is typed as.
        /// </summary>
        public static readonly String CommandName = "downloadServer";

        #endregion

        #region Suggest(Arguments)

        public override IEnumerable<SuggestionResponse> Suggest(String[] Arguments)

            => Arguments.Length == 1 && CommandName.StartsWith(Arguments[0], StringComparison.OrdinalIgnoreCase)
                   ? [ SuggestionResponse.CommandCompleted(CommandName) ]
                   : [];

        #endregion

        #region Execute(Arguments, CancellationToken)

        public override Task<String[]> Execute(String[]           Arguments,
                                               CancellationToken  CancellationToken)
        {

            if (Arguments.Length > 1)
                return Task.FromResult<String[]>([ Help() ]);

            if (!cli.MayDo(Permission.Read(DownloadServerAccess.Files), CommandName, out var refused))
                return Task.FromResult<String[]>([ refused ]);

            var node    = cli.DownloadServer;
            var files   = node.Files(null);
            var latest  = node.Downloads.Latest;

            String[] lines = [
                $"files offered   {node.ListenersText()}",
                $"areas           {String.Join(", ", node.Settings.Areas.Select(area => $"/{area} ({files.Count(file => file.Area == area)})"))} in {node.FilesDirectory}",
                $"files           {files.Count} ({DownloadServerNode.SizeOf(files.Sum(file => file.Size))})" +
                                  (files.Count > 0 ? $"; see '{FilesCommand.CommandName}'" : ""),
                $"largest file    {DownloadServerNode.SizeOf(node.Settings.MaxFileSize)} to be published",
                $"downloads       {node.Downloads.Count} ({DownloadServerNode.SizeOf(node.Downloads.TotalBytes)}) by {node.Downloads.Downloaders.Count} downloader(s)" +
                                  (node.Downloads.Count > 0 ? $"; see '{DownloadsCommand.CommandName}'" : ""),
                $"newest          {(latest is not null ? DownloadsCommand.Line(latest).Trim() : "none yet")}",
                $"downloaders     {node.DownloaderAccounts.Count}" +
                                  (node.DownloaderAccounts.Count == 0 ? $" - '{DownloadersCommand.CommandName} add <name>' makes one" : $"; see '{DownloadersCommand.CommandName}'"),
                $"refused         {node.HTTPDownloadsRefused} HTTP download(s) since the start"
            ];

            return Task.FromResult(lines);

        }

        #endregion

        #region Help()

        public override String Help()

            => $"{CommandName} - how the download server stands: where it offers files, how many, the newest download";

        #endregion

    }

}
