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
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;

using Newtonsoft.Json.Linq;

using cloud.charging.open.protocols.WWCP.Node.SecureShell;

#endregion

namespace cloud.charging.open.DownloadServer
{

    /// <summary>
    /// One file offered: where it lies, how large it is, since when it is as
    /// it is, and its SHA-256.
    /// </summary>
    /// <param name="Area">The area it lies in.</param>
    /// <param name="Name">Its name there.</param>
    /// <param name="Size">Its size in bytes.</param>
    /// <param name="Modified">When it was last written.</param>
    /// <param name="SHA256">Its SHA-256, in lower-case hex.</param>
    public sealed record OfferedFile(String          Area,
                                     String          Name,
                                     Int64           Size,
                                     DateTimeOffset  Modified,
                                     String          SHA256)
    {

        /// <summary>
        /// The path it is fetched by: "/firmware/station-4.2.1.bin".
        /// </summary>
        public String Path
            => $"/{Area}/{Name}";

        /// <summary>
        /// The file as the JSON API writes it.
        /// </summary>
        public JObject ToJSON()

            => new (
                   new JProperty("area",      Area),
                   new JProperty("name",      Name),
                   new JProperty("path",      Path),
                   new JProperty("size",      Size),
                   new JProperty("modified",  Modified.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)),
                   new JProperty("sha256",    SHA256)
               );

    }


    /// <summary>
    /// The files a download server offers: one directory an area below the
    /// files directory, and in it every file whose name is one a file may
    /// have and does not begin with a dot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The disk is what is offered: a file put in an area's directory by hand
    /// is offered from then on, as one published over SFTP or the web
    /// interface is, and one taken away is offered no more. Nothing else keeps
    /// a list of them.
    /// </para>
    /// <para>
    /// A file is published under a hidden name and moved into place once it
    /// is complete, so that nobody downloads half of it - and one taking the
    /// place of another of its name, so that nobody downloads half of each.
    /// </para>
    /// </remarks>
    public partial class DownloadServerNode
    {

        #region Data

        /// <summary>
        /// What the hidden name of a file being published through the JSON
        /// API begins with: never offered, and taken away at the next start
        /// where a publication never finished.
        /// </summary>
        public const String  PublishPrefix  = ".publish-";

        /// <summary>
        /// The SHA-256 of each file as it was when it was last read - its
        /// length and its time of writing with it, so that a file written
        /// since is read again.
        /// </summary>
        private readonly ConcurrentDictionary<String, (Int64 Size, DateTime Written, String SHA256)>  hashes  = new (StringComparer.Ordinal);

        #endregion

        #region Events

        /// <summary>
        /// Raised after a file was published: over SFTP, or through the web
        /// interface - with who did it.
        /// </summary>
        public event Action<OfferedFile, String>?   OnPublished;

        /// <summary>
        /// Raised after a file was taken away: its area, its name, who did it.
        /// </summary>
        public event Action<String, String, String>?  OnRemoved;

        #endregion


        #region (static) IsFileName(Name)

        /// <summary>
        /// Whether the given name is one a file offered may have: a name a
        /// file of an SFTP area may have, and none that begins with a dot -
        /// which is what a file being published, or anything else not to be
        /// offered, is called.
        /// </summary>
        public static Boolean IsFileName(String? Name)

            => SftpArea.IsFileName(Name) &&
               !Name!.StartsWith('.');

        #endregion

        #region HasArea(Area) / DirectoryOf(Area)

        /// <summary>
        /// Whether the given text names an area of this download server.
        /// </summary>
        public Boolean HasArea(String? Area)

            => Area is not null &&
               Settings.Areas.Contains(Area, StringComparer.Ordinal);

        /// <summary>
        /// The directory of the given area.
        /// </summary>
        public String DirectoryOf(String Area)

            => Path.Combine(FilesDirectory, Area);

        #endregion

        #region Files(Area)

        /// <summary>
        /// The files offered - in the given area, or in every one where it is
        /// null -, by area and name.
        /// </summary>
        public IReadOnlyList<OfferedFile> Files(String? Area)
        {

            var files = new List<OfferedFile>();

            foreach (var area in Settings.Areas)
            {

                if (Area is not null && area != Area)
                    continue;

                var directory = DirectoryOf(area);

                if (!Directory.Exists(directory))
                    continue;

                foreach (var path in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
                {

                    var name = Path.GetFileName(path);

                    if (IsFileName(name) && Described(area, name, path) is OfferedFile file)
                        files.Add(file);

                }

            }

            return files;

        }

        #endregion

        #region TryGetFile(Area, Name, out File, out Path)

        /// <summary>
        /// The file of the given name in the given area, and where it lies -
        /// where there is one, and it is one that is offered.
        /// </summary>
        public Boolean TryGetFile(String                                  Area,
                                  String                                  Name,
                                  [NotNullWhen(true)] out OfferedFile?   File,
                                  [NotNullWhen(true)] out String?        Path)
        {

            File  = null;
            Path  = null;

            if (!HasArea(Area) || !IsFileName(Name))
                return false;

            var path = PathOf(Area, Name);

            if (!System.IO.File.Exists(path) || Described(Area, Name, path) is not OfferedFile file)
                return false;

            File  = file;
            Path  = path;

            return true;

        }

        #endregion

        #region PublishAsync(Area, Name, Content, By, CancellationToken)

        /// <summary>
        /// Offer what the given stream holds as the file of the given name in
        /// the given area - in the place of the one of that name, where there
        /// is one: written under a hidden name, and moved into place once it is
        /// complete.
        /// </summary>
        /// <returns>The file offered, or why nothing is.</returns>
        public async Task<(OfferedFile? File, String? Refused)> PublishAsync(String             Area,
                                                                             String             Name,
                                                                             Stream             Content,
                                                                             String             By,
                                                                             CancellationToken  CancellationToken = default)
        {

            if (!HasArea(Area))
                return (null, $"There is no area '{Area}'. The areas are {String.Join(", ", Settings.Areas.Select(area => $"'{area}'"))}.");

            if (!IsFileName(Name))
                return (null, $"'{Name}' is no name a file offered can have: one part, without '/', '\\' or ':', and not beginning with a dot.");

            var directory  = DirectoryOf(Area);
            var hidden     = System.IO.Path.Combine(directory, $"{PublishPrefix}{Guid.NewGuid():N}");
            var target     = PathOf(Area, Name);

            Directory.CreateDirectory(directory);

            try
            {

                Int64 written = 0;

                await using (var file = new FileStream(hidden, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                {

                    var buffer = new Byte[81920];
                    Int32 read;

                    while ((read = await Content.ReadAsync(buffer, CancellationToken)) > 0)
                    {

                        written += read;

                        if (written > Settings.MaxFileSize)
                            return (null, $"'{Name}' is larger than the {SizeOf(Settings.MaxFileSize)} this download server takes.");

                        await file.WriteAsync(buffer.AsMemory(0, read), CancellationToken);

                    }

                }

                if (written == 0)
                    return (null, $"'{Name}' is empty: there is nothing to offer.");

                File.Move(hidden, target, overwrite: true);

            }
            finally
            {
                try { File.Delete(hidden); } catch (Exception) { }
            }

            if (!TryGetFile(Area, Name, out var published, out _))
                return (null, $"'/{Area}/{Name}' was written, but cannot be read back.");

            Log.Notice($"'{By}' published '{published.Path}': {SizeOf(published.Size)}, SHA-256 {published.SHA256}.", "downloadserver", "files");

            RaisePublished(published, By);

            return (published, null);

        }

        #endregion

        #region PublishPartAsync(Area, Name, Offset, Content, Final, By, CancellationToken)

        /// <summary>
        /// One part of a file published in parts - as the web interface sends a
        /// file larger than its HTTP server takes in one request: appended at
        /// the given offset to what the parts before brought, under a hidden
        /// name of the one who publishes and the name of the file; the last
        /// part, Final, puts the file in place, in the place of the one of that
        /// name.
        /// </summary>
        /// <remarks>
        /// A part at offset 0 begins anew. A part at another offset than the
        /// length of what is there is refused, and says that length - for the
        /// one who sends to go on from there.
        /// </remarks>
        /// <returns>The file, once the last part put it in place; how many bytes are there; or why nothing is.</returns>
        public async Task<(OfferedFile? File, Int64 Received, String? Refused, Boolean AtAnotherOffset)> PublishPartAsync(String             Area,
                                                                                                                         String             Name,
                                                                                                                         Int64              Offset,
                                                                                                                         Stream             Content,
                                                                                                                         Boolean            Final,
                                                                                                                         String             By,
                                                                                                                         CancellationToken  CancellationToken = default)
        {

            if (!HasArea(Area))
                return (null, 0, $"There is no area '{Area}'. The areas are {String.Join(", ", Settings.Areas.Select(area => $"'{area}'"))}.", false);

            if (!IsFileName(Name))
                return (null, 0, $"'{Name}' is no name a file offered can have: one part, without '/', '\\' or ':', and not beginning with a dot.", false);

            var directory  = DirectoryOf(Area);
            var hidden     = System.IO.Path.Combine(directory, $"{PublishPrefix}{Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{By}\n{Name}")))[..32]}");

            Directory.CreateDirectory(directory);

            Int64 received;

            await using (var file = new FileStream(hidden, Offset == 0 ? FileMode.Create : FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {

                if (file.Length != Offset)
                    return (null, file.Length, $"What the parts before brought of '{Name}' ends at byte {file.Length}, and not at {Offset}: go on from {file.Length}, or begin anew at 0.", true);

                file.Seek(0, SeekOrigin.End);

                var buffer = new Byte[81920];
                Int32 read;

                received = Offset;

                while ((read = await Content.ReadAsync(buffer, CancellationToken)) > 0)
                {

                    received += read;

                    if (received > Settings.MaxFileSize)
                    {
                        file.SetLength(0);
                        return (null, 0, $"'{Name}' is larger than the {SizeOf(Settings.MaxFileSize)} this download server takes.", false);
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), CancellationToken);

                }

            }

            if (!Final)
                return (null, received, null, false);

            if (received == 0)
            {
                try { File.Delete(hidden); } catch (Exception) { }
                return (null, 0, $"'{Name}' is empty: there is nothing to offer.", false);
            }

            File.Move(hidden, PathOf(Area, Name), overwrite: true);

            if (!TryGetFile(Area, Name, out var published, out _))
                return (null, received, $"'/{Area}/{Name}' was written, but cannot be read back.", false);

            Log.Notice($"'{By}' published '{published.Path}' in parts: {SizeOf(published.Size)}, SHA-256 {published.SHA256}.", "downloadserver", "files");

            RaisePublished(published, By);

            return (published, received, null, false);

        }

        #endregion

        #region RemoveFile(Area, Name, By, out Refused)

        /// <summary>
        /// Offer the file of the given name in the given area no more: taken
        /// away from the disk. What was downloaded of it stays recorded.
        /// </summary>
        public Boolean RemoveFile(String                             Area,
                                  String                             Name,
                                  String                             By,
                                  [NotNullWhen(false)] out String?  Refused)
        {

            Refused = null;

            if (!TryGetFile(Area, Name, out var file, out var path))
            {
                Refused = $"There is no file '/{Area}/{Name}'.";
                return false;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
            {
                Refused = $"'{file.Path}' could not be taken away: {problem.Message}";
                return false;
            }

            hashes.TryRemove(path, out _);

            Log.Notice($"'{By}' took '{file.Path}' away.", "downloadserver", "files");

            RaiseRemoved(Area, Name, By);

            return true;

        }

        #endregion


        #region (private) PathOf(Area, Name)

        /// <summary>
        /// Where the file of the given name in the given area lies - in the
        /// area's directory and nowhere else.
        /// </summary>
        private String PathOf(String Area, String Name)
        {

            var directory  = DirectoryOf(Area);
            var path       = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, Name));

            if (!String.Equals(System.IO.Path.GetDirectoryName(path), System.IO.Path.GetFullPath(directory), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException($"'{Name}' would lie outside the area '{Area}'.");

            return path;

        }

        #endregion

        #region (private) Described(Area, Name, Path)

        /// <summary>
        /// What is known of the file at the given path - its SHA-256 read
        /// again only where it was written since it was last read -, or null
        /// where it went away meanwhile.
        /// </summary>
        private OfferedFile? Described(String Area, String Name, String Path)
        {

            try
            {

                var info     = new FileInfo(Path);

                if (!info.Exists)
                    return null;

                var size     = info.Length;
                var written  = info.LastWriteTimeUtc;

                if (!hashes.TryGetValue(Path, out var known) || known.Size != size || known.Written != written)
                {

                    using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                    known = (size, written, Convert.ToHexStringLower(SHA256.HashData(stream)));

                    hashes[Path] = known;

                }

                return new OfferedFile(Area, Name, size, new DateTimeOffset(written, TimeSpan.Zero), known.SHA256);

            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
            {
                return null;
            }

        }

        #endregion

        #region (private) ForgetUnfinishedPublications()

        /// <summary>
        /// What a publication that never finished left under its hidden name -
        /// the node stopped while it was written - taken away at the start.
        /// </summary>
        private void ForgetUnfinishedPublications()
        {

            foreach (var area in Settings.Areas)
            {

                var directory = DirectoryOf(area);

                if (!Directory.Exists(directory))
                    continue;

                foreach (var left in Directory.EnumerateFiles(directory, PublishPrefix + "*"))
                {
                    try
                    {
                        File.Delete(left);
                    }
                    catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
                    {
                        Log.Warning($"What an unfinished publication left in '{left}' could not be taken away: {problem.Message}", "downloadserver", "files");
                    }
                }

            }

        }

        #endregion

        #region (private) RaisePublished(File, By) / RaiseRemoved(Area, Name, By)

        private void RaisePublished(OfferedFile File, String By)
        {
            foreach (var listener in OnPublished?.GetInvocationList().Cast<Action<OfferedFile, String>>() ?? [])
            {
                try
                {
                    listener(File, By);
                }
                catch (Exception problem)
                {
                    Log.Warning($"Whoever listens to the files failed over '{File.Path}': {problem.Message}", "downloadserver", "files");
                }
            }
        }

        private void RaiseRemoved(String Area, String Name, String By)
        {
            foreach (var listener in OnRemoved?.GetInvocationList().Cast<Action<String, String, String>>() ?? [])
            {
                try
                {
                    listener(Area, Name, By);
                }
                catch (Exception problem)
                {
                    Log.Warning($"Whoever listens to the files failed over '/{Area}/{Name}': {problem.Message}", "downloadserver", "files");
                }
            }
        }

        #endregion

    }

}
