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

using System.Security.Cryptography;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.Mail;

#endregion

namespace cloud.charging.open.DownloadServer
{

    /// <summary>
    /// What a change of the downloaders came to.
    /// </summary>
    public enum DownloaderChangeOutcome
    {

        /// <summary>Made, given a new password, or no longer let download.</summary>
        Changed,

        /// <summary>What was asked for cannot be: a name that is none, one that is taken.</summary>
        Refused,

        /// <summary>There is no downloader of that name.</summary>
        NotFound,

        /// <summary>The accounts refused to write it.</summary>
        NotSaved

    }


    /// <summary>
    /// What a change of the downloaders came to, what it said, and the
    /// password of an account that was made or given a new one - shown once,
    /// and never written down.
    /// </summary>
    public sealed record DownloaderChange(DownloaderChangeOutcome  Outcome,
                                          String                   Said,
                                          String?                  Downloader  = null,
                                          String?                  Password    = null);


    /// <summary>
    /// The accounts files are downloaded under: accounts of the node, in the
    /// "downloader" role.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One a charging station or a local controller, as a rule, named as it
    /// is - so that the record of the downloads says which of them fetched
    /// what -; or one several share, where that matters less.
    /// </para>
    /// <para>
    /// The password of a downloader is made up here and shown once, as the
    /// node's first account's is: what the accounts keep is its hash. Over
    /// SFTP a downloader may sign in with it where the "ssh" section says
    /// "passwords": true, and with a public key of its own in every case -
    /// given to it as to any account, with 'sshKeys'.
    /// </para>
    /// <para>
    /// A downloader that is taken away keeps its account, and with it the
    /// record of what it downloaded: it is only no longer in the role, and so
    /// may download no more.
    /// </para>
    /// </remarks>
    public partial class DownloadServerNode
    {

        #region Data

        private const String  DownloaderPasswordCharacters  = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz123456789";

        private const Int32   DownloaderPasswordLength      = 24;

        #endregion


        #region DownloaderAccounts

        /// <summary>
        /// Every account in the downloader role.
        /// </summary>
        public IReadOnlyList<IUser> DownloaderAccounts

            => [.. ExtAPI.Users.Where  (IsDownloader).
                                OrderBy(user => user.Id.ToString(), StringComparer.OrdinalIgnoreCase)];

        #endregion

        #region IsDownloader(User)

        /// <summary>
        /// Whether the given account is in the downloader role.
        /// </summary>
        public Boolean IsDownloader(IUser User)

            => RolesOf(User).Any(role => role.Name == DownloadServerAccess.Downloader.Name);

        #endregion

        #region DownloadersJSON()

        /// <summary>
        /// Every downloader: its name, what it is called, how many downloads it
        /// made and when last.
        /// </summary>
        public JArray DownloadersJSON()
        {

            var downloaded = Downloads.Downloaders.ToDictionary(one => one.Name, StringComparer.OrdinalIgnoreCase);

            return new JArray(DownloaderAccounts.Select(user => {

                       var id = user.Id.ToString();

                       downloaded.TryGetValue(id, out var made);

                       return new JObject(
                                  new JProperty("id",            id),
                                  new JProperty("name",          user.Name.FirstText()),
                                  new JProperty("disabled",      user.IsDisabled),
                                  new JProperty("downloads",     made.Downloads),
                                  new JProperty("lastDownload",  made.Downloads > 0 ? made.Last.ToString("o") : null)
                              );

                   }));

        }

        #endregion


        #region AddDownloader(Id, Name, By)

