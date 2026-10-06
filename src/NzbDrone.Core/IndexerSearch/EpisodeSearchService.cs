using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Queue;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.IndexerSearch
{
    public class EpisodeSearchService : IExecute<EpisodeSearchCommand>,
                                        IExecute<MissingEpisodeSearchCommand>,
                                        IExecute<CutoffUnmetEpisodeSearchCommand>
    {
        private readonly ISearchForReleases _releaseSearchService;
        private readonly IProcessDownloadDecisions _processDownloadDecisions;
        private readonly IEpisodeService _episodeService;
        private readonly IEpisodeCutoffService _episodeCutoffService;
        private readonly IQueueService _queueService;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public EpisodeSearchService(ISearchForReleases releaseSearchService,
                                    IProcessDownloadDecisions processDownloadDecisions,
                                    IEpisodeService episodeService,
                                    IEpisodeCutoffService episodeCutoffService,
                                    IQueueService queueService,
                                    IConfigService configService,
                                    Logger logger)
        {
            _releaseSearchService = releaseSearchService;
            _processDownloadDecisions = processDownloadDecisions;
            _episodeService = episodeService;
            _episodeCutoffService = episodeCutoffService;
            _queueService = queueService;
            _configService = configService;
            _logger = logger;
        }

        private async Task SearchForBulkEpisodes(List<Episode> episodes, bool monitoredOnly, bool userInvokedSearch)
        {
            _logger.ProgressInfo("Performing search for {0} episodes", episodes.Count);
            var downloadedCount = 0;
            var groups = new List<EpisodeSearchGroup>();

            foreach (var series in episodes.GroupBy(e => e.SeriesId))
            {
                foreach (var season in series.Select(e => e).GroupBy(e => e.SeasonNumber))
                {
                    groups.Add(new EpisodeSearchGroup
                    {
                        SeriesId = series.Key,
                        SeasonNumber = season.Key,
                        Episodes = season.ToList()
                    });
                }
            }

            var orderedGroups = groups.OrderBy(g => g.Episodes.Min(e => e.LastSearchTime ?? DateTime.MinValue));

            downloadedCount = await SearchAndProcess(orderedGroups, _configService.SearchConcurrency, _processDownloadDecisions, async group =>
            {
                var seriesId = group.SeriesId;
                var seasonNumber = group.SeasonNumber;
                var groupEpisodes = group.Episodes;

                if (groupEpisodes.Count > 1)
                {
                    try
                    {
                        return await _releaseSearchService.SeasonSearch(seriesId, seasonNumber, groupEpisodes, monitoredOnly, userInvokedSearch, false);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Unable to search for episodes in season {0} of [{1}]", seasonNumber, seriesId);
                        return null;
                    }
                }

                var episode = groupEpisodes.First();

                try
                {
                    return await _releaseSearchService.EpisodeSearch(episode, userInvokedSearch, false);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Unable to search for episode: [{0}]", episode);
                    return null;
                }
            });

            _logger.ProgressInfo("Completed search for {0} episodes. {1} reports downloaded.", episodes.Count, downloadedCount);
        }

        // Runs up to `concurrency` searches at once and processes their decisions one after another in the given order.
        // All indexer queries of the searches share `concurrency` slots, however many queries a single search makes.
        // Searches running ahead decide before earlier grabs reach the queue, so releases for episodes grabbed earlier are dropped,
        // a multi-season pack found by several season searches is grabbed once. A search returning null is skipped.
        internal static async Task<int> SearchAndProcess<T>(IEnumerable<T> items, int concurrency, IProcessDownloadDecisions processDownloadDecisions, Func<T, Task<List<DownloadDecision>>> search)
        {
            ReleaseSearchService.SearchSlots.Value = new SemaphoreSlim(Math.Max(1, concurrency));

            var pending = items.ToList();
            var searches = new List<Task<List<DownloadDecision>>>();
            var grabbedEpisodeIds = new HashSet<int>();
            var grabbedCount = 0;

            for (var i = 0; i < pending.Count; i++)
            {
                while (searches.Count < pending.Count && searches.Count < i + Math.Max(1, concurrency))
                {
                    searches.Add(search(pending[searches.Count]));
                }

                var decisions = await searches[i];

                if (decisions == null)
                {
                    continue;
                }

                grabbedCount += (await ProcessNotGrabbed(processDownloadDecisions, decisions, grabbedEpisodeIds)).Grabbed.Count;
            }

            return grabbedCount;
        }

        // Skips releases for episodes grabbed earlier, so a multi-episode release is grabbed once
        private static async Task<ProcessedDecisions> ProcessNotGrabbed(IProcessDownloadDecisions processDownloadDecisions, List<DownloadDecision> decisions, HashSet<int> grabbedEpisodeIds)
        {
            var processed = await processDownloadDecisions.ProcessDecisions(decisions.Where(d => d.RemoteEpisode.Episodes.None(e => grabbedEpisodeIds.Contains(e.Id))).ToList());

            grabbedEpisodeIds.UnionWith(processed.Grabbed.SelectMany(d => d.RemoteEpisode.Episodes).Select(e => e.Id));

            return processed;
        }

        private bool IsMonitored(bool episodeMonitored, bool seriesMonitored)
        {
            return episodeMonitored && seriesMonitored;
        }

        public void Execute(EpisodeSearchCommand message)
        {
            var userInvokedSearch = message.Trigger == CommandTrigger.Manual;

            // Cached releases are grabbed while going through the episodes, so only searches without them run in parallel.
            // Searches started by hand query the indexers, their results still refresh the cache
            if (!message.FallbackToIndexers)
            {
                var grabbed = SearchAndProcess(message.EpisodeIds, _configService.SearchConcurrency, _processDownloadDecisions, episodeId => _releaseSearchService.EpisodeSearch(episodeId, userInvokedSearch, false, !userInvokedSearch)).GetAwaiter().GetResult();

                _logger.ProgressInfo("Episode search completed. {0} reports downloaded.", grabbed);

                return;
            }

            var grabbedEpisodeIds = new HashSet<int>();

            foreach (var episodeId in message.EpisodeIds)
            {
                if (grabbedEpisodeIds.Contains(episodeId))
                {
                    continue;
                }

                if (GrabCachedRelease(() => _releaseSearchService.CachedEpisodeSearch(episodeId, false), _processDownloadDecisions, _logger, $"episode [{episodeId}]", grabbedEpisodeIds))
                {
                    continue;
                }

                var decisions = _releaseSearchService.EpisodeSearch(episodeId, userInvokedSearch, false, false).GetAwaiter().GetResult();
                var processed = ProcessNotGrabbed(_processDownloadDecisions, decisions, grabbedEpisodeIds).GetAwaiter().GetResult();

                _logger.ProgressInfo("Episode search completed. {0} reports downloaded.", processed.Grabbed.Count);
            }
        }

        internal static bool GrabCachedRelease(Func<CachedSearchResult> cachedSearch, IProcessDownloadDecisions processDownloadDecisions, Logger logger, string item, HashSet<int> grabbedEpisodeIds = null)
        {
            try
            {
                var decisions = cachedSearch()?.Decisions;

                if (decisions == null || decisions.Empty())
                {
                    return false;
                }

                var processed = ProcessNotGrabbed(processDownloadDecisions, decisions, grabbedEpisodeIds ?? new HashSet<int>()).GetAwaiter().GetResult();

                if (processed.Grabbed.Any() || processed.Pending.Any())
                {
                    logger.ProgressInfo("Used cached search results for {0}. {1} reports downloaded.", item, processed.Grabbed.Count);
                    return true;
                }

                logger.Debug("No cached search result for {0} is acceptable anymore, searching indexers", item);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Unable to grab cached search result for {0}, searching indexers", item);
            }

            return false;
        }

        public void Execute(MissingEpisodeSearchCommand message)
        {
            var monitored = message.Monitored;
            List<Episode> episodes;

            if (message.SeriesId.HasValue)
            {
                episodes = _episodeService.GetEpisodeBySeries(message.SeriesId.Value)
                                          .Where(e => e.Monitored == monitored &&
                                                 !e.HasFile &&
                                                 e.AirDateUtc.HasValue &&
                                                 e.AirDateUtc.Value.Before(DateTime.UtcNow))
                                          .ToList();
            }
            else
            {
                var pagingSpec = new PagingSpec<Episode>
                                 {
                                     Page = 1,
                                     PageSize = 1000000,
                                     SortDirection = SortDirection.Ascending,
                                     SortKey = "Id"
                                 };

                if (monitored)
                {
                    pagingSpec.FilterExpressions.Add(v => v.Monitored == true && v.Series.Monitored == true);
                }
                else
                {
                    pagingSpec.FilterExpressions.Add(v => v.Monitored == false || v.Series.Monitored == false);
                }

                episodes = _episodeService.EpisodesWithoutFiles(pagingSpec).Records.ToList();
            }

            var queue = _queueService.GetQueue().Where(q => q.Episode != null).Select(q => q.Episode.Id);
            var missing = episodes.Where(e => !queue.Contains(e.Id)).ToList();

            SearchForBulkEpisodes(missing, monitored, message.Trigger == CommandTrigger.Manual).GetAwaiter().GetResult();
        }

        public void Execute(CutoffUnmetEpisodeSearchCommand message)
        {
            var monitored = message.Monitored;

            var pagingSpec = new PagingSpec<Episode>
                             {
                                 Page = 1,
                                 PageSize = 100000,
                                 SortDirection = SortDirection.Ascending,
                                 SortKey = "Id"
                             };

            if (message.SeriesId.HasValue)
            {
                var seriesId = message.SeriesId.Value;
                pagingSpec.FilterExpressions.Add(v => v.SeriesId == seriesId);
            }

            if (monitored)
            {
                pagingSpec.FilterExpressions.Add(v => v.Monitored == true && v.Series.Monitored == true);
            }
            else
            {
                pagingSpec.FilterExpressions.Add(v => v.Monitored == false || v.Series.Monitored == false);
            }

            var episodes = _episodeCutoffService.EpisodesWhereCutoffUnmet(pagingSpec).Records.ToList();
            var queue = _queueService.GetQueue().Where(q => q.Episode != null).Select(q => q.Episode.Id);
            var cutoffUnmet = episodes.Where(e => !queue.Contains(e.Id)).ToList();

            SearchForBulkEpisodes(cutoffUnmet, monitored, message.Trigger == CommandTrigger.Manual).GetAwaiter().GetResult();
        }
    }
}
