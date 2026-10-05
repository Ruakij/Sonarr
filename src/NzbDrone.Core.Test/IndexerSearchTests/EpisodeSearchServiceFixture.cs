using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DataAugmentation.Scene;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    [TestFixture]
    public class EpisodeSearchServiceFixture : CoreTest<EpisodeSearchService>
    {
        private Series _series;
        private List<Episode> _episodes;
        private List<ReleaseInfo> _releases;
        private HashSet<string> _blocklistedGuids;
        private HashSet<string> _delayedGuids;
        private Mock<IIndexer> _indexer;

        [SetUp]
        public void Setup()
        {
            _series = new Series { Id = 1, Title = "Series", TvdbId = 10, SeriesType = SeriesTypes.Standard, Monitored = true, Tags = new HashSet<int>() };

            _episodes = new List<Episode>
            {
                new Episode { Id = 1, SeriesId = _series.Id, SeasonNumber = 1, EpisodeNumber = 1, Monitored = true },
                new Episode { Id = 2, SeriesId = _series.Id, SeasonNumber = 1, EpisodeNumber = 2, Monitored = true }
            };

            _releases = Enumerable.Range(1, 3)
                .Select(i => new ReleaseInfo { Guid = $"guid{i}", Title = $"Series.S01E01.Release{i}", DownloadProtocol = DownloadProtocol.Usenet })
                .ToList();

            _blocklistedGuids = new HashSet<string>();
            _delayedGuids = new HashSet<string>();

            Mocker.SetConstant<ICacheManager>(Mocker.Resolve<CacheManager>());
            Mocker.SetConstant<ISearchForReleases>(Mocker.Resolve<ReleaseSearchService>());
            Mocker.SetConstant<IProcessDownloadDecisions>(Mocker.Resolve<ProcessDownloadDecisions>());

            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.AutoRedownloadFailedCacheLifetime)
                  .Returns(10);

            Mocker.GetMock<ISeriesService>()
                  .Setup(s => s.GetSeries(_series.Id))
                  .Returns(_series);

            Mocker.GetMock<IEpisodeService>()
                  .Setup(s => s.GetEpisode(It.IsAny<int>()))
                  .Returns<int>(id => _episodes.Single(e => e.Id == id));

            Mocker.GetMock<IEpisodeService>()
                  .Setup(s => s.GetEpisodesBySeason(_series.Id, 1))
                  .Returns(() => _episodes.ToList());

            Mocker.GetMock<ISceneMappingService>()
                  .Setup(s => s.FindByTvdbId(It.IsAny<int>()))
                  .Returns(new List<SceneMapping>());

            Mocker.GetMock<ISceneMappingService>()
                  .Setup(s => s.GetSceneNames(It.IsAny<int>(), It.IsAny<List<int>>(), It.IsAny<List<int>>()))
                  .Returns(new List<string>());

            _indexer = Mocker.GetMock<IIndexer>();
            _indexer.SetupGet(s => s.Definition).Returns(new IndexerDefinition { Id = 1 });
            _indexer.Setup(s => s.Fetch(It.IsAny<SingleEpisodeSearchCriteria>()))
                    .Returns(() => Task.FromResult<IList<ReleaseInfo>>(_releases.ToList()));
            _indexer.Setup(s => s.Fetch(It.IsAny<SeasonSearchCriteria>()))
                    .Returns(() => Task.FromResult<IList<ReleaseInfo>>(_releases.ToList()));

            Mocker.GetMock<IIndexerFactory>()
                  .Setup(s => s.AutomaticSearchEnabled(true))
                  .Returns(new List<IIndexer> { _indexer.Object });

            Mocker.GetMock<IMakeDownloadDecision>()
                  .Setup(s => s.GetSearchDecision(It.IsAny<List<ReleaseInfo>>(), It.IsAny<SearchCriteriaBase>()))
                  .Returns<List<ReleaseInfo>, SearchCriteriaBase>((reports, criteria) => reports.Select(r => GetDecision(r, criteria)).ToList());

            Mocker.GetMock<IPrioritizeDownloadDecision>()
                  .Setup(s => s.PrioritizeDecisions(It.IsAny<List<DownloadDecision>>()))
                  .Returns<List<DownloadDecision>>(d => d);
        }

        private DownloadDecision GetDecision(ReleaseInfo release, SearchCriteriaBase criteria)
        {
            var remoteEpisode = new RemoteEpisode { Release = release, Series = criteria.Series, Episodes = criteria.Episodes.ToList() };

            if (_blocklistedGuids.Contains(release.Guid))
            {
                return new DownloadDecision(remoteEpisode, new DownloadRejection(DownloadRejectionReason.Blocklisted, "Blocklisted"));
            }

            if (_delayedGuids.Contains(release.Guid))
            {
                return new DownloadDecision(remoteEpisode, new DownloadRejection(DownloadRejectionReason.MinimumAgeDelay, "Delayed", RejectionType.Temporary));
            }

            return new DownloadDecision(remoteEpisode);
        }

        private void SearchAndFail(string guid)
        {
            Subject.Execute(new EpisodeSearchCommand(new List<int> { 1 }));

            _blocklistedGuids.Add(guid);
        }

        private void RedownloadFailed()
        {
            Subject.Execute(new EpisodeSearchCommand(new List<int> { 1 }) { UseCachedReleases = true });
        }

        private void VerifyGrabbed(string guid)
        {
            Mocker.GetMock<IDownloadService>()
                  .Verify(v => v.DownloadReport(It.Is<RemoteEpisode>(r => r.Release.Guid == guid), null), Times.Once());
        }

        private void VerifySearchCount(int count)
        {
            _indexer.Verify(v => v.Fetch(It.IsAny<SingleEpisodeSearchCriteria>()), Times.Exactly(count));
        }

        private ICached<List<ReleaseInfo>> GetCache()
        {
            return Mocker.Resolve<ICacheManager>().GetCache<List<ReleaseInfo>>(typeof(ReleaseSearchService), "approvedReleases");
        }

        [Test]
        public void should_grab_next_cached_release_without_searching()
        {
            SearchAndFail("guid1");

            RedownloadFailed();

            VerifyGrabbed("guid1");
            VerifyGrabbed("guid2");
            VerifySearchCount(1);
        }

        [Test]
        public void should_skip_cached_releases_that_are_rejected_now()
        {
            SearchAndFail("guid1");
            _blocklistedGuids.Add("guid2");

            RedownloadFailed();

            VerifyGrabbed("guid3");
            Mocker.GetMock<IDownloadService>()
                  .Verify(v => v.DownloadReport(It.Is<RemoteEpisode>(r => r.Release.Guid == "guid2"), null), Times.Never());
            VerifySearchCount(1);
        }

        [Test]
        public void should_search_when_no_cached_release_is_acceptable()
        {
            SearchAndFail("guid1");
            _blocklistedGuids.Add("guid2");
            _blocklistedGuids.Add("guid3");

            RedownloadFailed();

            VerifySearchCount(2);
        }

        [Test]
        public void should_search_when_cached_releases_expired()
        {
            SearchAndFail("guid1");

            var cache = GetCache();
            cache.Set("episode:1", cache.Find("episode:1"), TimeSpan.FromMilliseconds(-1));

            RedownloadFailed();

            VerifySearchCount(2);
        }

        [Test]
        public void should_search_when_cache_is_disabled()
        {
            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.AutoRedownloadFailedCacheLifetime)
                  .Returns(0);

            SearchAndFail("guid1");

            RedownloadFailed();

            VerifySearchCount(2);
        }

        [Test]
        public void should_grab_next_cached_release_when_link_expired()
        {
            SearchAndFail("guid1");

            Mocker.GetMock<IDownloadService>()
                  .Setup(s => s.DownloadReport(It.Is<RemoteEpisode>(r => r.Release.Guid == "guid2"), It.IsAny<int?>()))
                  .ThrowsAsync(new ReleaseUnavailableException(_releases[1], "Indexer link expired"));

            RedownloadFailed();

            VerifyGrabbed("guid3");
            VerifySearchCount(1);
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_not_search_when_cached_release_is_pending()
        {
            SearchAndFail("guid1");
            _delayedGuids.Add("guid2");
            _delayedGuids.Add("guid3");

            RedownloadFailed();

            Mocker.GetMock<IPendingReleaseService>()
                  .Verify(v => v.AddMany(It.Is<List<Tuple<DownloadDecision, PendingReleaseReason>>>(l => l.Any())), Times.Once());
            VerifySearchCount(1);
        }

        [Test]
        public void should_cache_temporarily_rejected_releases()
        {
            _delayedGuids.Add("guid2");
            SearchAndFail("guid1");
            _delayedGuids.Clear();

            RedownloadFailed();

            VerifyGrabbed("guid2");
            VerifySearchCount(1);
        }

        [Test]
        public void should_clear_expired_cache_entries_when_caching()
        {
            GetCache().Set("episode:99", _releases.ToList(), TimeSpan.FromMilliseconds(-1));

            SearchAndFail("guid1");

            GetCache().Count.Should().Be(1);
            GetCache().Find("episode:1").Should().NotBeNull();
        }

        [Test]
        public void should_clear_cache_when_lifetime_is_zero()
        {
            SearchAndFail("guid1");
            GetCache().Count.Should().Be(1);

            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.AutoRedownloadFailedCacheLifetime)
                  .Returns(0);

            Subject.Execute(new EpisodeSearchCommand(new List<int> { 1 }));

            GetCache().Count.Should().Be(0);
        }

        [Test]
        public void should_not_use_cached_releases_for_regular_search()
        {
            SearchAndFail("guid1");

            Subject.Execute(new EpisodeSearchCommand(new List<int> { 1 }));

            VerifySearchCount(2);
        }

        [Test]
        public void should_not_use_cached_season_search_for_episode()
        {
            Mocker.Resolve<ISearchForReleases>().SeasonSearch(_series.Id, 1, false, true, false, false).GetAwaiter().GetResult();

            RedownloadFailed();

            VerifySearchCount(1);
        }

        [Test]
        public void should_grab_next_cached_season_pack_without_searching()
        {
            _releases.ForEach(r => r.Title = r.Title.Replace("S01E01", "S01"));

            var seasonSearch = Mocker.Resolve<SeasonSearchService>();

            seasonSearch.Execute(new SeasonSearchCommand { SeriesId = _series.Id, SeasonNumber = 1 });
            _blocklistedGuids.Add("guid1");

            seasonSearch.Execute(new SeasonSearchCommand { SeriesId = _series.Id, SeasonNumber = 1, UseCachedReleases = true });

            VerifyGrabbed("guid1");
            VerifyGrabbed("guid2");
            _indexer.Verify(v => v.Fetch(It.IsAny<SeasonSearchCriteria>()), Times.Once());
        }
    }
}
