using System;
using System.Collections.Generic;
using NzbDrone.Core.DecisionEngine;

namespace NzbDrone.Core.IndexerSearch
{
    public class CachedSearchResult
    {
        public List<DownloadDecision> Decisions { get; }
        public DateTime SearchedAt { get; }

        public CachedSearchResult(List<DownloadDecision> decisions, DateTime searchedAt)
        {
            Decisions = decisions;
            SearchedAt = searchedAt;
        }
    }
}
