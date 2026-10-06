using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Profiles.Releases
{
    public interface IReleaseProfileService
    {
        List<ReleaseProfile> All();
        List<ReleaseProfile> AllForTag(int tagId);
        List<ReleaseProfile> AllForTags(HashSet<int> tagIds);
        List<ReleaseProfile> EnabledForTags(HashSet<int> tagIds, int indexerId);
        ReleaseProfile Get(int id);
        void Delete(int id);
        ReleaseProfile Add(ReleaseProfile restriction);
        ReleaseProfile Update(ReleaseProfile restriction);
    }

    public class ReleaseProfileService : IReleaseProfileService
    {
        private readonly IRestrictionRepository _repo;
        private readonly ICached<List<ReleaseProfile>> _cache;
        private readonly Logger _logger;

        public ReleaseProfileService(IRestrictionRepository repo, ICacheManager cacheManager, Logger logger)
        {
            _repo = repo;
            _cache = cacheManager.GetCache<List<ReleaseProfile>>(typeof(ReleaseProfile), "profiles");
            _logger = logger;
        }

        private List<ReleaseProfile> Cached()
        {
            return _cache.Get("all", () => _repo.All().ToList());
        }

        public List<ReleaseProfile> All()
        {
            return Cached().ToList();
        }

        public List<ReleaseProfile> AllForTag(int tagId)
        {
            return Cached().Where(r => r.Tags.Contains(tagId)).ToList();
        }

        public List<ReleaseProfile> AllForTags(HashSet<int> tagIds)
        {
            return Cached().Where(r => r.Tags.Intersect(tagIds).Any() || r.Tags.Empty()).ToList();
        }

        public List<ReleaseProfile> EnabledForTags(HashSet<int> tagIds, int indexerId)
        {
            return AllForTags(tagIds)
                .Where(r => r.Enabled)
                .Where(r => r.IndexerId == indexerId || r.IndexerId == 0).ToList();
        }

        public ReleaseProfile Get(int id)
        {
            return _repo.Get(id);
        }

        public void Delete(int id)
        {
            _repo.Delete(id);
            _cache.Clear();
        }

        public ReleaseProfile Add(ReleaseProfile restriction)
        {
            var result = _repo.Insert(restriction);
            _cache.Clear();

            return result;
        }

        public ReleaseProfile Update(ReleaseProfile restriction)
        {
            var result = _repo.Update(restriction);
            _cache.Clear();

            return result;
        }
    }
}
