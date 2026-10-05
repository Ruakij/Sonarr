using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.IndexerSearch
{
    public class SeasonSearchCommand : Command
    {
        public int SeriesId { get; set; }
        public int SeasonNumber { get; set; }

        // Search indexers when no cached search result is acceptable, cached results are used either way
        public bool FallbackToIndexers { get; set; }

        public override bool SendUpdatesToClient => true;
    }
}
