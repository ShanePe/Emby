﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using SQLitePCL.pretty;

namespace Emby.Server.Implementations.Data
{
    public class SqliteUserDataRepository : BaseSqliteRepository, IUserDataRepository
    {
        private SQLiteDatabaseConnection _connection;

        public SqliteUserDataRepository(ILogger logger, IApplicationPaths appPaths)
            : base(logger)
        {
            DbFilePath = Path.Combine(appPaths.DataPath, "userdata_v2.db");
        }

        protected override bool EnableConnectionPooling
        {
            get { return false; }
        }

        /// <summary>
        /// Gets the name of the repository
        /// </summary>
        /// <value>The name.</value>
        public string Name
        {
            get
            {
                return "SQLite";
            }
        }

        /// <summary>
        /// Opens the connection to the database
        /// </summary>
        /// <returns>Task.</returns>
        public void Initialize(SQLiteDatabaseConnection connection, ReaderWriterLockSlim writeLock)
        {
            WriteLock.Dispose();
            WriteLock = writeLock;
            _connection = connection;

            string[] queries = {

                                "create table if not exists UserDataDb.userdata (key nvarchar, userId GUID, rating float null, played bit, playCount int, isFavorite bit, playbackPositionTicks bigint, lastPlayedDate datetime null)",

                                "drop index if exists UserDataDb.idx_userdata",
                                "drop index if exists UserDataDb.idx_userdata1",
                                "drop index if exists UserDataDb.idx_userdata2",
                                "drop index if exists UserDataDb.userdataindex1",

                                "create unique index if not exists UserDataDb.userdataindex on userdata (key, userId)",
                                "create index if not exists UserDataDb.userdataindex2 on userdata (key, userId, played)",
                                "create index if not exists UserDataDb.userdataindex3 on userdata (key, userId, playbackPositionTicks)",
                                "create index if not exists UserDataDb.userdataindex4 on userdata (key, userId, isFavorite)",

                                //pragmas
                                "pragma temp_store = memory",

                                "pragma shrink_memory"
                               };

            _connection.RunQueries(queries);

            connection.RunInTransaction(db =>
            {
                var existingColumnNames = GetColumnNames(db, "userdata");

                AddColumn(db, "userdata", "AudioStreamIndex", "int", existingColumnNames);
                AddColumn(db, "userdata", "SubtitleStreamIndex", "int", existingColumnNames);
            });
        }

        /// <summary>
        /// Saves the user data.
        /// </summary>
        /// <param name="userId">The user id.</param>
        /// <param name="key">The key.</param>
        /// <param name="userData">The user data.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        /// <exception cref="System.ArgumentNullException">userData
        /// or
        /// cancellationToken
        /// or
        /// userId
        /// or
        /// userDataId</exception>
        public Task SaveUserData(Guid userId, string key, UserItemData userData, CancellationToken cancellationToken)
        {
            if (userData == null)
            {
                throw new ArgumentNullException("userData");
            }
            if (userId == Guid.Empty)
            {
                throw new ArgumentNullException("userId");
            }
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentNullException("key");
            }

            return PersistUserData(userId, key, userData, cancellationToken);
        }

        public Task SaveAllUserData(Guid userId, IEnumerable<UserItemData> userData, CancellationToken cancellationToken)
        {
            if (userData == null)
            {
                throw new ArgumentNullException("userData");
            }
            if (userId == Guid.Empty)
            {
                throw new ArgumentNullException("userId");
            }

            return PersistAllUserData(userId, userData.ToList(), cancellationToken);
        }

