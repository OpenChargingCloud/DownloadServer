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

using System.Globalization;
using System.Diagnostics.CodeAnalysis;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP.Node.Web;
using cloud.charging.open.protocols.WWCP.Node.Logging;

#endregion

namespace cloud.charging.open.DownloadServer
{

    /// <summary>
    /// The JSON API of a download server: what every node's has, and the
    /// download server at "/api/v1/downloadServer".
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item>GET  /api/v1/downloadServer                                 - how the download server stands: where files are offered, how many, how often fetched.</item>
    ///   <item>GET  /api/v1/downloadServer/settings                        - the "downloadServer" section as it is in effect, the switches of this start included.</item>
    ///   <item>GET  /api/v1/downloadServer/files                           - the files offered, of every area or - with ?area= - of one, and the areas there are.</item>
    ///   <item>GET  /api/v1/downloadServer/files/{area}/{name}             - one file: its size, when it was written, its SHA-256.</item>
    ///   <item>GET  /api/v1/downloadServer/files/{area}/{name}/content     - the file itself, to save.</item>
    ///   <item>PUT  /api/v1/downloadServer/files/{area}/{name}             - the body offered as that file, in the place of the one of that name.</item>
    ///   <item>PUT  /api/v1/downloadServer/files/{area}/{name}?offset=N    - one part of it, at byte N - 0 begins anew -, and with &amp;final=true the last: what the web interface sends of a file larger than one request may be; 409 with "received" where N is not where the parts before ended.</item>
    ///   <item>POST /api/v1/downloadServer/files/{area}/{name}/remove      - the file offered no more.</item>
    ///   <item>GET  /api/v1/downloadServer/downloads                       - what was downloaded, filtered by ?downloader=, ?area=, ?file=, ?since=, ?until=, ordered by ?order=newest|oldest, paged by ?skip= and ?take=.</item>
    ///   <item>GET  /api/v1/downloadServer/downloaders                     - the accounts in the downloader role.</item>
    ///   <item>POST /api/v1/downloadServer/downloaders                     - { "id", "name"? }: a downloader more, answered with its password, once.</item>
    ///   <item>POST /api/v1/downloadServer/downloaders/password            - { "id" }: a new password for a downloader, answered once.</item>
    ///   <item>POST /api/v1/downloadServer/downloaders/remove              - { "id" }: a downloader no longer let download.</item>
    /// </list>
    /// A file published through this API in one request is as large as the
    /// web interface's HTTP server takes a request; a larger one is sent in
    /// parts, as the web interface does, published over SFTP, or put in its
    /// area's directory.
    /// </remarks>
    public class DownloadServerHTTPAPI : NodeHTTPAPI
    {

        #region Properties

        /// <summary>
        /// The download server this API speaks for.
        /// </summary>
        public DownloadServerNode  DownloadServer  { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create and register the JSON API within the given HTTP server.
        /// </summary>
        public DownloadServerHTTPAPI(HTTPServer          HTTPServer,
                                     DownloadServerNode  Node,
                                     HTTPExtAPI          ExtAPI,
                                     EventLog            Log,
                                     HTTPPath?           APIPath   = null,
                                     String?             Version   = null)

            : base(HTTPServer,
                   Node,
                   ExtAPI,
                   Log,
                   APIPath,
                   Version ?? typeof(DownloadServerHTTPAPI).Assembly.GetName().Version?.ToString(3) ?? "0.0.0")

        {

            this.DownloadServer = Node;

            var root = HTTPPath.Root + "v1/downloadServer";

            AddHandler(root,                                    GetStatus,             HTTPMethod.GET);
            AddHandler(root + "/settings",                      GetSettings,           HTTPMethod.GET);
            AddHandler(root + "/files",                         GetFiles,              HTTPMethod.GET);
            AddHandler(root + "/files/{area}/{name}",           GetFile,               HTTPMethod.GET);
            AddHandler(root + "/files/{area}/{name}",           PutFile,               HTTPMethod.PUT);
            AddHandler(root + "/files/{area}/{name}/content",   GetFileContent,        HTTPMethod.GET);
            AddHandler(root + "/files/{area}/{name}/remove",    PostRemoveFile,        HTTPMethod.POST);
            AddHandler(root + "/downloads",                     GetDownloads,          HTTPMethod.GET);
            AddHandler(root + "/downloaders",                   GetDownloaders,        HTTPMethod.GET);
            AddHandler(root + "/downloaders",                   PostDownloader,        HTTPMethod.POST);
            AddHandler(root + "/downloaders/password",          PostPassword,          HTTPMethod.POST);
            AddHandler(root + "/downloaders/remove",            PostRemoveDownloader,  HTTPMethod.POST);

            // Every browser on the Files and the Downloads page hears of a
            // download, and of a file published or taken away, at once.
            Node.Downloads.OnDownloaded  += download         => Publish("download",      download.ToJSON());
            Node.OnPublished             += (file, by)       => Publish("filePublished", file.ToJSON());
            Node.OnRemoved               += (area, name, by) => Publish("fileRemoved",   new JObject(new JProperty("area", area), new JProperty("name", name)));

        }

        #endregion


        #region (private) GetStatus(Request) / GetSettings(Request)

        private Task<HTTPResponse> GetStatus(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(DownloadServerAccess.Files), false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, DownloadServer.StatusJSON()));

        }

