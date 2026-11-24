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

#endregion

namespace cloud.charging.open.DownloadServer
{

    /// <summary>
    /// How a file was fetched.
    /// </summary>
    public enum DownloadProtocol
    {

        /// <summary>
        /// By HTTP GET, on the download port.
        /// </summary>
        HTTP,

        /// <summary>
        /// Over SFTP, on the node's SSH server.
        /// </summary>
        SFTP

    }


    /// <summary>
    /// One file - or the part of one - a charging station, or whatever
    /// downloads, fetched from this download server, and how.
    /// </summary>
    /// <param name="Id">The number of the download: one above the one before, never reused.</param>
    /// <param name="Timestamp">When it was answered - by HTTP - or closed - over SFTP.</param>
    /// <param name="Protocol">How it was fetched.</param>
    /// <param name="Downloader">The account it was fetched under, or "anonymous".</param>
    /// <param name="Area">The area of the file.</param>
    /// <param name="FileName">The name of the file in it.</param>
    /// <param name="Status">200 for the file from its first byte on, 206 for the rest of it from a later one - as HTTP answered, and as SFTP read.</param>
    /// <param name="FirstByte">The first byte of the file sent: 0, or where a download that takes up where another stopped begins.</param>
    /// <param name="Bytes">How many bytes were sent - by HTTP, those the answer carries.</param>
    /// <param name="Size">How large the file was.</param>
    /// <param name="RemoteAddress">Where it was fetched from: the address and port of the client.</param>
    /// <param name="UserAgent">The User-Agent an HTTP download said, or null.</param>
    public sealed record DownloadEvent(UInt64            Id,
                                       DateTimeOffset    Timestamp,
                                       DownloadProtocol  Protocol,
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

        #region Properties

        /// <summary>
        /// The path the file is fetched by: "/firmware/station-4.2.1.bin".
        /// </summary>
        public String   Path
            => $"/{Area}/{FileName}";

        /// <summary>
        /// Whether what was sent reaches the end of the file - by itself, or
        /// after what an attempt before brought.
        /// </summary>
        public Boolean  ToTheEnd
            => FirstByte + Bytes >= Size;

        #endregion

        #region ToJSON()

        /// <summary>
        /// The download as the record of the downloads and the JSON API write it.
        /// </summary>
        public JObject ToJSON()
        {

            var json = new JObject(
                           new JProperty("id",             Id),
                           new JProperty("timestamp",      Timestamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)),
                           new JProperty("protocol",       Protocol.ToString()),
                           new JProperty("downloader",     Downloader),
                           new JProperty("area",           Area),
                           new JProperty("fileName",       FileName),
                           new JProperty("status",         Status),
                           new JProperty("firstByte",      FirstByte),
                           new JProperty("bytes",          Bytes),
                           new JProperty("size",           Size),
                           new JProperty("toTheEnd",       ToTheEnd),
                           new JProperty("remoteAddress",  RemoteAddress)
                       );

            if (UserAgent is not null)
                json.Add(new JProperty("userAgent", UserAgent));

            return json;

        }

        #endregion

        #region (static) TryParse(JSON, out Download)

        /// <summary>
        /// One line of the record read back.
        /// </summary>
        public static Boolean TryParse(JObject                                  JSON,
                                       [NotNullWhen(true)] out DownloadEvent?  Download)
        {

            Download = null;

            try
            {

                if (JSON["id"]?.Type != JTokenType.Integer ||
                    !DateTimeOffset.TryParse(JSON["timestamp"]?.Value<String>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) ||
                    !Enum.TryParse<DownloadProtocol>(JSON["protocol"]?.Value<String>(), true, out var protocol) ||
                    JSON["downloader"]?.Value<String>() is not String downloader ||
                    JSON["area"]      ?.Value<String>() is not String area       ||
                    JSON["fileName"]  ?.Value<String>() is not String fileName   ||
                    JSON["status"]   ?.Type != JTokenType.Integer                ||
                    JSON["firstByte"]?.Type != JTokenType.Integer                ||
                    JSON["bytes"]    ?.Type != JTokenType.Integer                ||
                    JSON["size"]     ?.Type != JTokenType.Integer)
                {
                    return false;
                }

                Download = new DownloadEvent(
                               JSON["id"]!.Value<UInt64>(),
                               timestamp,
                               protocol,
                               downloader,
                               area,
                               fileName,
                               JSON["status"]!.   Value<Int32>(),
                               JSON["firstByte"]!.Value<Int64>(),
                               JSON["bytes"]!.    Value<Int64>(),
                               JSON["size"]!.     Value<Int64>(),
                               JSON["remoteAddress"]?.Value<String>() ?? "",
                               JSON["userAgent"]    ?.Value<String>()
                           );

                return true;

            }
            catch (Exception)
            {
                return false;
            }

        }

        #endregion

    }

}