        /// <summary>
        /// An account more that may download, with a password made up for it.
        /// </summary>
        /// <param name="Id">The name it signs in with - as a rule the charging station's or the local controller's.</param>
        /// <param name="Name">What it is called, or null for its name.</param>
        /// <param name="By">Who made it, for the log.</param>
        public async Task<DownloaderChange> AddDownloader(String   Id,
                                                          String?  Name,
                                                          String   By)
        {

            if (!User_Id.TryParse(Id.Trim(), out var userId) ||
                userId.ToString().Length is 0 or > 64 ||
                userId.ToString().Any(c => Char.IsWhiteSpace(c) || Char.IsControl(c) || c is '/' or '\\' or ':' or '@'))
            {
                return new DownloaderChange(DownloaderChangeOutcome.Refused, $"'{Id}' is no name a downloader can sign in with: letters, digits, '-', '_' and '.', without spaces.");
            }

            if (ExtAPI.TryGetUser(userId, out var existing) && existing is not null)
                return new DownloaderChange(DownloaderChangeOutcome.Refused,
                                            IsDownloader(existing)
                                                ? $"'{userId}' is a downloader already. A new password is made with 'password'."
                                                : $"There is an account '{userId}' already, and it is no downloader. Give the downloader another name.");

            // The node's, which its first account was made in. Made only where
            // it is not there: "if not exists" answers with nothing - and not
            // with the one there is - where it is.
            var organizationId = Organization_Id.Parse(Kind.Organization);

            if (!ExtAPI.TryGetOrganization(organizationId, out var organization))
                organization = await ExtAPI.CreateOrganizationIfNotExists(
                                         organizationId,
                                         I18NString.Create(Languages.en, Kind.Organization)
                                     );

            if (organization is not Organization nodeOrganization)
                return new DownloaderChange(DownloaderChangeOutcome.NotSaved, $"The organization of this {Kind.Name} could not be had, and an account outside one cannot sign in.");

            var password = RandomNumberGenerator.GetString(DownloaderPasswordCharacters, DownloaderPasswordLength);

            // CreateUser, as the node makes its first account: the password is
            // set from inside it, where the account can already reach the
            // password store; the EULA is accepted at once, since nobody would
            // be there to accept it, and nothing is mailed to anybody.
            var made = await ExtAPI.CreateUser(
                                 userId,
                                 I18NString.Create(Languages.en, String.IsNullOrWhiteSpace(Name) ? userId.ToString() : Name.Trim()),
                                 SimpleEMailAddress.Parse($"{SafePart(userId.ToString())}@localhost"),
                                 User2OrganizationEdgeLabel.IsMember,
                                 nodeOrganization,
                                 Password:                  password,
                                 SkipDefaultNotifications:  true,
                                 SkipNewUserEMail:          true,
                                 SkipNewUserNotifications:  true,
                                 AcceptedEULA:              TimeProvider.GetUtcNow().AddSeconds(-1),
                                 IsAuthenticated:           true
                             );

            if (made is not User user)
                return new DownloaderChange(DownloaderChangeOutcome.NotSaved, $"The account '{userId}' could not be made.");

            if (!ExtAPI.TryGetUserGroup(UserGroup_Id.Parse(DownloadServerAccess.Downloader.Name), out var found) ||
                found is not UserGroup group)
            {
                return new DownloaderChange(DownloaderChangeOutcome.NotSaved, $"There is no group of the {DownloadServerAccess.Downloader.Name} role to put '{userId}' in.");
            }

            var joined = await ExtAPI.AddUserToUserGroup(user, User2UserGroupEdgeLabel.IsMember, group);

            if (!joined.IsSuccess)
                return new DownloaderChange(DownloaderChangeOutcome.NotSaved,
                                            $"The account '{userId}' was made, but could not be put in the {DownloadServerAccess.Downloader.Name} role: {joined.ErrorDescription?.FirstText()}");

            Log.Notice($"'{By}' made the downloader '{userId}'.", "downloadserver", "downloaders", "auth");

            return new DownloaderChange(DownloaderChangeOutcome.Changed,
                                        $"'{userId}' may download from now on.",
                                        userId.ToString(),
                                        password);

        }

        #endregion

        #region NewDownloaderPassword(Id, By)

