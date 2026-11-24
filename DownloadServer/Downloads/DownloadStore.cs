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

using System.Text;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

#endregion

namespace cloud.charging.open.DownloadServer
{

    /// <summary>
    /// Which downloads a query asks for, and in which order.
    /// </summary>
    /// <param name="Downloader">Only those of this account, or of every one where null.</param>
    /// <param name="Area">Only those of this area, or of every one where null.</param>
    /// <param name="FileName">Only those of a file of this name, or of every one where null.</param>
    /// <param name="Since">Only those at or after this time.</param>
    /// <param name="Until">Only those before this time.</param>
    /// <param name="Newest">The newest first, or the oldest.</param>
    /// <param name="Skip">How many of those to leave out, from the first in that order.</param>
    /// <param name="Take">How many to bring back at most.</param>
    public sealed record DownloadQuery(String?          Downloader  = null,
                                       String?          Area        = null,
                                       String?          FileName    = null,
                                       DateTimeOffset?  Since       = null,
                                       DateTimeOffset?  Until       = null,
                                       Boolean          Newest      = true,
                                       Int32            Skip        = 0,
                                       Int32            Take        = DownloadStore.DefaultPageSize);


    /// <summary>
    /// The record of what was downloaded from a download server: one line a
    /// download in a file beside the areas - appended as it happens, read back
    /// at every start.
    /// </summary>
    /// <remarks>
    /// The record is JSON, one download a line. A line that cannot be read is
    /// said and passed over, rather than refusing to start.
    /// </remarks>
    public sealed class DownloadStore
    {

        #region Data

        /// <summary>
        /// The name of the record beside the areas.
        /// </summary>
        public const String  RecordFileName   = "downloads.jsonl";

        /// <summary>
        /// How many downloads a query brings back unless it says.
        /// </summary>
        public const Int32   DefaultPageSize  = 500;

        /// <summary>
        /// The most downloads one query may bring back.
        /// </summary>
        public const Int32   MaxPageSize      = 5_000;

        private readonly Lock                 storeLock  = new ();
        private readonly List<DownloadEvent>  downloads  = [];
        private readonly TimeProvider         timeProvider;
        private readonly Action<String>       warn;

        /// <summary>
        /// UTF-8 without a byte order mark: the record is read a line at a
        /// time, and a mark in front of the first would make it no JSON.
        /// </summary>
        private static readonly Encoding UTF8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private UInt64 lastId;

        #endregion

        #region Properties

        /// <summary>
        /// The directory the record lies in, given in full.
        /// </summary>
        public String  Directory    { get; }

        /// <summary>
        /// The record file.
        /// </summary>
        public String  RecordFile
            => Path.Combine(Directory, RecordFileName);

        /// <summary>
        /// How many downloads are recorded.
        /// </summary>
        public Int32   Count
        {
            get
            {
                lock (storeLock)
                    return downloads.Count;
            }
        }

        /// <summary>
        /// How many bytes they sent together.
        /// </summary>
        public Int64   TotalBytes
        {
            get
            {
                lock (storeLock)
                    return downloads.Sum(download => download.Bytes);
            }
        }

        /// <summary>
        /// The newest download, or null.
        /// </summary>
        public DownloadEvent? Latest
        {
            get
            {
                lock (storeLock)
                    return downloads.Count > 0 ? downloads[^1] : null;
            }
        }

        /// <summary>
        /// Every download, the oldest first.
        /// </summary>
        public IReadOnlyList<DownloadEvent> All
        {
            get
            {
                lock (storeLock)
                    return [.. downloads];
            }
        }

        #endregion

        #region Events

