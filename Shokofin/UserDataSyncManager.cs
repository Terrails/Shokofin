using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Shokofin.API;
using Shokofin.Configuration;

namespace Shokofin
{

    [Flags]
    public enum SyncDirection {
        None = 0,
        Import = 1,
        Export = 2,
        Both = 3,
    }

    public class UserDataSyncManager
    {
        private readonly IUserDataManager UserDataManager;

        private readonly ILibraryManager LibraryManager;

        private readonly ILogger<UserDataSyncManager> Logger;

        private readonly ShokoAPIClient APIClient;

        private readonly IIdLookup Lookup;

        public UserDataSyncManager(IUserDataManager userDataManager, ILibraryManager libraryManager, ILogger<UserDataSyncManager> logger, ShokoAPIClient apiClient, IIdLookup lookup)
        {
            UserDataManager = userDataManager;
            LibraryManager = libraryManager;
            Logger = logger;
            APIClient = apiClient;
            Lookup = lookup;

            UserDataManager.UserDataSaved += OnUserDataSaved;
            LibraryManager.ItemAdded += OnItemAddedOrUpdated;
            LibraryManager.ItemUpdated += OnItemAddedOrUpdated;
        }

        public void Dispose()
        {
            UserDataManager.UserDataSaved -= OnUserDataSaved;
            LibraryManager.ItemAdded -= OnItemAddedOrUpdated;
            LibraryManager.ItemUpdated -= OnItemAddedOrUpdated;
        }

        private bool TryGetUserConfiguration(Guid userId, out UserConfiguration config)
        {
            config = Plugin.Instance.Configuration.UserList.FirstOrDefault(c => c.UserId == userId && c.EnableSynchronization);
            return config != null;
        }

        #region Export/Scrobble

        public void OnUserDataSaved(object sender, UserDataSaveEventArgs e)
        {
            if (e == null || e.Item == null || Guid.Equals(e.UserId, Guid.Empty) || e.UserData == null)
                return;

            if (e.SaveReason == UserDataSaveReason.UpdateUserRating) {
                OnUserRatingSaved(sender, e);
                return;
            }

            if (!(
                    (e.Item is Movie || e.Item is Episode) &&
                    TryGetUserConfiguration(e.UserId, out var userConfig) &&
                    Lookup.TryGetFileIdFor(e.Item, out var fileId) &&
                    Lookup.TryGetEpisodeIdFor(e.Item, out var episodeId)
                ))
                return;

            var userData = e.UserData;
            var config = Plugin.Instance.Configuration;
            switch (e.SaveReason) {
                case UserDataSaveReason.PlaybackStart:
                case UserDataSaveReason.PlaybackProgress:
                    if (!userConfig.SyncUserDataUnderPlayback)
                        return;
                    Logger.LogDebug("Scrobbled during playback. (File={FileId})", fileId);
                    APIClient.ScrobbleFile(fileId, userData.PlaybackPositionTicks, userConfig.Token).ConfigureAwait(false);
                    break;
                case UserDataSaveReason.PlaybackFinished:
                    if (!userConfig.SyncUserDataAfterPlayback)
                        return;
                    Logger.LogDebug("Scrobbled after playback. (File={FileId})", fileId);
                    APIClient.ScrobbleFile(fileId, userData.Played, userData.PlaybackPositionTicks, userConfig.Token).ConfigureAwait(false);
                    break;
                case UserDataSaveReason.TogglePlayed:
                    Logger.LogDebug("Scrobbled when toggled. (File={FileId})", fileId);
                    if (userData.PlaybackPositionTicks == 0)
                        APIClient.ScrobbleFile(fileId, userData.Played, userConfig.Token).ConfigureAwait(false);
                    else
                        APIClient.ScrobbleFile(fileId, userData.PlaybackPositionTicks, userConfig.Token).ConfigureAwait(false);
                    break;
            }
        }

        // Updates to favotite state and/or user rating.
        private void OnUserRatingSaved(object sender, UserDataSaveEventArgs e)
        {
            if (!TryGetUserConfiguration(e.UserId, out var userConfig))
                return;
            var userData = e.UserData;
            var config = Plugin.Instance.Configuration;
            switch (e.Item) {
                case Episode:
                case Movie: {
                    var video = e.Item as Video;
                    if (!Lookup.TryGetEpisodeIdFor(video, out var episodeId))
                        return;

                    SyncVideo(video, userConfig, userData, SyncDirection.Export, episodeId).ConfigureAwait(false);
                    break;
                }
                case Season season: {
                    if (!Lookup.TryGetSeriesIdFor(season, out var seriesId))
                        return;

                    SyncSeason(season, userConfig, userData, SyncDirection.Export, seriesId).ConfigureAwait(false);
                    break;
                }
                case Series series: {
                    if (!Lookup.TryGetSeriesIdFor(series, out var seriesId))
                        return;

                    SyncSeries(series, userConfig, userData, SyncDirection.Export, seriesId).ConfigureAwait(false);
                    break;
                }
            }
        }

        #endregion
        #region Import/Sync

        public async Task ScanAndSync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var enabledUsers = Plugin.Instance.Configuration.UserList.Where(c => c.EnableSynchronization).ToList();
            if (enabledUsers.Count == 0) {
                progress.Report(100);
                return;
            }

            var videos = LibraryManager.GetItemList(new InternalItemsQuery {
                MediaTypes = new[] { MediaType.Video },
                IsFolder = false,
                Recursive = true,
                DtoOptions = new DtoOptions(false) {
                    EnableImages = false
                },
                SourceTypes = new SourceType[] { SourceType.Library },
                HasChapterImages = false,
                IsVirtualItem = false,
            })
                .OfType<Video>()
                .ToList();

