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
    /// The newest downloads - of every downloader, or of one.
    /// </summary>
    /// <param name="CLI">The command line of the download server to ask.</param>
    public class DownloadsCommand(DownloadServerCLI CLI) : ACLICommand<DownloadServerCLI>(CLI),
                                                           ICLICommand
    {

        #region Data

        /// <summary>
        /// The name this is typed as.
        /// </summary>
        public static readonly String CommandName = "downloads";

        /// <summary>
        /// How many downloads are listed.
        /// </summary>
        public const Int32 Listed = 20;

        #endregion

        #region Suggest(Arguments)

        public override IEnumerable<SuggestionResponse> Suggest(String[] Arguments)
        {

            if (Arguments.Length == 1)
                return CommandName.StartsWith(Arguments[0], StringComparison.OrdinalIgnoreCase)
                           ? [ SuggestionResponse.CommandCompleted(CommandName) ]
                           : [];

            // The downloaders there are.
            if (Arguments.Length == 2)
                return cli.DownloadServer.Downloads.Downloaders.
                           Select(one => one.Name).
                           Where (name => name.StartsWith(Arguments[1], StringComparison.OrdinalIgnoreCase)).
                           Select(name => SuggestionResponse.ParameterCompleted($"{CommandName} {name}"));

            return [];

        }

        #endregion

        #region Execute(Arguments, CancellationToken)

        public override Task<String[]> Execute(String[]           Arguments,
                                               CancellationToken  CancellationToken)
        {

            if (Arguments.Length > 2)
                return Task.FromResult<String[]>([ Help() ]);

            if (!cli.MayDo(Permission.Read(DownloadServerAccess.Downloads), CommandName, out var refused))
                return Task.FromResult<String[]>([ refused ]);

            var downloader  = Arguments.Length == 2 ? Arguments[1] : null;
            var downloads   = cli.DownloadServer.Downloads.Query(new DownloadQuery(Downloader: downloader, Take: Listed), out var total);

            if (total == 0)
                return Task.FromResult<String[]>([ downloader is not null
                                                       ? $"'{downloader}' downloaded nothing."
                                                       : "Nothing was downloaded yet." ]);

            return Task.FromResult<String[]>([
                       total > downloads.Count
                           ? $"The newest {downloads.Count} of {total} download(s){(downloader is not null ? $" of '{downloader}'" : "")}:"
                           : $"{total} download(s){(downloader is not null ? $" of '{downloader}'" : "")}, the newest first:",
                       .. downloads.Select(Line)
                   ]);

        }

        #endregion


        #region (static) Line(Download)

        /// <summary>
        /// One download as the command line lists it.
        /// </summary>
        public static String Line(DownloadEvent Download)

            => $"    {Download.Id,6}  {Download.Timestamp.ToUniversalTime():yyyy-MM-dd HH:mm:ss}Z  {Download.Protocol,-4}  " +
               $"{Download.Path,-40} {DownloadServerNode.SizeOf(Download.Bytes),10}" +
               (Download.FirstByte > 0 ? $" from byte {Download.FirstByte}" : "") +
               $"  by {Download.Downloader} from {Download.RemoteAddress}";

        #endregion

        #region Help()

        public override String Help()

            => $"{CommandName} [<downloader>] - the newest {Listed} downloads, of every downloader or of one";

        #endregion

    }

}
