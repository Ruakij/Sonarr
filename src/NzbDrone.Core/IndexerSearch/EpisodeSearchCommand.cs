using System.Collections.Generic;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.IndexerSearch
{
    public class EpisodeSearchCommand : Command
    {
        public List<int> EpisodeIds { get; set; }

        // Search indexers when no cached search result is acceptable, cached results are used either way
        public bool FallbackToIndexers { get; set; }

        public override bool SendUpdatesToClient => true;

        public EpisodeSearchCommand()
        {
        }

        public EpisodeSearchCommand(List<int> episodeIds)
        {
            EpisodeIds = episodeIds;
        }
    }
}
