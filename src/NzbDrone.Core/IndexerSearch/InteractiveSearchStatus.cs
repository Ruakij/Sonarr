using System;
using System.Collections.Generic;
using System.Linq;

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

        // When the results of a cached indexer were searched
        public DateTime? CachedAt { get; set; }

        // Duration of the single query this status belongs to, failed queries included
        public double? ResponseMs { get; set; }

        public int? QueryCount { get; set; }
        public int? MedianResponseMs { get; set; }

        // Successful queries of the indexer across all searches, automatic ones included
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
