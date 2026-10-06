using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DataAugmentation.Scene;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.IndexerSearch
{
    public interface ISearchForReleases
    {
        Task<List<DownloadDecision>> EpisodeSearch(int episodeId, bool userInvokedSearch, bool interactiveSearch, bool useCache = true);
        Task<List<DownloadDecision>> EpisodeSearch(Episode episode, bool userInvokedSearch, bool interactiveSearch, bool useCache = true);
        Task<List<DownloadDecision>> SeasonSearch(int seriesId, int seasonNumber, bool missingOnly, bool monitoredOnly, bool userInvokedSearch, bool interactiveSearch, bool useCache = true);
        Task<List<DownloadDecision>> SeasonSearch(int seriesId, int seasonNumber, List<Episode> episodes, bool monitoredOnly, bool userInvokedSearch, bool interactiveSearch, bool useCache = true);
        CachedSearchResult CachedEpisodeSearch(int episodeId, bool interactiveSearch);
        CachedSearchResult CachedSeasonSearch(int seriesId, int seasonNumber, bool interactiveSearch);
        Task<CachedSearchResult> InteractiveEpisodeSearch(int episodeId, bool refresh, bool searchRemaining);
        Task<CachedSearchResult> InteractiveSeasonSearch(int seriesId, int seasonNumber, bool refresh, bool searchRemaining);
        InteractiveSearchStatus InteractiveEpisodeSearchStatus(int episodeId);
        InteractiveSearchStatus InteractiveSeasonSearchStatus(int seriesId, int seasonNumber);
    }

    public class ReleaseSearchService : ISearchForReleases
    {
        private readonly IIndexerFactory _indexerFactory;
        private readonly ISceneMappingService _sceneMapping;
        private readonly ISeriesService _seriesService;
        private readonly IEpisodeService _episodeService;
        private readonly IMakeDownloadDecision _makeDownloadDecision;
        private readonly IConfigService _configService;
        private readonly IUpgradableSpecification _upgradableSpecification;
        private readonly ICached<CachedSearch> _searchResultCache;
        private readonly ICached<InteractiveSearch> _interactiveSearches;
        private readonly AsyncLocal<CachedSearch> _currentSearch = new AsyncLocal<CachedSearch>();

        // Searching the remaining indexers of an interactive search queries only these, all at once
        private readonly AsyncLocal<HashSet<int>> _onlyIndexerIds = new AsyncLocal<HashSet<int>>();

        // Search Concurrency is one limit per search command: every indexer query of the command takes a slot of the same semaphore.
        // Only the indexer query holds a slot, the searches around it never wait on one, so nested searches cannot deadlock
        internal static readonly AsyncLocal<SemaphoreSlim> SearchSlots = new AsyncLocal<SemaphoreSlim>();
        private readonly Logger _logger;

        public ReleaseSearchService(IIndexerFactory indexerFactory,
                                ISceneMappingService sceneMapping,
                                ISeriesService seriesService,
                                IEpisodeService episodeService,
                                IMakeDownloadDecision makeDownloadDecision,
                                IConfigService configService,
                                ICacheManager cacheManager,
                                IUpgradableSpecification upgradableSpecification,
                                Logger logger)
        {
            _indexerFactory = indexerFactory;
            _sceneMapping = sceneMapping;
            _seriesService = seriesService;
            _episodeService = episodeService;
            _makeDownloadDecision = makeDownloadDecision;
            _configService = configService;
            _upgradableSpecification = upgradableSpecification;
            _searchResultCache = cacheManager.GetCache<CachedSearch>(GetType(), "searchResults");
            _interactiveSearches = cacheManager.GetCache<InteractiveSearch>(GetType(), "interactiveSearches");
            _logger = logger;
        }

        public async Task<List<DownloadDecision>> EpisodeSearch(int episodeId, bool userInvokedSearch, bool interactiveSearch, bool useCache = true)
        {
            var episode = _episodeService.GetEpisode(episodeId);

            return await EpisodeSearch(episode, userInvokedSearch, interactiveSearch, useCache);
        }

        public async Task<List<DownloadDecision>> EpisodeSearch(Episode episode, bool userInvokedSearch, bool interactiveSearch, bool useCache = true)
        {
            SearchSlots.Value ??= new SemaphoreSlim(Math.Max(1, _configService.SearchConcurrency));

            var key = EpisodeCacheKey(episode.Id);
            var episodes = new List<Episode> { episode };

            var cached = useCache ? FindCachedSearch(FindCachedEntry(key), episode.SeriesId, episodes, false, userInvokedSearch, interactiveSearch) : null;

            return cached?.Decisions ?? (await SearchAndCache(key, episodes, () => SearchEpisode(episode, userInvokedSearch, interactiveSearch))).Decisions;
        }

        public async Task<List<DownloadDecision>> SeasonSearch(int seriesId, int seasonNumber, List<Episode> episodes, bool monitoredOnly, bool userInvokedSearch, bool interactiveSearch, bool useCache = true)
        {
            SearchSlots.Value ??= new SemaphoreSlim(Math.Max(1, _configService.SearchConcurrency));

            var key = SeasonCacheKey(seriesId, seasonNumber);

            var cached = useCache ? FindCachedSearch(FindCachedEntry(key), seriesId, episodes, monitoredOnly, userInvokedSearch, interactiveSearch) : null;

            return cached?.Decisions ?? (await SearchAndCache(key, episodes, () => SearchSeason(seriesId, seasonNumber, episodes, monitoredOnly, userInvokedSearch, interactiveSearch))).Decisions;
        }

        public async Task<CachedSearchResult> InteractiveEpisodeSearch(int episodeId, bool refresh, bool searchRemaining)
        {
            var episode = _episodeService.GetEpisode(episodeId);

            return await InteractiveSearchItem(EpisodeCacheKey(episodeId), episode.SeriesId, new List<Episode> { episode }, refresh, searchRemaining, () => SearchEpisode(episode, true, true));
        }

        public async Task<CachedSearchResult> InteractiveSeasonSearch(int seriesId, int seasonNumber, bool refresh, bool searchRemaining)
        {
            var episodes = _episodeService.GetEpisodesBySeason(seriesId, seasonNumber);

            return await InteractiveSearchItem(SeasonCacheKey(seriesId, seasonNumber), seriesId, episodes, refresh, searchRemaining, () => SearchSeason(seriesId, seasonNumber, episodes, false, true, true));
        }

        public InteractiveSearchStatus InteractiveEpisodeSearchStatus(int episodeId)
        {
            return GetInteractiveSearchStatus(EpisodeCacheKey(episodeId));
        }

        public InteractiveSearchStatus InteractiveSeasonSearchStatus(int seriesId, int seasonNumber)
        {
            return GetInteractiveSearchStatus(SeasonCacheKey(seriesId, seasonNumber));
        }

        private async Task<CachedSearchResult> InteractiveSearchItem(string key, int seriesId, List<Episode> episodes, bool refresh, bool searchRemaining, Func<Task<List<DownloadDecision>>> search)
        {
            SearchSlots.Value ??= new SemaphoreSlim(Math.Max(1, _configService.SearchConcurrency));

            var previous = searchRemaining ? _interactiveSearches.Find(key) : null;

            if (previous != null)
            {
                var remainingIndexerIds = GetInteractiveSearchStatus(previous).Indexers
                    .Where(i => i.Status != IndexerSearchStatusType.Searched && i.Status != IndexerSearchStatusType.Cached)
                    .Select(i => i.IndexerId)
                    .ToHashSet();

                var interactiveSearch = previous;

                if (remainingIndexerIds.Any())
                {
                    _logger.ProgressInfo("Searching {0} remaining indexers", remainingIndexerIds.Count);

                    _onlyIndexerIds.Value = remainingIndexerIds;

                    var entry = (await SearchAndCache(key, episodes, search, previous.Entry)).Entry;

                    interactiveSearch = new InteractiveSearch
                    {
                        Entry = entry,
                        SeriesId = seriesId,
                        CachedAt = previous.CachedAt,
                        SearchedIndexerIds = previous.SearchedIndexerIds.Union(remainingIndexerIds).ToHashSet()
                    };
                }

                SetInteractiveSearch(key, interactiveSearch);

                return new CachedSearchResult(Decide(interactiveSearch.Entry, seriesId, episodes, false, true, true, false).Decisions, interactiveSearch.CachedAt);
            }

            var cachedEntry = refresh ? null : FindCachedEntry(key);
            var cached = FindCachedSearch(cachedEntry, seriesId, episodes, false, true, true);

            if (cached != null)
            {
                SetInteractiveSearch(key, new InteractiveSearch { Entry = cachedEntry, SeriesId = seriesId, CachedAt = cached.SearchedAt, SearchedIndexerIds = new HashSet<int>() });

                return cached;
            }

            var result = await SearchAndCache(key, episodes, search);

            SetInteractiveSearch(key, new InteractiveSearch { Entry = result.Entry, SeriesId = seriesId, SearchedIndexerIds = result.Entry.Searches.SelectMany(s => s.Statuses).Select(i => i.IndexerId).ToHashSet() });

            return new CachedSearchResult(result.Decisions, null);
        }

        private void SetInteractiveSearch(string key, InteractiveSearch interactiveSearch)
        {
            // Lives as long as the releases of the interactive search can be grabbed
            _interactiveSearches.ClearExpired();
            _interactiveSearches.Set(key, interactiveSearch, TimeSpan.FromMinutes(30));
        }

        private InteractiveSearchStatus GetInteractiveSearchStatus(string key)
        {
            var interactiveSearch = _interactiveSearches.Find(key);

            return interactiveSearch == null ? new InteractiveSearchStatus() : GetInteractiveSearchStatus(interactiveSearch);
        }

        private InteractiveSearchStatus GetInteractiveSearchStatus(InteractiveSearch interactiveSearch)
        {
            var entry = interactiveSearch.Entry;
            var seriesTags = _seriesService.GetSeries(interactiveSearch.SeriesId).Tags;

            var statuses = _indexerFactory.InteractiveSearchEnabled()
                .Where(i => i.Definition.Tags.Empty() || i.Definition.Tags.Intersect(seriesTags).Any())
                .Select(indexer =>
                {
                    var definition = (IndexerDefinition)indexer.Definition;
                    var searches = entry.Searches.SelectMany(s => s.Statuses).Where(s => s.IndexerId == definition.Id).ToList();

                    // A season searched with several queries shows the worst outcome of the indexer
                    var worst = searches.MaxBy(s => s.Status);

                    var status = new IndexerSearchStatus
                    {
                        IndexerId = definition.Id,
                        Name = definition.Name,
                        Priority = definition.Priority,
                        Status = worst?.Status ?? IndexerSearchStatusType.Skipped,
                        ReleaseCount = searches.Sum(s => s.ReleaseCount),
                        Message = worst?.Message
                    };

                    if (status.Status == IndexerSearchStatusType.Searched && !interactiveSearch.SearchedIndexerIds.Contains(definition.Id))
                    {
                        status.Status = IndexerSearchStatusType.Cached;
                    }

                    return status;
                })
                .OrderBy(s => s.Priority)
                .ThenBy(s => s.Name)
                .ToList();

            return new InteractiveSearchStatus { CachedAt = interactiveSearch.CachedAt, Indexers = statuses };
        }

        public CachedSearchResult CachedEpisodeSearch(int episodeId, bool interactiveSearch)
        {
            var entry = FindCachedEntry(EpisodeCacheKey(episodeId));

            if (entry == null)
            {
                return null;
            }

            var episode = _episodeService.GetEpisode(episodeId);

            return FindCachedSearch(entry, episode.SeriesId, new List<Episode> { episode }, false, interactiveSearch, interactiveSearch);
        }

        public CachedSearchResult CachedSeasonSearch(int seriesId, int seasonNumber, bool interactiveSearch)
        {
            var entry = FindCachedEntry(SeasonCacheKey(seriesId, seasonNumber));

            if (entry == null)
            {
                return null;
            }

            return FindCachedSearch(entry, seriesId, _episodeService.GetEpisodesBySeason(seriesId, seasonNumber), !interactiveSearch, interactiveSearch, interactiveSearch);
        }

        private static string EpisodeCacheKey(int episodeId) => $"episode:{episodeId}";

        private static string SeasonCacheKey(int seriesId, int seasonNumber) => $"season:{seriesId}:{seasonNumber}";

        private CachedSearch FindCachedEntry(string key)
        {
            return _configService.SearchResultCacheLifetime > 0 ? _searchResultCache.Find(key) : null;
        }

        private CachedSearchResult FindCachedSearch(CachedSearch entry, int seriesId, List<Episode> episodes, bool monitoredOnly, bool userInvokedSearch, bool interactiveSearch)
        {
            if (entry == null || !entry.HasAnswers || !episodes.All(e => entry.EpisodeIds.Contains(e.Id)))
            {
                return null;
            }

            var result = Decide(entry, seriesId, episodes, monitoredOnly, userInvokedSearch, interactiveSearch, true);

            if (result.Decisions != null)
            {
                _logger.ProgressInfo("Using {0} search results for {1} cached at {2}", result.ReleaseCount, result.Series.Title, entry.SearchedAt.ToLocalTime());
            }

            return result.Decisions == null ? null : new CachedSearchResult(result.Decisions, entry.SearchedAt);
        }

        // Without requireComplete an interactive search gets the results of every indexer that answered, as when the remaining indexers were searched
        private (List<DownloadDecision> Decisions, int ReleaseCount, Series Series) Decide(CachedSearch entry, int seriesId, List<Episode> episodes, bool monitoredOnly, bool userInvokedSearch, bool interactiveSearch, bool requireComplete)
        {
            var series = _seriesService.GetSeries(seriesId);
            var decisions = new List<DownloadDecision>();
            var releaseCount = 0;

            // Decisions are made again on the original search criteria with the current series and episodes,
            // so the blocklist, queue and files apply as they are now while the episode matching of the search stays the same.
            foreach (var search in entry.Searches)
            {
                var criteria = search.Criteria.Clone();

                criteria.Series = series;
                criteria.Episodes = episodes.Where(e => search.Criteria.Episodes.Any(c => c.Id == e.Id)).ToList();
                criteria.MonitoredEpisodesOnly = monitoredOnly;
                criteria.UserInvokedSearch = userInvokedSearch;
                criteria.InteractiveSearch = interactiveSearch;

                if (criteria.Episodes.Empty())
                {
                    continue;
                }

                var indexers = GetIndexers(criteria);
                var indexerIds = indexers.Select(i => i.Definition.Id).ToHashSet();
                var releases = search.Reports.Where(r => indexerIds.Contains(r.IndexerId)).ToList();
                var searchDecisions = _makeDownloadDecision.GetSearchDecision(releases, criteria);

                // Interactive search shows everything its indexers return, so it is only served by a search that got an answer from all indexers it would search
                if (interactiveSearch && requireComplete && !IsCompleteSearch(indexers, search.IndexerIds, searchDecisions, criteria))
                {
                    _logger.Debug("Cached search results for {0} are missing results of some indexers, searching indexers", criteria);
                    return (null, 0, series);
                }

                releaseCount += releases.Count;

                decisions.AddRange(searchDecisions);
            }

            return (DeDupeDecisions(decisions), releaseCount, series);
        }

        // Complete means every indexer group was answered up to the one that found a good enough release, like a search in priority order would have searched them
        private bool IsCompleteSearch(List<IIndexer> indexers, HashSet<int> answeredIndexerIds, List<DownloadDecision> decisions, SearchCriteriaBase criteria)
        {
            var groups = GetIndexerGroups(indexers);
            var searchedIndexerIds = new HashSet<int>();

            for (var i = 0; i < groups.Count; i++)
            {
                if (groups[i].Any(indexer => !answeredIndexerIds.Contains(indexer.Definition.Id)))
                {
                    return false;
                }

                searchedIndexerIds.UnionWith(groups[i].Select(indexer => indexer.Definition.Id));

                if (i < groups.Count - 1 && decisions.Any(d => searchedIndexerIds.Contains(d.RemoteEpisode.Release.IndexerId) && IsGoodEnough(d, criteria.Episodes)))
                {
                    return true;
                }
            }

            return true;
        }

        // With previous the search only adds to the results of an earlier search, which keeps its search time
        private async Task<(List<DownloadDecision> Decisions, CachedSearch Entry)> SearchAndCache(string key, List<Episode> episodes, Func<Task<List<DownloadDecision>>> search, CachedSearch previous = null)
        {
            var entry = new CachedSearch { EpisodeIds = episodes.Select(e => e.Id).ToHashSet() };

            // Dispatch records the reports of every indexer search made below into the entry
            _currentSearch.Value = entry;

            var decisions = await search();

            if (previous != null)
            {
                entry = previous.Merge(entry);
            }

            var lifetime = _configService.SearchResultCacheLifetime;

            if (lifetime <= 0)
            {
                _searchResultCache.Clear();
            }
            else
            {
                // Cached<T> only evicts expired entries on lookup, so drop them here to keep the cache bounded
                _searchResultCache.ClearExpired();

                // Without an answer of any indexer there is nothing to serve, the next search has to ask the indexers again
                if (entry.HasAnswers)
                {
                    _searchResultCache.Set(key, entry, TimeSpan.FromMinutes(lifetime));
                }
            }

            return (decisions, entry);
        }

        private async Task<List<DownloadDecision>> SearchEpisode(Episode episode, bool userInvokedSearch, bool interactiveSearch)
        {
            var series = _seriesService.GetSeries(episode.SeriesId);

            if (series.SeriesType == SeriesTypes.Daily)
            {
                if (string.IsNullOrWhiteSpace(episode.AirDate))
                {
                    _logger.Error("Daily episode is missing an air date. Try refreshing the series info.");
                    throw new SearchFailedException("Air date is missing");
                }

                return await SearchDaily(series, episode, false, userInvokedSearch, interactiveSearch);
            }

            if (series.SeriesType == SeriesTypes.Anime)
            {
                if (episode.SeasonNumber == 0 &&
                    episode.SceneAbsoluteEpisodeNumber == null &&
                    episode.AbsoluteEpisodeNumber == null)
                {
                    // Search for special episodes in season 0 that don't have absolute episode numbers
                    return await SearchSpecial(series, new List<Episode> { episode }, false, userInvokedSearch, interactiveSearch);
                }

                return await SearchAnime(series, episode, false, userInvokedSearch, interactiveSearch);
            }

            if (episode.SeasonNumber == 0)
            {
                // Search for special episodes in season 0
                return await SearchSpecial(series, new List<Episode> { episode }, false, userInvokedSearch, interactiveSearch);
            }

            return await SearchSingle(series, episode, false, userInvokedSearch, interactiveSearch);
        }

        public async Task<List<DownloadDecision>> SeasonSearch(int seriesId, int seasonNumber, bool missingOnly, bool monitoredOnly, bool userInvokedSearch, bool interactiveSearch, bool useCache = true)
        {
            var episodes = _episodeService.GetEpisodesBySeason(seriesId, seasonNumber);

            if (missingOnly)
            {
                episodes = episodes.Where(e => !e.HasFile).ToList();
            }

            return await SeasonSearch(seriesId, seasonNumber, episodes, monitoredOnly, userInvokedSearch, interactiveSearch, useCache);
        }

        private async Task<List<DownloadDecision>> SearchSeason(int seriesId, int seasonNumber, List<Episode> episodes, bool monitoredOnly, bool userInvokedSearch, bool interactiveSearch)
        {
            var series = _seriesService.GetSeries(seriesId);

            if (series.SeriesType == SeriesTypes.Anime)
            {
                return await SearchAnimeSeason(series, episodes, monitoredOnly, userInvokedSearch, interactiveSearch);
            }

            if (series.SeriesType == SeriesTypes.Daily)
            {
                return await SearchDailySeason(series, episodes, monitoredOnly, userInvokedSearch, interactiveSearch);
            }

            var mappings = GetSceneSeasonMappings(series, episodes);

            var downloadDecisions = await SearchAll(mappings, mapping =>
            {
                if (mapping.SeasonNumber == 0)
                {
                    // search for special episodes in season 0
                    return SearchSpecial(series, mapping.Episodes, monitoredOnly, userInvokedSearch, interactiveSearch);
                }

                if (mapping.Episodes.Count == 1)
                {
                    var episodeSpec = Get<SingleEpisodeSearchCriteria>(series, mapping, monitoredOnly, userInvokedSearch, interactiveSearch);
                    episodeSpec.SeasonNumber = mapping.SeasonNumber;
                    episodeSpec.EpisodeNumber = mapping.EpisodeMapping.EpisodeNumber;

                    return Dispatch(indexer => indexer.Fetch(episodeSpec), episodeSpec);
                }

                var searchSpec = Get<SeasonSearchCriteria>(series, mapping, monitoredOnly, userInvokedSearch, interactiveSearch);
                searchSpec.SeasonNumber = mapping.SeasonNumber;

                return Dispatch(indexer => indexer.Fetch(searchSpec), searchSpec);
            });

            return DeDupeDecisions(downloadDecisions);
        }

        private List<SceneSeasonMapping> GetSceneSeasonMappings(Series series, List<Episode> episodes)
        {
            var dict = new Dictionary<SceneSeasonMapping, SceneSeasonMapping>();

            var sceneMappings = _sceneMapping.FindByTvdbId(series.TvdbId);

            // Group the episode by SceneSeasonNumber/SeasonNumber, in 99% of cases this will result in 1 groupedEpisode
            var groupedEpisodes = episodes.ToLookup(v => ((v.SceneSeasonNumber ?? v.SeasonNumber) * 100000) + v.SeasonNumber);

            foreach (var groupedEpisode in groupedEpisodes)
            {
                var episodeMappings = GetSceneEpisodeMappings(series, groupedEpisode.First(), sceneMappings);

                foreach (var episodeMapping in episodeMappings)
                {
                    var seasonMapping = new SceneSeasonMapping
                    {
                        Episodes = groupedEpisode.ToList(),
                        EpisodeMapping = episodeMapping,
                        SceneTitles = episodeMapping.SceneTitles,
                        SearchMode = episodeMapping.SearchMode,
                        SeasonNumber = episodeMapping.SeasonNumber
                    };

                    if (dict.TryGetValue(seasonMapping, out var existing))
                    {
                        existing.Episodes.AddRange(seasonMapping.Episodes);
                        existing.SceneTitles.AddRange(seasonMapping.SceneTitles);
                    }
                    else
                    {
                        dict[seasonMapping] = seasonMapping;
                    }
                }
            }

            foreach (var item in dict)
            {
                item.Value.Episodes = item.Value.Episodes.Distinct().ToList();
                item.Value.SceneTitles = item.Value.SceneTitles.Distinct(StringComparer.InvariantCultureIgnoreCase).ToList();
            }

            return dict.Values.ToList();
        }

        private List<SceneEpisodeMapping> GetSceneEpisodeMappings(Series series, Episode episode)
        {
            var dict = new Dictionary<SceneEpisodeMapping, SceneEpisodeMapping>();

            var sceneMappings = _sceneMapping.FindByTvdbId(series.TvdbId);

            var episodeMappings = GetSceneEpisodeMappings(series, episode, sceneMappings);

            foreach (var episodeMapping in episodeMappings)
            {
                if (dict.TryGetValue(episodeMapping, out var existing))
                {
                    existing.SceneTitles.AddRange(episodeMapping.SceneTitles);
                }
                else
                {
                    dict[episodeMapping] = episodeMapping;
                }
            }

            foreach (var item in dict)
            {
                item.Value.SceneTitles = item.Value.SceneTitles.Distinct(StringComparer.InvariantCultureIgnoreCase).ToList();
            }

            return dict.Values.ToList();
        }

        private IEnumerable<SceneEpisodeMapping> GetSceneEpisodeMappings(Series series, Episode episode, List<SceneMapping> sceneMappings)
        {
            var includeGlobal = true;

            foreach (var sceneMapping in sceneMappings)
            {
                // There are two kinds of mappings:
                // - Mapped on Release Season Number with sceneMapping.SceneSeasonNumber specified and optionally sceneMapping.SeasonNumber. This translates via episode.SceneSeasonNumber/SeasonNumber to specific episodes.
                // - Mapped on Episode Season Number with optionally sceneMapping.SeasonNumber. This translates from episode.SceneSeasonNumber/SeasonNumber to specific releases. (Filter by episode.SeasonNumber or globally)

                var ignoreSceneNumbering = sceneMapping.SceneOrigin == "tvdb" || sceneMapping.SceneOrigin == "unknown:tvdb";
                var mappingSceneSeasonNumber = sceneMapping.SceneSeasonNumber.NonNegative();
                var mappingSeasonNumber = sceneMapping.SeasonNumber.NonNegative();

                // Select scene or tvdb on the episode
                var mappedSeasonNumber = ignoreSceneNumbering ? episode.SeasonNumber : (episode.SceneSeasonNumber ?? episode.SeasonNumber);
                var releaseSeasonNumber = sceneMapping.SceneSeasonNumber.NonNegative() ?? mappedSeasonNumber;

                if (mappingSceneSeasonNumber.HasValue)
                {
                    // Apply the alternative mapping (release to scene/tvdb)
                    var mappedAltSeasonNumber = sceneMapping.SeasonNumber.NonNegative() ?? sceneMapping.SceneSeasonNumber.NonNegative() ?? mappedSeasonNumber;

                    // Check if the mapping applies to the current season
                    if (mappedAltSeasonNumber != mappedSeasonNumber)
                    {
                        continue;
                    }
                }
                else
                {
                    // Check if the mapping applies to the current season
                    if (mappingSeasonNumber.HasValue && mappingSeasonNumber.Value != episode.SeasonNumber)
                    {
                        continue;
                    }
                }

                if (sceneMapping.SearchTerm == series.Title && sceneMapping.FilterRegex.IsNullOrWhiteSpace())
                {
                    // Disable the implied mapping if we have an explicit mapping by the same name
                    includeGlobal = false;
                }

                // By default we do a alt title search in case indexers don't have the release properly indexed.  Services can override this behavior.
                var searchMode = sceneMapping.SearchMode ?? ((mappingSceneSeasonNumber.HasValue && series.CleanTitle != sceneMapping.SearchTerm.CleanSeriesTitle()) ? SearchMode.SearchTitle : SearchMode.Default);

                if (ignoreSceneNumbering)
                {
                    yield return new SceneEpisodeMapping
                    {
                        Episode = episode,
                        SearchMode = searchMode,
                        SceneTitles = new List<string> { sceneMapping.SearchTerm },
                        SeasonNumber = releaseSeasonNumber,
                        EpisodeNumber = episode.EpisodeNumber,
                        AbsoluteEpisodeNumber = episode.AbsoluteEpisodeNumber
                    };
                }
                else
                {
                    yield return new SceneEpisodeMapping
                    {
                        Episode = episode,
                        SearchMode = searchMode,
                        SceneTitles = new List<string> { sceneMapping.SearchTerm },
                        SeasonNumber = releaseSeasonNumber,
                        EpisodeNumber = episode.SceneEpisodeNumber ?? episode.EpisodeNumber,
                        AbsoluteEpisodeNumber = episode.SceneAbsoluteEpisodeNumber ?? episode.AbsoluteEpisodeNumber
                    };
                }
            }

            if (includeGlobal)
            {
                yield return new SceneEpisodeMapping
                {
                    Episode = episode,
                    SearchMode = SearchMode.Default,
                    SceneTitles = new List<string> { series.Title },
                    SeasonNumber = episode.SceneSeasonNumber ?? episode.SeasonNumber,
                    EpisodeNumber = episode.SceneEpisodeNumber ?? episode.EpisodeNumber,
                    AbsoluteEpisodeNumber = episode.SceneSeasonNumber ?? episode.AbsoluteEpisodeNumber
                };
            }
        }

        private async Task<List<DownloadDecision>> SearchSingle(Series series, Episode episode, bool monitoredOnly, bool userInvokedSearch, bool interactiveSearch)
        {
            var mappings = GetSceneEpisodeMappings(series, episode);

            var downloadDecisions = await SearchAll(mappings, mapping =>
            {
                var searchSpec = Get<SingleEpisodeSearchCriteria>(series, mapping, monitoredOnly, userInvokedSearch, interactiveSearch);
                searchSpec.SeasonNumber = mapping.SeasonNumber;
                searchSpec.EpisodeNumber = mapping.EpisodeNumber;

                return Dispatch(indexer => indexer.Fetch(searchSpec), searchSpec);
            });

            return DeDupeDecisions(downloadDecisions);
        }

        private async Task<List<DownloadDecision>> SearchDaily(Series series, Episode episode, bool monitoredOnly, bool userInvokedSearch, bool interactiveSearch)
        {
            var airDate = DateTime.ParseExact(episode.AirDate, Episode.AIR_DATE_FORMAT, CultureInfo.InvariantCulture);
            var searchSpec = Get<DailyEpisodeSearchCriteria>(series, new List<Episode> { episode }, monitoredOnly, userInvokedSearch, interactiveSearch);
            searchSpec.AirDate = airDate;

            var downloadDecisions = await Dispatch(indexer => indexer.Fetch(searchSpec), searchSpec);

            return DeDupeDecisions(downloadDecisions);
        }

        private async Task<List<DownloadDecision>> SearchAnime(Series series, Episode episode, bool monitoredOnly, bool userInvokedSearch, bool interactiveSearch, bool isSeasonSearch = false)
        {
            var searchSpec = Get<AnimeEpisodeSearchCriteria>(series, new List<Episode> { episode }, monitoredOnly, userInvokedSearch, interactiveSearch);

            searchSpec.IsSeasonSearch = isSeasonSearch;

            searchSpec.SeasonNumber = episode.SceneSeasonNumber ?? episode.SeasonNumber;
            searchSpec.EpisodeNumber = episode.SceneEpisodeNumber ?? episode.EpisodeNumber;
            searchSpec.AbsoluteEpisodeNumber = episode.SceneAbsoluteEpisodeNumber ?? episode.AbsoluteEpisodeNumber ?? 0;

            var downloadDecisions = await Dispatch(indexer => indexer.Fetch(searchSpec), searchSpec);

            return DeDupeDecisions(downloadDecisions);
        }

        private async Task<List<DownloadDecision>> SearchSpecial(Series series, List<Episode> episodes, bool monitoredOnly, bool userInvokedSearch, bool interactiveSearch)
        {
            var downloadDecisions = new List<DownloadDecision>();

            var searchSpec = Get<SpecialEpisodeSearchCriteria>(series, episodes, monitoredOnly, userInvokedSearch, interactiveSearch);

            // build list of queries for each episode in the form: "<series> <episode-title>"
            searchSpec.EpisodeQueryTitles = episodes.Where(e => !string.IsNullOrWhiteSpace(e.Title))
                                                    .Where(e => interactiveSearch || !monitoredOnly || e.Monitored)
                                                    .SelectMany(e => searchSpec.CleanSceneTitles.Select(title => title + " " + SearchCriteriaBase.GetCleanSceneTitle(e.Title)))
                                                    .Distinct(StringComparer.InvariantCultureIgnoreCase)
                                                    .ToArray();

            downloadDecisions.AddRange(await Dispatch(indexer => indexer.Fetch(searchSpec), searchSpec));

            // Search for each episode by season/episode number as well, episodes need to be monitored if it's not an interactive search
            var episodesToSearch = episodes.Where(e => interactiveSearch || !monitoredOnly || e.Monitored);

            downloadDecisions.AddRange(await SearchAll(episodesToSearch, episode => SearchSingle(series, episode, monitoredOnly, userInvokedSearch, interactiveSearch)));

            return DeDupeDecisions(downloadDecisions);
        }

        private async Task<List<DownloadDecision>> SearchAnimeSeason(Series series, List<Episode> episodes, bool monitoredOnly, bool userInvokedSearch, bool interactiveSearch)
        {
            // Episode needs to be monitored if it's not an interactive search
            // and Ensure episode has an airdate and has already aired
            var episodesToSearch = episodes
                .Where(ep => interactiveSearch || !monitoredOnly || ep.Monitored)
                .Where(ep => ep.AirDateUtc.HasValue && ep.AirDateUtc.Value.Before(DateTime.UtcNow))
                .ToList();

            var seasonsToSearch = GetSceneSeasonMappings(series, episodesToSearch)
                .GroupBy(ep => ep.SeasonNumber)
                .Select(epList => epList.First())
                .ToList();

            var downloadDecisions = await SearchAll(seasonsToSearch, season =>
            {
                var searchSpec = Get<AnimeSeasonSearchCriteria>(series, episodes, monitoredOnly, userInvokedSearch, interactiveSearch);
                searchSpec.SeasonNumber = season.SeasonNumber;

                return Dispatch(indexer => indexer.Fetch(searchSpec), searchSpec);
            });

            downloadDecisions.AddRange(await SearchEpisodes(downloadDecisions, episodesToSearch, interactiveSearch, episode => SearchAnime(series, episode, monitoredOnly, userInvokedSearch, interactiveSearch, true)));

            return DeDupeDecisions(downloadDecisions);
        }

        // Searches the episodes on their own after the season search, unless that search already found a good enough release.
        private async Task<List<DownloadDecision>> SearchEpisodes(List<DownloadDecision> bulkDecisions, List<Episode> episodes, bool interactiveSearch, Func<Episode, Task<List<DownloadDecision>>> search)
        {
            if (episodes.Empty())
            {
                return new List<DownloadDecision>();
            }

            if (_configService.EarlySearchReturn && !interactiveSearch && bulkDecisions.Any(d => IsGoodEnough(d, episodes)))
            {
                _logger.ProgressInfo("Found a release for all {0} searched episodes, skipping episode searches", episodes.Count);

                return new List<DownloadDecision>();
            }

            return await SearchAll(episodes, search);
        }

        // Starts all searches at once, SearchSlots limits how many of their indexer queries run. The decisions keep the order of the items.
        // Indexer rate limits reserve their request slots atomically, so concurrent searches still keep each indexer's interval
        private static async Task<List<DownloadDecision>> SearchAll<T>(IEnumerable<T> items, Func<T, Task<List<DownloadDecision>>> search)
        {
            var results = await Task.WhenAll(items.Select(search));

            return results.SelectMany(d => d).ToList();
        }

        private async Task<List<DownloadDecision>> SearchDailySeason(Series series, List<Episode> episodes, bool monitoredOnly, bool userInvokedSearch, bool interactiveSearch)
        {
            // Episode needs to be monitored if it's not an interactive search
            // and Ensure episode has an airdate
            var episodesToSearch = episodes
                .Where(ep => interactiveSearch || !monitoredOnly || ep.Monitored)
                .Where(ep => ep.AirDate.IsNotNullOrWhiteSpace())
                .ToList();

            var yearGroups = episodesToSearch.GroupBy(v => DateTime.ParseExact(v.AirDate, Episode.AIR_DATE_FORMAT, CultureInfo.InvariantCulture).Year);

            var downloadDecisions = await SearchAll(yearGroups, yearGroup =>
            {
                var yearEpisodes = yearGroup.ToList();

                if (yearEpisodes.Count == 1)
                {
                    return SearchDaily(series, yearEpisodes.First(), monitoredOnly, userInvokedSearch, interactiveSearch);
                }

                var searchSpec = Get<DailySeasonSearchCriteria>(series, yearEpisodes, monitoredOnly, userInvokedSearch, interactiveSearch);
                searchSpec.Year = yearGroup.Key;

                return Dispatch(indexer => indexer.Fetch(searchSpec), searchSpec);
            });

            return DeDupeDecisions(downloadDecisions);
        }

        private TSpec Get<TSpec>(Series series, List<Episode> episodes, bool monitoredOnly, bool userInvokedSearch, bool interactiveSearch)
            where TSpec : SearchCriteriaBase, new()
        {
            var spec = new TSpec();

            spec.Series = series;
            spec.SceneTitles = _sceneMapping.GetSceneNames(series.TvdbId,
                                                           episodes.Select(e => e.SeasonNumber).Distinct().ToList(),
                                                           episodes.Select(e => e.SceneSeasonNumber ?? e.SeasonNumber).Distinct().ToList());

            spec.Episodes = episodes;
            spec.MonitoredEpisodesOnly = monitoredOnly;
            spec.UserInvokedSearch = userInvokedSearch;
            spec.InteractiveSearch = interactiveSearch;

            if (!spec.SceneTitles.Contains(series.Title, StringComparer.InvariantCultureIgnoreCase))
            {
                spec.SceneTitles.Add(series.Title);
            }

            return spec;
        }

        private TSpec Get<TSpec>(Series series, SceneEpisodeMapping mapping, bool monitoredOnly, bool userInvokedSearch, bool interactiveSearch)
            where TSpec : SearchCriteriaBase, new()
        {
            var spec = new TSpec();

            spec.Series = series;
            spec.SceneTitles = mapping.SceneTitles;
            spec.SearchMode = mapping.SearchMode;

            spec.Episodes = new List<Episode> { mapping.Episode };
            spec.MonitoredEpisodesOnly = monitoredOnly;
            spec.UserInvokedSearch = userInvokedSearch;
            spec.InteractiveSearch = interactiveSearch;

            return spec;
        }

        private TSpec Get<TSpec>(Series series, SceneSeasonMapping mapping, bool monitoredOnly, bool userInvokedSearch, bool interactiveSearch)
            where TSpec : SearchCriteriaBase, new()
        {
            var spec = new TSpec();

            spec.Series = series;
            spec.SceneTitles = mapping.SceneTitles;
            spec.SearchMode = mapping.SearchMode;

            spec.Episodes = mapping.Episodes;
            spec.MonitoredEpisodesOnly = monitoredOnly;
            spec.UserInvokedSearch = userInvokedSearch;
            spec.InteractiveSearch = interactiveSearch;

            return spec;
        }

        private List<IIndexer> GetIndexers(SearchCriteriaBase criteriaBase)
        {
            var indexers = criteriaBase.InteractiveSearch ?
                _indexerFactory.InteractiveSearchEnabled() :
                _indexerFactory.AutomaticSearchEnabled();

            // Filter indexers to untagged indexers and indexers with intersecting tags
            return indexers.Where(i => i.Definition.Tags.Empty() || i.Definition.Tags.Intersect(criteriaBase.Series.Tags).Any()).ToList();
        }

        private async Task<List<DownloadDecision>> Dispatch(Func<IIndexer, Task<IList<ReleaseInfo>>> searchAction, SearchCriteriaBase criteriaBase)
        {
            var indexers = GetIndexers(criteriaBase);
            var onlyIndexerIds = _onlyIndexerIds.Value;

            if (onlyIndexerIds != null)
            {
                indexers = indexers.Where(i => onlyIndexerIds.Contains(i.Definition.Id)).ToList();
            }

            List<DownloadDecision> decisions;
            var reports = new List<ReleaseInfo>();
            var answeredIndexerIds = new HashSet<int>();
            var slots = SearchSlots.Value;

            // The remaining indexers of an interactive search are asked for explicitly, so they are searched at once
            var groups = onlyIndexerIds == null ? GetIndexerGroups(indexers) : new List<List<IIndexer>> { indexers };
            var searchedGroups = 0;

            if (slots != null)
            {
                await slots.WaitAsync();
            }

            try
            {
                _logger.ProgressInfo("Searching indexers for {0}. {1} active indexers", criteriaBase, indexers.Count);

                decisions = new List<DownloadDecision>();

                for (var i = 0; i < groups.Count; i++)
                {
                    var group = groups[i];
                    var tasks = group.Select(indexer => DispatchIndexer(searchAction, indexer, criteriaBase)).ToList();

                    searchedGroups++;

                    if (_configService.EarlySearchReturn && !criteriaBase.InteractiveSearch)
                    {
                        decisions.AddRange(await CollectDecisionsWithEarlyReturn(group, tasks, criteriaBase, reports, answeredIndexerIds));
                    }
                    else
                    {
                        var groupReports = (await Task.WhenAll(tasks)).SelectMany(x => x).ToList();

                        reports.AddRange(groupReports);
                        answeredIndexerIds.UnionWith(group.Select(indexer => indexer.Definition.Id));

                        _logger.ProgressDebug("Total of {0} reports were found for {1} from {2} indexers", groupReports.Count, criteriaBase, group.Count);

                        decisions.AddRange(_makeDownloadDecision.GetSearchDecision(groupReports, criteriaBase));
                    }

                    if (i < groups.Count - 1 && decisions.Any(d => IsGoodEnough(d, criteriaBase.Episodes)))
                    {
                        _logger.ProgressInfo("Found a good enough release for {0}, skipping {1} indexers of lower priority", criteriaBase, groups.Skip(i + 1).Sum(g => g.Count));
                        break;
                    }
                }
            }
            finally
            {
                slots?.Release();
            }

            var currentSearch = _currentSearch.Value;

            if (currentSearch != null)
            {
                var statuses = GetStatuses(groups, searchedGroups, answeredIndexerIds, reports, criteriaBase);

                // Failed indexers did not deliver their results, so the cache does not count them as answered
                var deliveredIndexerIds = answeredIndexerIds.Where(id => !criteriaBase.IndexerFailures.ContainsKey(id)).ToHashSet();

                // Episode searches of a season search run concurrently and record into the same entry
                lock (currentSearch.Searches)
                {
                    currentSearch.Searches.Add(new Search(criteriaBase, reports, deliveredIndexerIds, statuses));
                }
            }

            // Update the last search time for all episodes if at least 1 indexer was searched.
            if (indexers.Any())
            {
                var lastSearchTime = DateTime.UtcNow;
                _logger.Debug("Setting last search time to: {0}", lastSearchTime);

                criteriaBase.Episodes.ForEach(e => e.LastSearchTime = lastSearchTime);
                _episodeService.UpdateLastSearchTime(criteriaBase.Episodes);
            }

            return decisions;
        }

        private async Task<List<DownloadDecision>> CollectDecisionsWithEarlyReturn(List<IIndexer> indexers, List<Task<IList<ReleaseInfo>>> tasks, SearchCriteriaBase criteriaBase, List<ReleaseInfo> allReports, HashSet<int> answeredIndexerIds)
        {
            var minimumWait = TimeSpan.FromSeconds(_configService.EarlySearchReturnMinimumWait);
            var requiredPriority = _configService.EarlySearchReturnRequiredPriority;

            // Lower priority numbers are preferred, failed indexers count as answered
            var requiredTasks = tasks.Where((task, i) => requiredPriority > 0 && ((IndexerDefinition)indexers[i].Definition).Priority <= requiredPriority).ToList();

            var decisions = new List<DownloadDecision>();
            var pending = new List<Task>(tasks);
            var foundGoodRelease = false;
            var stopwatch = Stopwatch.StartNew();

            using var delayCancellation = new CancellationTokenSource();

            try
            {
                while (pending.Any())
                {
                    Task completed;

                    if (!foundGoodRelease || requiredTasks.Any(t => !t.IsCompleted))
                    {
                        completed = await Task.WhenAny(pending);
                    }
                    else
                    {
                        var remaining = minimumWait - stopwatch.Elapsed;

                        // Past the minimum wait, indexers that already answered are still read, only those still running are dropped
                        completed = remaining > TimeSpan.Zero
                            ? await Task.WhenAny(pending.Append(Task.Delay(remaining, delayCancellation.Token)))
                            : pending.FirstOrDefault(t => t.IsCompleted);
                    }

                    if (completed == null)
                    {
                        break;
                    }

                    if (!pending.Remove(completed))
                    {
                        continue;
                    }

                    // Decisions are made per release, so deciding on each indexer's results as they arrive matches deciding on all of them at once
                    var completedTask = (Task<IList<ReleaseInfo>>)completed;
                    var reports = (await completedTask).ToList();
                    var batchDecisions = _makeDownloadDecision.GetSearchDecision(reports, criteriaBase, false);

                    allReports.AddRange(reports);
                    answeredIndexerIds.Add(indexers[tasks.IndexOf(completedTask)].Definition.Id);

                    decisions.AddRange(batchDecisions);

                    foundGoodRelease = foundGoodRelease || batchDecisions.Any(d => IsGoodEnough(d, criteriaBase.Episodes));
                }
            }
            finally
            {
                delayCancellation.Cancel();
            }

            if (pending.Any())
            {
                _logger.ProgressInfo("Returning early for {0} after {1:0.#}s, ignoring results of {2} pending indexers", criteriaBase, stopwatch.Elapsed.TotalSeconds, pending.Count);
            }

            if (allReports.Any())
            {
                _logger.ProgressInfo("Processed {0} releases for {1} from {2} indexers", allReports.Count, criteriaBase, tasks.Count - pending.Count);
            }
            else
            {
                _logger.ProgressInfo("No results found");
            }

            return decisions;
        }

        // Search Indexers in Priority Order searches indexers of equal priority together, best priority first.
        // Indexers the early return always waits for share the first group, without the setting all indexers are one group
        private List<List<IIndexer>> GetIndexerGroups(List<IIndexer> indexers)
        {
            if (!_configService.EarlySearchReturn || !_configService.SearchIndexersInPriorityOrder)
            {
                return new List<List<IIndexer>> { indexers };
            }

            var requiredPriority = _configService.EarlySearchReturnRequiredPriority;

            return indexers.GroupBy(i => Math.Max(((IndexerDefinition)i.Definition).Priority, requiredPriority))
                           .OrderBy(g => g.Key)
                           .Select(g => g.ToList())
                           .ToList();
        }

        // Good enough means the searched episodes would not be upgraded from this release anymore.
        // Only a release covering every searched episode counts, a single episode must not end a season search.
        private bool IsGoodEnough(DownloadDecision decision, List<Episode> episodes)
        {
            var remoteEpisode = decision.RemoteEpisode;

            return decision.Approved &&
                   episodes.All(e => remoteEpisode.Episodes.Any(r => r.Id == e.Id)) &&
                   !_upgradableSpecification.CutoffNotMet(remoteEpisode.Series.QualityProfile.Value, remoteEpisode.ParsedEpisodeInfo.Quality, remoteEpisode.CustomFormats);
        }

        // Indexers of groups never reached were skipped, those still running when the search returned were not waited for
        private static List<IndexerSearchStatus> GetStatuses(List<List<IIndexer>> groups, int searchedGroups, HashSet<int> answeredIndexerIds, List<ReleaseInfo> reports, SearchCriteriaBase criteriaBase)
        {
            return groups.SelectMany((group, g) => group.Select(indexer =>
            {
                var id = indexer.Definition.Id;

                if (g >= searchedGroups)
                {
                    return GetStatus(indexer, IndexerSearchStatusType.Skipped, reports);
                }

                if (!answeredIndexerIds.Contains(id))
                {
                    return GetStatus(indexer, IndexerSearchStatusType.NotWaitedFor, reports);
                }

                if (criteriaBase.IndexerFailures.TryGetValue(id, out var failure))
                {
                    var timedOut = failure is TaskCanceledException or TimeoutException or WebException { Status: WebExceptionStatus.Timeout };

                    return GetStatus(indexer, timedOut ? IndexerSearchStatusType.TimedOut : IndexerSearchStatusType.Failed, reports, failure.Message);
                }

                return GetStatus(indexer, IndexerSearchStatusType.Searched, reports);
            })).ToList();
        }

        private static IndexerSearchStatus GetStatus(IIndexer indexer, IndexerSearchStatusType status, List<ReleaseInfo> reports, string message = null)
        {
            var id = indexer.Definition.Id;

            return new IndexerSearchStatus
            {
                IndexerId = id,
                Name = indexer.Definition.Name,
                Priority = ((IndexerDefinition)indexer.Definition).Priority,
                Status = status,
                ReleaseCount = reports.Count(r => r.IndexerId == id),
                Message = message
            };
        }

        private async Task<IList<ReleaseInfo>> DispatchIndexer(Func<IIndexer, Task<IList<ReleaseInfo>>> searchAction, IIndexer indexer, SearchCriteriaBase criteriaBase)
        {
            try
            {
                return await searchAction(indexer);
            }
            catch (Exception ex)
            {
                criteriaBase.IndexerFailures.TryAdd(indexer.Definition.Id, ex);
                _logger.Error(ex, "Error while searching for {0}", criteriaBase);
            }

            return Array.Empty<ReleaseInfo>();
        }

        private List<DownloadDecision> DeDupeDecisions(List<DownloadDecision> decisions)
        {
            // De-dupe reports by guid so duplicate results aren't returned. Pick the one with the least rejections and higher indexer priority.
            return decisions.GroupBy(d => d.RemoteEpisode.Release.Guid)
                .Select(d => d.OrderBy(v => v.Rejections.Count()).ThenBy(v => v.RemoteEpisode?.Release?.IndexerPriority ?? IndexerDefinition.DefaultPriority).First())
                .ToList();
        }

        internal class CachedSearch
        {
            public DateTime SearchedAt { get; set; } = DateTime.UtcNow;
            public HashSet<int> EpisodeIds { get; set; }

            public List<Search> Searches { get; } = new ();

            public bool HasAnswers => Searches.Any(s => s.IndexerIds.Any());

            // Adds a search of some indexers to this one, their new results and statuses replace the earlier ones
            public CachedSearch Merge(CachedSearch remaining)
            {
                var merged = new CachedSearch { SearchedAt = SearchedAt, EpisodeIds = EpisodeIds };
                var added = remaining.Searches.ToList();

                foreach (var search in Searches)
                {
                    var match = added.FirstOrDefault(r => SameCriteria(r.Criteria, search.Criteria));

                    if (match == null)
                    {
                        merged.Searches.Add(search);
                        continue;
                    }

                    added.Remove(match);

                    var newIds = match.Statuses.Select(s => s.IndexerId).ToHashSet();

                    merged.Searches.Add(new Search(
                        search.Criteria,
                        search.Reports.Where(r => !newIds.Contains(r.IndexerId)).Concat(match.Reports).ToList(),
                        search.IndexerIds.Where(id => !newIds.Contains(id)).Concat(match.IndexerIds).ToHashSet(),
                        search.Statuses.Where(s => !newIds.Contains(s.IndexerId)).Concat(match.Statuses).ToList()));
                }

                merged.Searches.AddRange(added);

                return merged;
            }

            private static bool SameCriteria(SearchCriteriaBase a, SearchCriteriaBase b)
            {
                return a.GetType() == b.GetType() &&
                       a.ToString() == b.ToString() &&
                       a.Episodes.Select(e => e.Id).OrderBy(id => id).SequenceEqual(b.Episodes.Select(e => e.Id).OrderBy(id => id));
            }
        }

        internal class Search
        {
            public SearchCriteriaBase Criteria { get; }
            public List<ReleaseInfo> Reports { get; }

            // The indexers that answered, a search returned early lacks the ones still pending
            public HashSet<int> IndexerIds { get; }
            public List<IndexerSearchStatus> Statuses { get; }

            public Search(SearchCriteriaBase criteria, List<ReleaseInfo> reports, HashSet<int> indexerIds, List<IndexerSearchStatus> statuses)
            {
                Criteria = criteria;
                Reports = reports;
                IndexerIds = indexerIds;
                Statuses = statuses;
            }
        }

        internal class InteractiveSearch
        {
            public CachedSearch Entry { get; set; }
            public int SeriesId { get; set; }

            // Null when the results came from the search result cache
            public DateTime? CachedAt { get; set; }

            // Indexers queried by this interactive search, the others answered an earlier search
            public HashSet<int> SearchedIndexerIds { get; set; }
        }
    }
}
