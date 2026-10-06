using NLog;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.IndexerSearch
{
    public class SeasonSearchService : IExecute<SeasonSearchCommand>
    {
        private readonly ISearchForReleases _releaseSearchService;
        private readonly IProcessDownloadDecisions _processDownloadDecisions;
        private readonly Logger _logger;

        public SeasonSearchService(ISearchForReleases releaseSearchService,
                                   IProcessDownloadDecisions processDownloadDecisions,
                                   Logger logger)
        {
            _releaseSearchService = releaseSearchService;
            _processDownloadDecisions = processDownloadDecisions;
            _logger = logger;
        }

        public void Execute(SeasonSearchCommand message)
        {
            if (message.FallbackToIndexers &&
                EpisodeSearchService.GrabCachedRelease(() => _releaseSearchService.CachedSeasonSearch(message.SeriesId, message.SeasonNumber), _processDownloadDecisions, _logger, $"season {message.SeasonNumber} of [{message.SeriesId}]"))
            {
                return;
            }

            var userInvokedSearch = message.Trigger == CommandTrigger.Manual;

            // Searches started by hand query the indexers, their results still refresh the cache
            var decisions = _releaseSearchService.SeasonSearch(message.SeriesId, message.SeasonNumber, false, true, userInvokedSearch, false, !message.FallbackToIndexers && !userInvokedSearch).GetAwaiter().GetResult();
            var processed = _processDownloadDecisions.ProcessDecisions(decisions).GetAwaiter().GetResult();

            _logger.ProgressInfo("Season search completed. {0} reports downloaded.", processed.Grabbed.Count);
        }
    }
}