        /// <summary>
        /// Raised after a download was recorded.
        /// </summary>
        public event Action<DownloadEvent>? OnDownloaded;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// The downloads recorded in the given directory.
        /// </summary>
        /// <param name="Directory">The directory, given in full; made where it is not there.</param>
        /// <param name="TimeProvider">What the time is.</param>
        /// <param name="Warn">Where what is wrong with the record is said.</param>
        public DownloadStore(String           Directory,
                             TimeProvider?    TimeProvider  = null,
                             Action<String>?  Warn          = null)
        {

            if (String.IsNullOrWhiteSpace(Directory) || !Path.IsPathFullyQualified(Directory))
                throw new ArgumentException($"The downloads need a directory given in full, not '{Directory}'.", nameof(Directory));

            this.Directory     = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Directory));
            this.timeProvider  = TimeProvider ?? System.TimeProvider.System;
            this.warn          = Warn         ?? (_ => { });

        }

        #endregion


        #region Load()

        /// <summary>
        /// Read the record; how many downloads it holds.
        /// </summary>
        public Int32 Load()
        {

            System.IO.Directory.CreateDirectory(Directory);

            lock (storeLock)
            {

                downloads.Clear();
                lastId = 0;

                if (!File.Exists(RecordFile))
                    return 0;

                var lineNumber = 0;

                foreach (var line in File.ReadLines(RecordFile, UTF8))
                {

                    lineNumber++;

                    if (String.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {

                        if (ParseLine(line.TrimStart((Char) 0xFEFF)) is JObject json &&
                            DownloadEvent.TryParse(json, out var download))
                        {
                            downloads.Add(download);
                            lastId = Math.Max(lastId, download.Id);
                            continue;
                        }

                    }
                    catch (JsonException)
                    { }

                    warn($"Line {lineNumber} of '{RecordFile}' is no download, and was passed over.");

                }

                downloads.Sort((a, b) => a.Timestamp != b.Timestamp
                                             ? a.Timestamp.CompareTo(b.Timestamp)
                                             : a.Id.CompareTo(b.Id));

                return downloads.Count;

            }

        }

        #endregion

        #region Record(...)

        /// <summary>
        /// Write a download down.
        /// </summary>
        public DownloadEvent Record(DownloadProtocol  Protocol,
                                    String            Downloader,
                                    String            Area,
                                    String            FileName,
                                    Int32             Status,
                                    Int64             FirstByte,
                                    Int64             Bytes,
                                    Int64             Size,
                                    String            RemoteAddress,
                                    String?           UserAgent  = null)
        {

            DownloadEvent download;

            lock (storeLock)
            {

                download = new DownloadEvent(++lastId,
                                             timeProvider.GetUtcNow(),
                                             Protocol,
                                             Downloader,
                                             Area,
                                             FileName,
                                             Status,
                                             FirstByte,
                                             Bytes,
                                             Size,
                                             RemoteAddress,
                                             UserAgent);

                try
                {
                    File.AppendAllText(RecordFile, download.ToJSON().ToString(Formatting.None) + "\n", UTF8);
                }
                catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
                {
                    // A download that happened is not undone because it could
                    // not be written down: it is kept until the next start.
                    warn($"Download {download.Id} could not be written to '{RecordFile}': {problem.Message}");
                }

                downloads.Add(download);

            }

            foreach (var listener in OnDownloaded?.GetInvocationList().Cast<Action<DownloadEvent>>() ?? [])
            {
                try
                {
                    listener(download);
                }
                catch (Exception problem)
                {
                    warn($"Whoever listens to the downloads failed over download {download.Id}: {problem.Message}");
                }
            }

            return download;

        }

        #endregion

        #region Query(Query, out Total)

        /// <summary>
        /// The downloads a query asks for, and how many there are of them in all.
        /// </summary>
        public IReadOnlyList<DownloadEvent> Query(DownloadQuery  Query,
                                                  out Int32      Total)
        {

            IEnumerable<DownloadEvent> matching = All;

            if (Query.Downloader is { Length: > 0 } downloader)
                matching = matching.Where(download => String.Equals(download.Downloader, downloader, StringComparison.OrdinalIgnoreCase));

            if (Query.Area is { Length: > 0 } area)
                matching = matching.Where(download => String.Equals(download.Area,       area,       StringComparison.Ordinal));

            if (Query.FileName is { Length: > 0 } fileName)
                matching = matching.Where(download => String.Equals(download.FileName,   fileName,   StringComparison.Ordinal));

            if (Query.Since is DateTimeOffset since)
                matching = matching.Where(download => download.Timestamp >= since);

            if (Query.Until is DateTimeOffset until)
                matching = matching.Where(download => download.Timestamp <  until);

            var list = matching.ToList();

            Total = list.Count;

            if (Query.Newest)
                list.Reverse();

            return [.. list.Skip(Math.Max(0, Query.Skip)).
                            Take(Math.Clamp(Query.Take, 0, MaxPageSize))];

        }

        #endregion

        #region Downloaders / Files

        /// <summary>
        /// Every account that downloaded, with how many times and when last.
        /// </summary>
        public IReadOnlyList<(String Name, Int32 Downloads, DateTimeOffset Last)> Downloaders

            => [.. All.GroupBy (download => download.Downloader, StringComparer.OrdinalIgnoreCase).
                       Select  (group    => (group.Key, group.Count(), group.Max(download => download.Timestamp))).
                       OrderBy (one      => one.Key, StringComparer.OrdinalIgnoreCase)];

        /// <summary>
        /// Every file that was downloaded, by its path, with how many times and
        /// when last.
        /// </summary>
        public IReadOnlyList<(String Path, Int32 Downloads, DateTimeOffset Last)> Files

            => [.. All.GroupBy (download => download.Path, StringComparer.Ordinal).
                       Select  (group    => (group.Key, group.Count(), group.Max(download => download.Timestamp))).
                       OrderBy (one      => one.Key, StringComparer.Ordinal)];

        #endregion


        #region (private static) ParseLine(Line)

        /// <summary>
        /// One line of the record as JSON - its times as the text they were
        /// written as: Newtonsoft would otherwise read them as dates, and give
        /// them back as text without their milliseconds.
        /// </summary>
        private static JObject? ParseLine(String Line)
        {

            using var reader = new JsonTextReader(new StringReader(Line)) {
                                   DateParseHandling = DateParseHandling.None
                               };

            return JToken.ReadFrom(reader) as JObject;

        }

        #endregion

    }

}
