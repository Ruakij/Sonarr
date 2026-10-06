using System;
using System.Collections.Generic;

namespace NzbDrone.Core.IndexerSearch
{
    // Ordered from best to worst, an indexer searched several times for one item shows its worst outcome
    public enum IndexerSearchStatusType
    {
        Searched,
        Cached,
        Skipped,
        NotWaitedFor,
        Failed,
        TimedOut
    }

    public class IndexerSearchStatus
    {
        public int IndexerId { get; set; }
        public string Name { get; set; }
        public int Priority { get; set; }
        public IndexerSearchStatusType Status { get; set; }
        public int ReleaseCount { get; set; }
        public string Message { get; set; }
    }

    public class InteractiveSearchStatus
    {
        public DateTime? CachedAt { get; set; }
        public List<IndexerSearchStatus> Indexers { get; set; } = new ();
    }
}
