using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using Shokofin.API;
using Shokofin.Utils;

using Info = Shokofin.API.Info;

namespace Shokofin.Providers
{
    public class SeasonProvider : IRemoteMetadataProvider<Season, SeasonInfo>
    {
        public string Name => Plugin.MetadataProviderName;
        private readonly IHttpClientFactory HttpClientFactory;
        private readonly ILogger<SeasonProvider> Logger;

        private readonly ShokoAPIManager ApiManager;

        public SeasonProvider(IHttpClientFactory httpClientFactory, ILogger<SeasonProvider> logger, ShokoAPIManager apiManager)
        {
            HttpClientFactory = httpClientFactory;
            Logger = logger;
            ApiManager = apiManager;
        }

        public async Task<MetadataResult<Season>> GetMetadata(SeasonInfo info, CancellationToken cancellationToken)
        {
            try {
                if (!info.IndexNumber.HasValue || info.IndexNumber.HasValue && info.IndexNumber.Value == 0)
                    return null;

                var result = new MetadataResult<Season>();
                var filterByType = Plugin.Instance.Configuration.FilterOnLibraryTypes ? Ordering.GroupFilterType.Others : Ordering.GroupFilterType.Default;
                if (!info.SeriesProviderIds.TryGetValue("Shoko Series", out var seriesId) || !info.IndexNumber.HasValue) {
                    Logger.LogDebug("Unable refresh Season {SeasonNumber} {SeasonName}", info.IndexNumber, info.Name);
                    return result;
                }

                var seasonNumber = info.IndexNumber.Value;
                var showInfo = await ApiManager.GetShowInfoForSeries(seriesId, filterByType);
                if (showInfo == null) {
                    Logger.LogWarning("Unable to find show info for Season {SeasonNumber}. (Series={SeriesId})", seasonNumber, seriesId);
                    return result;
                }

                var seasonInfo = showInfo.GetSeriesInfoBySeasonNumber(seasonNumber);
                if (seasonInfo == null || !showInfo.SeasonNumberBaseDictionary.TryGetValue(seasonInfo, out var baseSeasonNumber)) {
                    Logger.LogWarning("Unable to find series info for Season {SeasonNumber}. (Series={SeriesId},Group={GroupId})", seasonNumber, seriesId, showInfo.Id);
                    return result;
                }

                Logger.LogInformation("Found info for Season {SeasonNumber} in Series {SeriesName} (Series={SeriesId},Group={GroupId})", seasonNumber, showInfo.Name, seriesId, showInfo.Id);

                var offset = Math.Abs(seasonNumber - baseSeasonNumber);

                result.Item = CreateMetadata(seasonInfo, seasonNumber, offset, info.MetadataLanguage);
                result.HasMetadata = true;
                result.ResetPeople();
                foreach (var person in seasonInfo.Staff)
                    result.AddPerson(person);

                return result;
            }
            catch (Exception ex) {
                Logger.LogError(ex, "Threw unexpectedly; {Message}", ex.Message);
                return new MetadataResult<Season>();
            }
        }

        public static Season CreateMetadata(Info.SeasonInfo seasonInfo, int seasonNumber, int offset, string metadataLanguage)
            => CreateMetadata(seasonInfo, seasonNumber, offset, metadataLanguage, null, Guid.Empty);

        public static Season CreateMetadata(Info.SeasonInfo seasonInfo, int seasonNumber, int offset, Series series, System.Guid seasonId)
            => CreateMetadata(seasonInfo, seasonNumber, offset, series.GetPreferredMetadataLanguage(), series, seasonId);

        public static Season CreateMetadata(Info.SeasonInfo seasonInfo, int seasonNumber, int offset, string metadataLanguage, Series series, System.Guid seasonId)
        {
            var ( displayTitle, alternateTitle ) = Text.GetSeriesTitles(seasonInfo.AniDB.Titles, seasonInfo.Shoko.Name, metadataLanguage);
            var sortTitle = $"S{seasonNumber} - {seasonInfo.Shoko.Name}";

            if (offset > 0) {
                string type = string.Empty;
                switch (offset) {
                    default:
                        break;
                    case 1:
                        if (seasonInfo.AlternateEpisodesList.Count > 0)
                            type = "Alternate Stories";
                        else
                            type = "Other Episodes";
                        break;
                    case 2:
                        type = "Other Episodes";
                        break;
                }
                if (!string.IsNullOrEmpty(type)) {
                    displayTitle += $" ({type})";
                    alternateTitle += $" ({type})";
                }
            }

            Season season;
            if (series != null) {
                season = new Season {
                    Name = displayTitle,
                    OriginalTitle = alternateTitle,
                    IndexNumber = seasonNumber,
                    SortName = sortTitle,
                    ForcedSortName = sortTitle,
                    Id = seasonId,
                    IsVirtualItem = true,
                    Overview = Text.GetDescription(seasonInfo),
                    PremiereDate = seasonInfo.AniDB.AirDate,
                    EndDate = seasonInfo.AniDB.EndDate,
                    ProductionYear = seasonInfo.AniDB.AirDate?.Year,
                    Tags = seasonInfo.Tags.ToArray(),
                    Genres = seasonInfo.Genres.ToArray(),
                    Studios = seasonInfo.Studios.ToArray(),
                    CommunityRating = seasonInfo.AniDB.Rating?.ToFloat(10),
                    SeriesId = series.Id,
                    SeriesName = series.Name,
                    SeriesPresentationUniqueKey = series.GetPresentationUniqueKey(),
                    DateModified = DateTime.UtcNow,
                    DateLastSaved = DateTime.UtcNow,
                };
            }
            else {
                season = new Season {
                    Name = displayTitle,
                    OriginalTitle = alternateTitle,
                    IndexNumber = seasonNumber,
                    SortName = sortTitle,
                    ForcedSortName = sortTitle,
                    Overview = Text.GetDescription(seasonInfo),
                    PremiereDate = seasonInfo.AniDB.AirDate,
                    EndDate = seasonInfo.AniDB.EndDate,
                    ProductionYear = seasonInfo.AniDB.AirDate?.Year,
                    Tags = seasonInfo.Tags.ToArray(),
                    Genres = seasonInfo.Genres.ToArray(),
                    Studios = seasonInfo.Studios.ToArray(),
                    CommunityRating = seasonInfo.AniDB.Rating?.ToFloat(10),
                };
            }
            season.ProviderIds.Add("Shoko Series", seasonInfo.Id);
            season.ProviderIds.Add("Shoko Season Offset", offset.ToString());
            if (Plugin.Instance.Configuration.AddAniDBId)
                season.ProviderIds.Add("AniDB", seasonInfo.AniDB.Id.ToString());

            return season;
        }

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeasonInfo searchInfo, CancellationToken cancellationToken)
            => Task.FromResult<IEnumerable<RemoteSearchResult>>(new List<RemoteSearchResult>());

        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
            => HttpClientFactory.CreateClient().GetAsync(url, cancellationToken);

        private static string GetSeasonName(int seasonNumber, string seasonName)
            => seasonNumber switch
            {
                127 => "Misc.",
                126 => "Credits",
                125 => "Trailers",
                124 => "Others",
                123 => "Unknown",
                _ => seasonName,
            };
    }
}