        private Task<HTTPResponse> GetSettings(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(DownloadServerAccess.Files), false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, DownloadServer.Settings.ToJSON()));

        }

        #endregion

        #region (private) GetFiles(Request)

        /// <summary>
        /// The files offered - of one area, or of every one -, how often each
        /// was downloaded, and the areas there are.
        /// </summary>
        private Task<HTTPResponse> GetFiles(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(DownloadServerAccess.Files), false, out _, out var refused))
                return Task.FromResult(refused);

            var area = Request.QueryString.GetString("area");

            if (area is { Length: > 0 } && !DownloadServer.HasArea(area))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.NotFound, $"There is no area '{area}'."));

            var downloaded = DownloadServer.Downloads.Files.ToDictionary(one => one.Path, StringComparer.Ordinal);

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, new JObject(
                       new JProperty("areas",  new JArray(DownloadServer.Settings.Areas)),
                       new JProperty("files",  new JArray(DownloadServer.Files(area is { Length: > 0 } ? area : null).Select(file => {

                                                   var json = file.ToJSON();

                                                   downloaded.TryGetValue(file.Path, out var made);

                                                   json.Add(new JProperty("downloads",     made.Downloads));
                                                   json.Add(new JProperty("lastDownload",  made.Downloads > 0 ? made.Last.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) : null));

                                                   return json;

                                               })))
                   )));

        }

        #endregion

        #region (private) GetFile(Request)

        private Task<HTTPResponse> GetFile(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(DownloadServerAccess.Files), false, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryGetFile(Request, out var file, out _, out var notFound))
                return Task.FromResult(notFound);

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, file.ToJSON()));

        }

        #endregion

        #region (private) GetFileContent(Request)

        /// <summary>
        /// A file itself, to be saved under its name - for whoever may read
        /// the files in the web interface. Not recorded as a download: the
        /// record is of what the download port and SFTP served.
        /// </summary>
        private Task<HTTPResponse> GetFileContent(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(DownloadServerAccess.Files), false, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryGetFile(Request, out var file, out var path, out var notFound))
                return Task.FromResult(notFound);

            FileStream stream;

            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.InternalServerError, $"{file.Path} could not be read: {problem.Message}"));
            }

            return Task.FromResult(
                       new HTTPResponse.Builder(Request) {
                           HTTPStatusCode  = HTTPStatusCode.OK,
                           ContentType     = HTTPContentType.Application.OCTETSTREAM,
                           ContentStream   = stream,
                           CacheControl    = "no-store"
                       }.
                       Set("Content-Disposition",     $"attachment; filename=\"{DownloadServerNode.AsciiOf(file.Name)}\"; filename*=UTF-8''{Uri.EscapeDataString(file.Name)}").
                       Set("X-Content-Type-Options",  "nosniff").
                       Set("Content-Security-Policy", "default-src 'none'; sandbox").
                       AsImmutable
                   );

        }

        #endregion

        #region (private) PutFile(Request)

        /// <summary>
        /// The body of the request offered as the file named in its path.
        /// </summary>
        private async Task<HTTPResponse> PutFile(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(DownloadServerAccess.Files), true, out var user, out var refused))
                return refused;

            var (area, name) = AreaAndName(Request);

            using var body = new MemoryStream(Request.HTTPBody ?? [], writable: false);

            #region One part of a file published in parts: ?offset=<byte>[&final=true]

            if (Request.QueryString.GetString("offset") is String offsetText)
            {

                if (!Int64.TryParse(offsetText, NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
                    return ErrorJSON(Request, HTTPStatusCode.BadRequest, $"'offset' is no number of bytes: '{offsetText}'.");

                var final = String.Equals(Request.QueryString.GetString("final"), "true", StringComparison.OrdinalIgnoreCase);

                var (published, received, notTaken, atAnotherOffset) = await DownloadServer.PublishPartAsync(area, name, offset, body, final, user.Id.ToString());

                if (atAnotherOffset)
                    return JSONResponse(Request, HTTPStatusCode.Conflict, new JObject(
                               new JProperty("error",     notTaken),
                               new JProperty("received",  received)
                           ));

                if (notTaken is not null)
                    return ErrorJSON(Request,
                                     DownloadServer.HasArea(area) ? HTTPStatusCode.BadRequest : HTTPStatusCode.NotFound,
                                     notTaken);

                return JSONResponse(Request, HTTPStatusCode.OK, published is not null
                                                                    ? new JObject(
                                                                          new JProperty("said",      $"'{published.Path}' is offered: {DownloadServerNode.SizeOf(published.Size)}."),
                                                                          new JProperty("received",  received),
                                                                          new JProperty("file",      published.ToJSON())
                                                                      )
                                                                    : new JObject(
                                                                          new JProperty("received",  received)
                                                                      ));

            }

            #endregion

            var (file, problem) = await DownloadServer.PublishAsync(area, name, body, user.Id.ToString());

            if (file is null)
                return ErrorJSON(Request,
                                 DownloadServer.HasArea(area) ? HTTPStatusCode.BadRequest : HTTPStatusCode.NotFound,
                                 problem ?? "Nothing was published.");

            return JSONResponse(Request, HTTPStatusCode.OK, new JObject(
                       new JProperty("said",  $"'{file.Path}' is offered: {DownloadServerNode.SizeOf(file.Size)}."),
                       new JProperty("file",  file.ToJSON())
                   ));

        }

        #endregion

        #region (private) PostRemoveFile(Request)

        private Task<HTTPResponse> PostRemoveFile(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(DownloadServerAccess.Files), true, out var user, out var refused))
                return Task.FromResult(refused);

            var (area, name) = AreaAndName(Request);

            if (!DownloadServer.RemoveFile(area, name, user.Id.ToString(), out var problem))
                return Task.FromResult(ErrorJSON(Request,
                                                 problem.StartsWith("There is no", StringComparison.Ordinal) ? HTTPStatusCode.NotFound : HTTPStatusCode.InternalServerError,
                                                 problem));

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, new JObject(
                       new JProperty("said", $"'/{area}/{name}' is offered no more. What was downloaded of it stays recorded.")
                   )));

        }

        #endregion

        #region (private) GetDownloads(Request)

        /// <summary>
        /// The downloads asked for, how many there are in all, and every
        /// downloader and file there is - for the filters to offer.
        /// </summary>
        private Task<HTTPResponse> GetDownloads(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(DownloadServerAccess.Downloads), false, out _, out var refused))
                return Task.FromResult(refused);

            var query = Request.QueryString;

            DateTimeOffset? since = null;
            DateTimeOffset? until = null;

            if (query.GetString("since") is { Length: > 0 } sinceText)
            {
                if (!DateTimeOffset.TryParse(sinceText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                    return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, $"'since' is no point in time: '{sinceText}'."));
                since = parsed;
            }

            if (query.GetString("until") is { Length: > 0 } untilText)
            {
                if (!DateTimeOffset.TryParse(untilText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                    return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, $"'until' is no point in time: '{untilText}'."));
                until = parsed;
            }

            var order = query.GetString("order") ?? "newest";

            if (order is not ("newest" or "oldest"))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, $"'order' is \"newest\" or \"oldest\", not '{order}'."));

            var downloads = DownloadServer.Downloads.Query(new DownloadQuery(
                                                               Downloader:  query.GetString("downloader"),
                                                               Area:        query.GetString("area"),
                                                               FileName:    query.GetString("file"),
                                                               Since:       since,
                                                               Until:       until,
                                                               Newest:      order == "newest",
                                                               Skip:        Math.Max(0, query.GetInt32("skip") ?? 0),
                                                               Take:        Math.Clamp(query.GetInt32("take") ?? DownloadStore.DefaultPageSize, 1, DownloadStore.MaxPageSize)
                                                           ),
                                                           out var total);

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, new JObject(
                       new JProperty("total",        total),
                       new JProperty("downloads",    new JArray(downloads.Select(download => download.ToJSON()))),
                       new JProperty("downloaders",  new JArray(DownloadServer.Downloads.Downloaders.Select(one => one.Name))),
                       new JProperty("files",        new JArray(DownloadServer.Downloads.Files.      Select(one => one.Path)))
                   )));

        }

        #endregion


        #region (private) GetDownloaders(Request)

        private Task<HTTPResponse> GetDownloaders(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(DownloadServerAccess.Downloaders), false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, DownloadServer.DownloadersJSON()));

        }

        #endregion

        #region (private) PostDownloader(Request)

        private async Task<HTTPResponse> PostDownloader(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(DownloadServerAccess.Downloaders), true, out var user, out var refused))
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var badRequest))
                return badRequest;

            if (!TryText(json, "id",   true,  out var id,   out var problem) ||
                !TryText(json, "name", false, out var name, out problem))
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, problem);

            return Answer(Request, await DownloadServer.AddDownloader(id!, name, user.Id.ToString()));

        }

        #endregion

        #region (private) PostPassword(Request)

        private async Task<HTTPResponse> PostPassword(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(DownloadServerAccess.Downloaders), true, out var user, out var refused))
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var badRequest))
                return badRequest;

            if (!TryText(json, "id", true, out var id, out var problem))
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, problem);

            return Answer(Request, await DownloadServer.NewDownloaderPassword(id!, user.Id.ToString()));

        }

        #endregion

        #region (private) PostRemoveDownloader(Request)

        private async Task<HTTPResponse> PostRemoveDownloader(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(DownloadServerAccess.Downloaders), true, out var user, out var refused))
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var badRequest))
                return badRequest;

            if (!TryText(json, "id", true, out var id, out var problem))
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, problem);

            return Answer(Request, await DownloadServer.RemoveDownloader(id!, user.Id.ToString()));

        }

        #endregion


        #region (private) Answer(Request, Change)

        /// <summary>
        /// What a change of the downloaders came to: what it said, the
        /// password where one was made, and the downloaders as they are now -
        /// or why nothing changed, with the status that says whose fault.
        /// </summary>
        private HTTPResponse Answer(HTTPRequest       Request,
                                    DownloaderChange  Change)

            => Change.Outcome switch {

                   DownloaderChangeOutcome.Changed   => JSONResponse(Request, HTTPStatusCode.OK, new JObject(
                                                            new JProperty("said",         Change.Said),
                                                            new JProperty("downloader",   Change.Downloader),
                                                            new JProperty("password",     Change.Password),
                                                            new JProperty("downloaders",  DownloadServer.DownloadersJSON())
                                                        )),

                   DownloaderChangeOutcome.NotFound  => ErrorJSON(Request, HTTPStatusCode.NotFound,            Change.Said),
                   DownloaderChangeOutcome.NotSaved  => ErrorJSON(Request, HTTPStatusCode.InternalServerError, Change.Said),
                   _                                 => ErrorJSON(Request, HTTPStatusCode.BadRequest,          Change.Said)

               };

        #endregion

        #region (private static) AreaAndName(Request)

        /// <summary>
        /// The area and the name of a file, as the path of the request names them.
        /// </summary>
        private static (String Area, String Name) AreaAndName(HTTPRequest Request)

            => (Request.ParsedURLParameters.Length > 0 ? Uri.UnescapeDataString(Request.ParsedURLParameters[0]).Trim() : "",
                Request.ParsedURLParameters.Length > 1 ? Uri.UnescapeDataString(Request.ParsedURLParameters[1])        : "");

        #endregion

        #region (private) TryGetFile(Request, out File, out Path, out NotFound)

        private Boolean TryGetFile(HTTPRequest                             Request,
                                   [NotNullWhen(true)]  out OfferedFile?  File,
                                   [NotNullWhen(true)]  out String?       Path,
                                   [NotNullWhen(false)] out HTTPResponse? NotFound)
        {

            NotFound = null;

            var (area, name) = AreaAndName(Request);

            if (!DownloadServer.TryGetFile(area, name, out File, out Path))
            {
                NotFound = ErrorJSON(Request, HTTPStatusCode.NotFound, $"There is no file '/{area}/{name}'.");
                return false;
            }

            return true;

        }

        #endregion

        #region (private static) TryText(JSON, Key, Required, out Text, out Problem)

        private static Boolean TryText(JObject                           JSON,
                                       String                            Key,
                                       Boolean                           Required,
                                       out String?                       Text,
                                       [NotNullWhen(false)] out String?  Problem)
        {

            Text     = null;
            Problem  = null;

            if (JSON[Key] is not JToken token || token.Type == JTokenType.Null ||
                (token.Type == JTokenType.String && String.IsNullOrWhiteSpace(token.Value<String>()) && !Required))
            {

                if (!Required)
                    return true;

                Problem = $"\"{Key}\" is missing.";
                return false;

            }

            if (token.Type != JTokenType.String || String.IsNullOrWhiteSpace(token.Value<String>()))
            {
                Problem = $"\"{Key}\" is text, and not empty.";
                return false;
            }

            Text = token.Value<String>()!.Trim();
            return true;

        }

        #endregion

    }

}
