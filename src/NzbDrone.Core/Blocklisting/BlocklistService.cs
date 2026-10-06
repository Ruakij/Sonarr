using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Tv.Events;

namespace NzbDrone.Core.Blocklisting
{
    public interface IBlocklistService
    {
        bool Blocklisted(int seriesId, ReleaseInfo release);
        bool BlocklistedTorrentHash(int seriesId, string hash);
        PagingSpec<Blocklist> Paged(PagingSpec<Blocklist> pagingSpec);
        void Block(RemoteEpisode remoteEpisode, string message);
        void Delete(int id);
        void Delete(List<int> ids);
    }

    public class BlocklistService : IBlocklistService,
                                    IExecute<ClearBlocklistCommand>,
                                    IHandle<DownloadFailedEvent>,
                                    IHandleAsync<SeriesDeletedEvent>
    {
        private readonly IBlocklistRepository _blocklistRepository;
        private readonly IMainDatabase _database;

        public BlocklistService(IBlocklistRepository blocklistRepository, IMainDatabase database)
        {
            _blocklistRepository = blocklistRepository;
            _database = database;
        }

        public bool Blocklisted(int seriesId, ReleaseInfo release)
        {
            if (release.DownloadProtocol == DownloadProtocol.Torrent)
            {
                if (release is not TorrentInfo torrentInfo)
                {
                    return false;
                }

                if (torrentInfo.InfoHash.IsNotNullOrWhiteSpace())
                {
                    var blocklistedByTorrentInfohash = InRunCache()
                        ? SeriesBlocklist(seriesId).Where(b => SqliteLikeContains(b.TorrentInfoHash, torrentInfo.InfoHash))
                        : _blocklistRepository.BlocklistedByTorrentInfoHash(seriesId, torrentInfo.InfoHash);

                    return blocklistedByTorrentInfohash.Any(b => SameTorrent(b, torrentInfo));
                }

                return BlocklistedByTitle(seriesId, release.Title)
                    .Where(b => b.Protocol == DownloadProtocol.Torrent)
                    .Any(b => SameTorrent(b, torrentInfo));
            }

            return BlocklistedByTitle(seriesId, release.Title)
                .Where(b => b.Protocol == DownloadProtocol.Usenet)
                .Any(b => SameNzb(b, release));
        }

        // Within a decision run the blocklist of the series is loaded once and matched in memory. Only for SQLite,
        // whose LIKE rules are matched exactly; PostgreSQL ILIKE depends on the collation and keeps querying.
        private bool InRunCache()
        {
            return DecisionRunCache.Active && _database.DatabaseType == DatabaseType.SQLite;
        }

        private List<Blocklist> SeriesBlocklist(int seriesId)
        {
            return DecisionRunCache.GetOrAdd("BlocklistBySeries", seriesId, () => _blocklistRepository.BlocklistedBySeries(seriesId));
        }

        private IEnumerable<Blocklist> BlocklistedByTitle(int seriesId, string title)
        {
            return InRunCache()
                ? SeriesBlocklist(seriesId).Where(b => SqliteLikeContains(b.SourceTitle, title))
                : _blocklistRepository.BlocklistedByTitle(seriesId, title);
        }

        // value LIKE '%' || search || '%' as SQLite evaluates it: % and _ in the search are wildcards,
        // _ matches one character, only ASCII letters compare case-insensitively and NULL never matches.
        public static bool SqliteLikeContains(string value, string search)
        {
            if (value == null || search == null)
            {
                return false;
            }

            var text = value.EnumerateRunes().Select(FoldAscii).ToArray();
            var pattern = ("%" + search + "%").EnumerateRunes().Select(FoldAscii).ToArray();

            var t = 0;
            var p = 0;
            var star = -1;
            var starText = 0;

            while (t < text.Length)
            {
                if (p < pattern.Length && pattern[p] == '%')
                {
                    star = p++;
                    starText = t;
                }
                else if (p < pattern.Length && (pattern[p] == '_' || pattern[p] == text[t]))
                {
                    p++;
                    t++;
                }
                else if (star >= 0)
                {
                    p = star + 1;
                    t = ++starText;
                }
                else
                {
                    return false;
                }
            }

            while (p < pattern.Length && pattern[p] == '%')
            {
                p++;
            }

            return p == pattern.Length;
        }

        private static int FoldAscii(System.Text.Rune rune)
        {
            return rune.Value is >= 'A' and <= 'Z' ? rune.Value + 32 : rune.Value;
        }

        public bool BlocklistedTorrentHash(int seriesId, string hash)
        {
            return _blocklistRepository.BlocklistedByTorrentInfoHash(seriesId, hash).Any(b =>
                b.TorrentInfoHash.Equals(hash, StringComparison.InvariantCultureIgnoreCase));
        }

        public PagingSpec<Blocklist> Paged(PagingSpec<Blocklist> pagingSpec)
        {
            return _blocklistRepository.GetPaged(pagingSpec);
        }

        public void Block(RemoteEpisode remoteEpisode, string message)
        {
            var blocklist = new Blocklist
                            {
                                SeriesId = remoteEpisode.Series.Id,
                                EpisodeIds = remoteEpisode.Episodes.Select(e => e.Id).ToList(),
                                SourceTitle =  remoteEpisode.Release.Title,
                                Quality = remoteEpisode.ParsedEpisodeInfo.Quality,
                                Date = DateTime.UtcNow,
                                PublishedDate = remoteEpisode.Release.PublishDate,
                                Size = remoteEpisode.Release.Size,
                                Indexer = remoteEpisode.Release.Indexer,
                                Protocol = remoteEpisode.Release.DownloadProtocol,
                                Message = message,
                                Languages = remoteEpisode.ParsedEpisodeInfo.Languages
                            };

            if (remoteEpisode.Release is TorrentInfo torrentRelease)
            {
                blocklist.TorrentInfoHash = torrentRelease.InfoHash;
            }

            _blocklistRepository.Insert(blocklist);
        }

        public void Delete(int id)
        {
            _blocklistRepository.Delete(id);
        }

        public void Delete(List<int> ids)
        {
            _blocklistRepository.DeleteMany(ids);
        }

        private bool SameNzb(Blocklist item, ReleaseInfo release)
        {
            if (item.PublishedDate == release.PublishDate)
            {
                return true;
            }

            if (!HasSameIndexer(item, release.Indexer) &&
                HasSamePublishedDate(item, release.PublishDate) &&
                HasSameSize(item, release.Size))
            {
                return true;
            }

            return false;
        }

        private bool SameTorrent(Blocklist item, TorrentInfo release)
        {
            if (release.InfoHash.IsNotNullOrWhiteSpace())
            {
                return release.InfoHash.Equals(item.TorrentInfoHash, StringComparison.InvariantCultureIgnoreCase);
            }

            return HasSameIndexer(item, release.Indexer);
        }

        private bool HasSameIndexer(Blocklist item, string indexer)
        {
            if (item.Indexer.IsNullOrWhiteSpace())
            {
                return true;
            }

            return item.Indexer.Equals(indexer, StringComparison.InvariantCultureIgnoreCase);
        }

        private bool HasSamePublishedDate(Blocklist item, DateTime publishedDate)
        {
            if (!item.PublishedDate.HasValue)
            {
                return true;
            }

            return item.PublishedDate.Value.AddMinutes(-2) <= publishedDate &&
                   item.PublishedDate.Value.AddMinutes(2) >= publishedDate;
        }

        private bool HasSameSize(Blocklist item, long size)
        {
            if (!item.Size.HasValue)
            {
                return true;
            }

            var difference = Math.Abs(item.Size.Value - size);

            return difference <= 2.Megabytes();
        }

        public void Execute(ClearBlocklistCommand message)
        {
            _blocklistRepository.Purge();
        }

        public void Handle(DownloadFailedEvent message)
        {
            var blocklist = new Blocklist
            {
                SeriesId = message.SeriesId,
                EpisodeIds = message.EpisodeIds,
                SourceTitle = message.SourceTitle,
                Quality = message.Quality,
                Date = DateTime.UtcNow,
                PublishedDate = DateTime.Parse(message.Data.GetValueOrDefault("publishedDate")),
                Size = long.Parse(message.Data.GetValueOrDefault("size", "0")),
                Indexer = message.Data.GetValueOrDefault("indexer"),
                Protocol = (DownloadProtocol)Convert.ToInt32(message.Data.GetValueOrDefault("protocol")),
                Message = message.Message,
                Languages = message.Languages,
                TorrentInfoHash = message.TrackedDownload?.Protocol == DownloadProtocol.Torrent
                    ? message.TrackedDownload.DownloadItem.DownloadId
                    : message.Data.GetValueOrDefault("torrentInfoHash", null)
            };

            if (Enum.TryParse(message.Data.GetValueOrDefault("indexerFlags"), true, out IndexerFlags flags))
            {
                blocklist.IndexerFlags = flags;
            }

            if (Enum.TryParse(message.Data.GetValueOrDefault("releaseType"), true, out ReleaseType releaseType))
            {
                blocklist.ReleaseType = releaseType;
            }

            _blocklistRepository.Insert(blocklist);
        }

        public void HandleAsync(SeriesDeletedEvent message)
        {
            _blocklistRepository.DeleteForSeriesIds(message.Series.Select(m => m.Id).ToList());
        }
    }
}