        /// <summary>
        /// A new password for a downloader, made up here: the one before opens
        /// nothing from then on.
        /// </summary>
        public async Task<DownloaderChange> NewDownloaderPassword(String  Id,
                                                                  String  By)
        {

            if (!TryGetDownloader(Id, out var user, out var notFound))
                return notFound;

            var password  = RandomNumberGenerator.GetString(DownloaderPasswordCharacters, DownloaderPasswordLength);

            // The overload of several accounts, which sets a password anew: the
            // one of one account changes it only against the one it has, and
            // whoever gives a downloader a new one has, as a rule, lost it.
            var changed   = await ExtAPI.ChangePassword(new[] { user }, password, SuppressNotifications: true);

            if (changed.Result != CommandResult.Success)
                return new DownloaderChange(DownloaderChangeOutcome.NotSaved, $"The password of '{user.Id}' could not be changed: {changed.Description.FirstText()}");

            Log.Notice($"'{By}' gave the downloader '{user.Id}' a new password.", "downloadserver", "downloaders", "auth");

            return new DownloaderChange(DownloaderChangeOutcome.Changed,
                                        $"'{user.Id}' signs in with a new password from now on; the one before opens nothing.",
                                        user.Id.ToString(),
                                        password);

        }

        #endregion

        #region RemoveDownloader(Id, By)

        /// <summary>
        /// A downloader no longer let download: its account taken out of the
        /// downloader role. The record of what it downloaded is kept.
        /// </summary>
        public async Task<DownloaderChange> RemoveDownloader(String  Id,
                                                             String  By)
        {

            if (!TryGetDownloader(Id, out var user, out var notFound))
                return notFound;

            if (user is not User account ||
                !ExtAPI.TryGetUserGroup(UserGroup_Id.Parse(DownloadServerAccess.Downloader.Name), out var found) ||
                found is not UserGroup group)
            {
                return new DownloaderChange(DownloaderChangeOutcome.NotSaved, $"'{user.Id}' could not be taken out of the {DownloadServerAccess.Downloader.Name} role.");
            }

            var removed = await ExtAPI.RemoveUserFromUserGroup(account, User2UserGroupEdgeLabel.IsMember, group);

            if (!removed.IsSuccess)
                return new DownloaderChange(DownloaderChangeOutcome.NotSaved,
                                            $"'{user.Id}' could not be taken out of the {DownloadServerAccess.Downloader.Name} role: {removed.ErrorDescription?.FirstText()}");

            Log.Notice($"'{By}' took the downloader '{user.Id}' away: it may download no more.", "downloadserver", "downloaders", "auth");

            return new DownloaderChange(DownloaderChangeOutcome.Changed,
                                        $"'{user.Id}' may download no more. The record of what it downloaded is kept, and its account too, without the {DownloadServerAccess.Downloader.Name} role.",
                                        user.Id.ToString());

        }

        #endregion

        #region (private) TryGetDownloader(Id, out User, out NotFound)

        private Boolean TryGetDownloader(String                Id,
                                         out IUser             User,
                                         out DownloaderChange  NotFound)
        {

            User      = null!;
            NotFound  = new DownloaderChange(DownloaderChangeOutcome.NotFound, $"There is no downloader '{Id}'.");

            if (!User_Id.TryParse(Id.Trim(), out var userId) ||
                !ExtAPI.TryGetUser(userId, out var found) ||
                found is null ||
                !IsDownloader(found))
            {
                return false;
            }

            User = found;
            return true;

        }

        #endregion

        #region (static) SafePart(Text)

        /// <summary>
        /// A part of a name as a mail address of an account may carry it:
        /// letters, digits, '-', '_' and '.' as they are, every other character
        /// as '_', no dot in front or behind.
        /// </summary>
        public static String SafePart(String? Text)
        {

            var safe = new String((Text ?? "").Select(c => Char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray()).
                           TrimStart('.').
                           TrimEnd  ('.');

            if (safe.Length > 64)
                safe = safe[..64];

            return safe.Length == 0 ? "_" : safe;

        }

        #endregion

    }

}
