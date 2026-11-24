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
using org.GraphDefined.Vanaheimr.Illias;

using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.DownloadServer.CommandLine
{

    /// <summary>
    /// The accounts files are downloaded under: listed, made, given a new
    /// password, and taken away.
    /// </summary>
    /// <param name="CLI">The command line of the download server to ask.</param>
    public class DownloadersCommand(DownloadServerCLI CLI) : ACLICommand<DownloadServerCLI>(CLI),
                                                             ICLICommand
    {

        #region Data

        /// <summary>
        /// The name this is typed as.
        /// </summary>
        public static readonly String CommandName = "downloaders";

        private static readonly String[] Add       = [ "add" ];
        private static readonly String[] Password  = [ "password" ];
        private static readonly String[] Remove    = [ "remove", "delete" ];

        #endregion

        #region Suggest(Arguments)

        public override IEnumerable<SuggestionResponse> Suggest(String[] Arguments)
        {

            if (Arguments.Length == 1)
                return CommandName.StartsWith(Arguments[0], StringComparison.OrdinalIgnoreCase)
                           ? [ SuggestionResponse.CommandCompleted(CommandName) ]
                           : [];

            if (Arguments.Length == 2)
                return new[] { "add", "password", "remove" }.
                           Where (sub => sub.StartsWith(Arguments[1], StringComparison.OrdinalIgnoreCase)).
                           Select(sub => SuggestionResponse.ParameterCompleted($"{CommandName} {sub}"));

            // The downloaders there are, for the one to change.
            if (Arguments.Length == 3 && (Is(Arguments[1], Password) || Is(Arguments[1], Remove)))
                return cli.DownloadServer.DownloaderAccounts.
                           Select(user => user.Id.ToString()).
                           Where (name => name.StartsWith(Arguments[2], StringComparison.OrdinalIgnoreCase)).
                           Select(name => SuggestionResponse.ParameterCompleted($"{CommandName} {Arguments[1]} {name}"));

            return [];

        }

        #endregion

        #region Execute(Arguments, CancellationToken)

        public override async Task<String[]> Execute(String[]           Arguments,
                                                     CancellationToken  CancellationToken)
        {

            if (Arguments.Length == 1)
                return Listed();

            if (Is(Arguments[1], Add))
            {

                if (Arguments.Length < 3)
                    return [ $"{CommandName} add <name> [<what it is called>] - e.g. '{CommandName} add CS001 Charging station in the yard'" ];

                if (!cli.MayDo(Permission.Edit(DownloadServerAccess.Downloaders), CommandName, out var refused))
                    return [ refused ];

                return Said(await cli.DownloadServer.AddDownloader(Arguments[2],
                                                                   Arguments.Length > 3 ? String.Join(' ', Arguments[3..]) : null,
                                                                   cli.Caller.ToString()));

            }

            if (Is(Arguments[1], Password) || Is(Arguments[1], Remove))
            {

                if (Arguments.Length != 3)
                    return [ $"{CommandName} {Arguments[1]} <name>" ];

                if (!cli.MayDo(Permission.Edit(DownloadServerAccess.Downloaders), CommandName, out var refused))
                    return [ refused ];

                return Said(Is(Arguments[1], Password)
                                ? await cli.DownloadServer.NewDownloaderPassword(Arguments[2], cli.Caller.ToString())
                                : await cli.DownloadServer.RemoveDownloader     (Arguments[2], cli.Caller.ToString()));

            }

            return [ Help() ];

        }

        #endregion


        #region (private) Listed()

        private String[] Listed()
        {

            if (!cli.MayDo(Permission.Read(DownloadServerAccess.Downloaders), CommandName, out var refused))
                return [ refused ];

            var downloaders  = cli.DownloadServer.DownloaderAccounts;

            if (downloaders.Count == 0)
                return [ $"There is no downloader. '{CommandName} add <name>' makes one." ];

            var downloaded   = cli.DownloadServer.Downloads.Downloaders.ToDictionary(one => one.Name, StringComparer.OrdinalIgnoreCase);

            return [
                       $"{downloaders.Count} downloader(s):",
                       .. downloaders.Select(user => {

                              var id = user.Id.ToString();

                              return downloaded.TryGetValue(id, out var made)
                                         ? $"    {id,-24} {user.Name.FirstText(),-32} {made.Downloads} download(s), the last at {made.Last.ToUniversalTime():yyyy-MM-dd HH:mm:ss}Z"
                                         : $"    {id,-24} {user.Name.FirstText(),-32} nothing downloaded yet";

                          })
                   ];

        }

        #endregion

        #region (private static) Said(Change)

        private static String[] Said(DownloaderChange Change)

            => Change.Password is not null
                   ? [ Change.Said,
                       $"Its password, shown this once: {Change.Password}" ]
                   : [ Change.Said ];

        #endregion

        #region (private static) Is(Text, Words)

        private static Boolean Is(String Text, String[] Words)

            => Words.Any(word => word.Equals(Text, StringComparison.OrdinalIgnoreCase));

        #endregion

        #region Help()

        public override String Help()

            => $"{CommandName} [add <name> [<what it is called>] | password <name> | remove <name>] - the accounts files are downloaded under: " +
                "listed, made, given a new password, taken away";

        #endregion

    }

}
