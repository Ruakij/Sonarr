using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.IndexerSearch;

namespace Sonarr.Api.V3.Indexers
{
    public class ReleaseSearchStatusResource
    {
        public DateTime? CachedAt { get; set; }
        public List<IndexerSearchStatusResource> Indexers { get; set; }
    }

    public class IndexerSearchStatusResource
    {
        public int IndexerId { get; set; }
        public string Name { get; set; }
        public int Priority { get; set; }
        public IndexerSearchStatusType Status { get; set; }
        public int ReleaseCount { get; set; }
        public string Message { get; set; }
    }

    public static class ReleaseSearchStatusResourceMapper
    {
        public static ReleaseSearchStatusResource ToResource(this InteractiveSearchStatus model)
        {
            return new ReleaseSearchStatusResource
            {
                CachedAt = model.CachedAt,
                Indexers = model.Indexers.Select(i => new IndexerSearchStatusResource
                {
                    IndexerId = i.IndexerId,
                    Name = i.Name,
                    Priority = i.Priority,
                    Status = i.Status,
                    ReleaseCount = i.ReleaseCount,
                    Message = i.Message
                }).ToList()
            };
        }
    }
}
