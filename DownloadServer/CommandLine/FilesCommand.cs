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
    /// The files offered - of every area, or of one - and a file offered no
    /// more.
    /// </summary>
    /// <param name="CLI">The command line of the download server to ask.</param>
    public class FilesCommand(DownloadServerCLI CLI) : ACLICommand<DownloadServerCLI>(CLI),
                                                       ICLICommand
    {

        #region Data

        /// <summary>
        /// The name this is typed as.
        /// </summary>
        public static readonly String CommandName = "files";

        private static readonly String[] Remove = [ "remove", "delete" ];

        #endregion

        #region Suggest(Arguments)

        public override IEnumerable<SuggestionResponse> Suggest(String[] Arguments)
        {

            if (Arguments.Length == 1)
                return CommandName.StartsWith(Arguments[0], StringComparison.OrdinalIgnoreCase)
                           ? [ SuggestionResponse.CommandCompleted(CommandName) ]
                           : [];

            // The areas there are, and "remove".
            if (Arguments.Length == 2)
                return cli.DownloadServer.Settings.Areas.
                           Append("remove").
                           Where (name => name.StartsWith(Arguments[1], StringComparison.OrdinalIgnoreCase)).
                           Select(name => SuggestionResponse.ParameterCompleted($"{CommandName} {name}"));

            // The file to take away, as "<area>/<name>".
            if (Arguments.Length == 3 && Remove.Any(word => word.Equals(Arguments[1], StringComparison.OrdinalIgnoreCase)))
                return cli.DownloadServer.Files(null).
                           Select(file => file.Path.TrimStart('/')).
                           Where (path => path.StartsWith(Arguments[2], StringComparison.Ordinal)).
                           Select(path => SuggestionResponse.ParameterCompleted($"{CommandName} {Arguments[1]} {path}"));

            return [];

        }

        #endregion

        #region Execute(Arguments, CancellationToken)

        public override Task<String[]> Execute(String[]           Arguments,
                                               CancellationToken  CancellationToken)
        {

            if (Arguments.Length >= 2 && Remove.Any(word => word.Equals(Arguments[1], StringComparison.OrdinalIgnoreCase)))
                return Task.FromResult(Removed(Arguments));

            if (Arguments.Length > 2)
                return Task.FromResult<String[]>([ Help() ]);

            if (!cli.MayDo(Permission.Read(DownloadServerAccess.Files), CommandName, out var refused))
                return Task.FromResult<String[]>([ refused ]);

            var area = Arguments.Length == 2 ? Arguments[1] : null;

            if (area is not null && !cli.DownloadServer.HasArea(area))
                return Task.FromResult<String[]>([ $"There is no area '{area}'. The areas are {String.Join(", ", cli.DownloadServer.Settings.Areas)}." ]);

            var files      = cli.DownloadServer.Files(area);

            if (files.Count == 0)
                return Task.FromResult<String[]>([ area is not null
                                                       ? $"Nothing is offered in /{area}."
                                                       : "Nothing is offered yet." ]);

            var downloaded = cli.DownloadServer.Downloads.Files.ToDictionary(one => one.Path, StringComparer.Ordinal);

            return Task.FromResult<String[]>([
                       $"{files.Count} file(s){(area is not null ? $" in /{area}" : "")}:",
                       .. files.Select(file => {
                              downloaded.TryGetValue(file.Path, out var made);
                              return $"    {file.Path,-48} {DownloadServerNode.SizeOf(file.Size),10}  {file.Modified.ToUniversalTime():yyyy-MM-dd HH:mm:ss}Z  " +
                                     $"{file.SHA256[..16]}...  {made.Downloads} download(s)";
                          })
                   ]);

        }

        #endregion

        #region (private) Removed(Arguments)

        private String[] Removed(String[] Arguments)
        {

            var parts = Arguments.Length == 3 ? Arguments[2].Trim('/').Split('/') : [];

            if (parts.Length != 2)
                return [ $"{CommandName} remove <area>/<file name> - e.g. '{CommandName} remove firmware/station-4.2.1.bin'" ];

            if (!cli.MayDo(Permission.Edit(DownloadServerAccess.Files), CommandName, out var refused))
                return [ refused ];

            return cli.DownloadServer.RemoveFile(parts[0], parts[1], cli.Caller.ToString(), out var problem)
                       ? [ $"'/{parts[0]}/{parts[1]}' is offered no more. What was downloaded of it stays recorded." ]
                       : [ problem ];

        }

        #endregion

        #region Help()

        public override String Help()

            => $"{CommandName} [<area> | remove <area>/<file name>] - the files offered, of every area or of one; or one offered no more";

        #endregion

    }

}
