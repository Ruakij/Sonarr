using System;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.IndexerSearch
{
    // Ordered from best to worst, an indexer searched several times for one item shows its worst outcome.
    // Cached comes first, so an indexer with one sent query among cached ones counts as searched
    public enum IndexerSearchStatusType
    {
        Cached,
        Searched,
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

        // When the oldest cached query of the indexer was fetched
        public DateTime? CachedAt { get; set; }

        // Durations of the HTTP requests of the query this status belongs to, pages and failed requests included,
        // an indexer without HTTP requests of its own counts one per query. Null for a query that was not sent or not waited for
        public List<double> RequestDurationsMs { get; set; }

        // QueryCount and MedianResponseMs cover the HTTP requests of the search, pages and failed requests included
        public int? QueryCount { get; set; }
        public int? MedianResponseMs { get; set; }

        // The requests of the successful queries of the indexer across all searches, automatic ones included
        public int? HistoryCount { get; set; }
        public int? HistoryMedianMs { get; set; }
        public int? HistoryLowMs { get; set; }
        public int? HistoryHighMs { get; set; }
    }

    public static class ResponseTimeStatistics
    {
        // Linear interpolation between the closest ranks
        public static double Percentile(IEnumerable<double> values, double percentile)
        {
            var sorted = values.OrderBy(v => v).ToList();

            if (!sorted.Any())
            {
                throw new ArgumentException("No values", nameof(values));
            }

            var rank = percentile / 100 * (sorted.Count - 1);
            var lower = (int)Math.Floor(rank);
            var upper = (int)Math.Ceiling(rank);

            return sorted[lower] + ((sorted[upper] - sorted[lower]) * (rank - lower));
        }

        public static double Median(IEnumerable<double> values) => Percentile(values, 50);
    }

    public class InteractiveSearchStatus
    {
        public DateTime? CachedAt { get; set; }
        public List<IndexerSearchStatus> Indexers { get; set; } = new ();
    }
}
