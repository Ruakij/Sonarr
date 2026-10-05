namespace NzbDrone.Core.IndexerSearch.Definitions
{
    // Re-evaluates cached search results without the season and episode number matching of the original search
    public class CachedReleasesSearchCriteria : SearchCriteriaBase
    {
        public override string ToString()
        {
            return string.Format("[{0} : cached]", Series.Title);
        }
    }
}