        /// <summary>
        /// Persists the user data.
        /// </summary>
        /// <param name="userId">The user id.</param>
        /// <param name="key">The key.</param>
        /// <param name="userData">The user data.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        public async Task PersistUserData(Guid userId, string key, UserItemData userData, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using (WriteLock.Write())
            {
                _connection.RunInTransaction(db =>
                {
                    SaveUserData(db, userId, key, userData);
                });
            }
        }

        private void SaveUserData(IDatabaseConnection db, Guid userId, string key, UserItemData userData)
        {
            var paramList = new List<object>();
            var commandText = "replace into userdata (key, userId, rating,played,playCount,isFavorite,playbackPositionTicks,lastPlayedDate,AudioStreamIndex,SubtitleStreamIndex) values (?, ?, ?,?,?,?,?,?,?,?)";

            paramList.Add(key);
            paramList.Add(userId.ToGuidParamValue());
            paramList.Add(userData.Rating);
            paramList.Add(userData.Played);
            paramList.Add(userData.PlayCount);
            paramList.Add(userData.IsFavorite);
            paramList.Add(userData.PlaybackPositionTicks);

            if (userData.LastPlayedDate.HasValue)
            {
                paramList.Add(userData.LastPlayedDate.Value.ToDateTimeParamValue());
            }
            else
            {
                paramList.Add(null);
            }
            paramList.Add(userData.AudioStreamIndex);
            paramList.Add(userData.SubtitleStreamIndex);

            db.Execute(commandText, paramList.ToArray());
        }

        /// <summary>
        /// Persist all user data for the specified user
        /// </summary>
        private async Task PersistAllUserData(Guid userId, List<UserItemData> userDataList, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using (WriteLock.Write())
            {
                _connection.RunInTransaction(db =>
                {
                    foreach (var userItemData in userDataList)
                    {
                        SaveUserData(db, userId, userItemData.Key, userItemData);
                    }
                });
            }
        }

        /// <summary>
        /// Gets the user data.
        /// </summary>
        /// <param name="userId">The user id.</param>
        /// <param name="key">The key.</param>
        /// <returns>Task{UserItemData}.</returns>
        /// <exception cref="System.ArgumentNullException">
        /// userId
        /// or
        /// key
        /// </exception>
        public UserItemData GetUserData(Guid userId, string key)
        {
            if (userId == Guid.Empty)
            {
                throw new ArgumentNullException("userId");
            }
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentNullException("key");
            }

            var commandText = "select key,userid,rating,played,playCount,isFavorite,playbackPositionTicks,lastPlayedDate,AudioStreamIndex,SubtitleStreamIndex from userdata where key = ? and userId=?";

            var paramList = new List<object>();
            paramList.Add(key);
            paramList.Add(userId.ToGuidParamValue());

            foreach (var row in _connection.Query(commandText, paramList.ToArray()))
            {
                return ReadRow(row);
            }

            return null;
        }

        public UserItemData GetUserData(Guid userId, List<string> keys)
        {
            if (userId == Guid.Empty)
            {
                throw new ArgumentNullException("userId");
            }
            if (keys == null)
            {
                throw new ArgumentNullException("keys");
            }

            if (keys.Count == 0)
            {
                return null;
            }

            return GetUserData(userId, keys[0]);
        }

        /// <summary>
        /// Return all user-data associated with the given user
        /// </summary>
        /// <param name="userId"></param>
        /// <returns></returns>
        public IEnumerable<UserItemData> GetAllUserData(Guid userId)
        {
            if (userId == Guid.Empty)
            {
                throw new ArgumentNullException("userId");
            }

            var list = new List<UserItemData>();

            using (WriteLock.Read())
            {
                var commandText = "select key,userid,rating,played,playCount,isFavorite,playbackPositionTicks,lastPlayedDate,AudioStreamIndex,SubtitleStreamIndex from userdata where userId=?";

                var paramList = new List<object>();
                paramList.Add(userId.ToGuidParamValue());

                foreach (var row in _connection.Query(commandText, paramList.ToArray()))
                {
                    list.Add(ReadRow(row));
                }
            }

            return list;
        }

        /// <summary>
        /// Read a row from the specified reader into the provided userData object
        /// </summary>
        /// <param name="reader"></param>
        private UserItemData ReadRow(IReadOnlyList<IResultSetValue> reader)
        {
            var userData = new UserItemData();

            userData.Key = reader[0].ToString();
            userData.UserId = reader[1].ReadGuid();

            if (reader[2].SQLiteType != SQLiteType.Null)
            {
                userData.Rating = reader[2].ToDouble();
            }

            userData.Played = reader[3].ToBool();
            userData.PlayCount = reader[4].ToInt();
            userData.IsFavorite = reader[5].ToBool();
            userData.PlaybackPositionTicks = reader[6].ToInt64();

            if (reader[7].SQLiteType != SQLiteType.Null)
            {
                userData.LastPlayedDate = reader[7].ReadDateTime();
            }

            if (reader[8].SQLiteType != SQLiteType.Null)
            {
                userData.AudioStreamIndex = reader[8].ToInt();
            }

            if (reader[9].SQLiteType != SQLiteType.Null)
            {
                userData.SubtitleStreamIndex = reader[9].ToInt();
            }

            return userData;
        }

        protected override void Dispose(bool dispose)
        {
            // handled by library database
        }

        protected override void CloseConnection()
        {
            // handled by library database
        }
    }
}