            var numComplete = 0;
            var numTotal = videos.Count * enabledUsers.Count;
            foreach (var video in videos)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!(Lookup.TryGetFileIdFor(video, out var fileId) && Lookup.TryGetEpisodeIdFor(video, out var episodeId)))
                    continue;

                foreach (var userConfig in enabledUsers) {
                    await SyncVideo(video, userConfig, null, SyncDirection.Both, fileId, episodeId).ConfigureAwait(false);

                    numComplete++;
                    double percent = numComplete;
                    percent /= numTotal;

                    progress.Report(percent * 100);
                }
            }
            progress.Report(100);
        }

        public void OnItemAddedOrUpdated(object sender, ItemChangeEventArgs e)
        {
            if (e == null || e.Item == null || e.Parent == null || !(e.UpdateReason.HasFlag(ItemUpdateType.MetadataImport) || e.UpdateReason.HasFlag(ItemUpdateType.MetadataDownload)))
                return;

            switch (e.Item) {
                case Video video: {
                    if (!(Lookup.TryGetFileIdFor(video, out var fileId) && Lookup.TryGetEpisodeIdFor(video, out var episodeId)))
                        return;

                    foreach (var userConfig in Plugin.Instance.Configuration.UserList) {
                        if (!userConfig.EnableSynchronization)
                            continue;

                        if (!userConfig.SyncUserDataOnImport)
                            continue;

                        SyncVideo(video, userConfig, null, SyncDirection.Import, fileId, episodeId).ConfigureAwait(false);
                    }
                    break;
                }
                case Season season: {
                    if (!Lookup.TryGetSeriesIdFor(season, out var seriesId))
                        return;

                    foreach (var userConfig in Plugin.Instance.Configuration.UserList) {
                        if (!userConfig.EnableSynchronization)
                            continue;

                        if (!userConfig.SyncUserDataOnImport)
                            continue;

                        SyncSeason(season, userConfig, null, SyncDirection.Import, seriesId).ConfigureAwait(false);
                    }
                    break;
                }
                case Series series: {
                    if (!Lookup.TryGetSeriesIdFor(series, out var seriesId))
                        return;

                    foreach (var userConfig in Plugin.Instance.Configuration.UserList) {
                        if (!userConfig.EnableSynchronization)
                            continue;

                        if (!userConfig.SyncUserDataOnImport)
                            continue;

                        SyncSeries(series, userConfig, null, SyncDirection.Import, seriesId).ConfigureAwait(false);
                    }
                    break;
                }
            }

        }

        #endregion

        private async Task SyncSeries(Series series, UserConfiguration userConfig, UserItemData userData, SyncDirection direction, string seriesId)
        {
            if (userData == null)
                userData = UserDataManager.GetUserData(userConfig.UserId, series);
            // Create some new user-data if none exists.
            if (userData == null)
                userData = new UserItemData {
                    UserId = userConfig.UserId,

                    LastPlayedDate = null,
                };

            // TODO: Check what needs to be done, e.g. update JF, update SS, or nothing.
            Logger.LogDebug("TODO; Sync user rating for series {SeriesName}. (Series={SeriesId})", series.Name, seriesId);
        }

        private async Task SyncSeason(Season season, UserConfiguration userConfig, UserItemData userData, SyncDirection direction, string seriesId)
        {
            if (userData == null)
                userData = UserDataManager.GetUserData(userConfig.UserId, season);
            // Create some new user-data if none exists.
            if (userData == null)
                userData = new UserItemData {
                    UserId = userConfig.UserId,

                    LastPlayedDate = null,
                };

            // TODO: Check what needs to be done, e.g. update JF, update SS, or nothing.
            Logger.LogDebug("TODO; Sync user rating for season {SeasonNumber} in series {SeriesName}. (Series={SeriesId})", season.IndexNumber, season.SeriesName, seriesId);
        }

        private async Task SyncVideo(Video video, UserConfiguration userConfig, UserItemData userData, SyncDirection direction, string episodeId)
        {
            // Try to load the user-data if it was not provided
            if (userData == null)
                userData = UserDataManager.GetUserData(userConfig.UserId, video);
            // Create some new user-data if none exists.
            if (userData == null)
                userData = new UserItemData {
                    UserId = userConfig.UserId,

                    LastPlayedDate = null,
                };

            // var remoteUserData = await APIClient.GetFileUserData(fileId, userConfig.Token);
            // if (remoteUserData == null)
            //     return;

            // TODO: Check what needs to be done, e.g. update JF, update SS, or nothing.
            Logger.LogDebug("TODO: Sync user data for video {ItemName}. (Episode={EpisodeId})", video.Name, episodeId);
        }

        private async Task SyncVideo(Video video, UserConfiguration userConfig, UserItemData userData, SyncDirection direction, string fileId, string episodeId)
        {
            // Try to load the user-data if it was not provided
            if (userData == null)
                userData = UserDataManager.GetUserData(userConfig.UserId, video);
            // Create some new user-data if none exists.
            if (userData == null)
                userData = new UserItemData {
                    UserId = userConfig.UserId,

                    LastPlayedDate = null,
                };

            // var remoteUserData = await APIClient.GetFileUserData(fileId, userConfig.Token);
            // if (remoteUserData == null)
            //     return;

            // TODO: Check what needs to be done, e.g. update JF, update SS, or nothing.
            Logger.LogDebug("TODO: Sync user data for video {ItemName}. (File={FileId},Episode={EpisodeId})", video.Name, fileId, episodeId);
        }
    }
}
