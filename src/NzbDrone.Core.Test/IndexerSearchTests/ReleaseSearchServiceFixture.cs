using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DataAugmentation.Scene;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    public class ReleaseSearchServiceFixture : CoreTest<ReleaseSearchService>
    {
        private Mock<IIndexer> _mockIndexer;
        private Series _xemSeries;
        private List<Episode> _xemEpisodes;
        private TaskCompletionSource<IList<ReleaseInfo>> _neverAnswers;

        [SetUp]
        public void SetUp()
        {
            _mockIndexer = Mocker.GetMock<IIndexer>();
            _mockIndexer.SetupGet(s => s.Definition).Returns(new IndexerDefinition { Id = 1 });
            _mockIndexer.SetupGet(s => s.SupportsSearch).Returns(true);

            Mocker.GetMock<IIndexerFactory>()
                  .Setup(s => s.AutomaticSearchEnabled(true))
                  .Returns(new List<IIndexer> { _mockIndexer.Object });

            Mocker.GetMock<IMakeDownloadDecision>()
                .Setup(s => s.GetSearchDecision(It.IsAny<List<Parser.Model.ReleaseInfo>>(), It.IsAny<SearchCriteriaBase>()))
                .Returns(new List<DownloadDecision>());

            _xemSeries = Builder<Series>.CreateNew()
                .With(v => v.UseSceneNumbering = true)
                .With(v => v.Monitored = true)
                .Build();

            _xemEpisodes = new List<Episode>();

            Mocker.GetMock<ISeriesService>()
                .Setup(v => v.GetSeries(_xemSeries.Id))
                .Returns(_xemSeries);

            Mocker.GetMock<IEpisodeService>()
                .Setup(v => v.GetEpisodesBySeason(_xemSeries.Id, It.IsAny<int>()))
                .Returns<int, int>((i, j) => _xemEpisodes.Where(d => d.SeasonNumber == j).ToList());

            Mocker.GetMock<ISceneMappingService>()
                  .Setup(s => s.FindByTvdbId(It.IsAny<int>()))
                  .Returns(new List<SceneMapping>());

            Mocker.GetMock<ISceneMappingService>()
                  .Setup(s => s.GetSceneNames(It.IsAny<int>(), It.IsAny<List<int>>(), It.IsAny<List<int>>()))
                  .Returns(new List<string>());

            _neverAnswers = new TaskCompletionSource<IList<ReleaseInfo>>();
        }

        [TearDown]
        public void TearDown()
        {
            _neverAnswers.TrySetResult(new List<ReleaseInfo>());
        }

        private void WithEpisode(int seasonNumber, int episodeNumber, int? sceneSeasonNumber, int? sceneEpisodeNumber, string airDate = null)
        {
            var episode = Builder<Episode>.CreateNew()
                .With(v => v.SeriesId == _xemSeries.Id)
                .With(v => v.Series == _xemSeries)
                .With(v => v.SeasonNumber, seasonNumber)
                .With(v => v.EpisodeNumber, episodeNumber)
                .With(v => v.SceneSeasonNumber, sceneSeasonNumber)
                .With(v => v.SceneEpisodeNumber, sceneEpisodeNumber)
                .With(v => v.AirDate = airDate ?? $"{2000 + seasonNumber}-{(episodeNumber % 12) + 1:00}-05")
                .With(v => v.AirDateUtc = DateTime.ParseExact(v.AirDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime())
                .With(v => v.Monitored = true)
                .Build();

            _xemEpisodes.Add(episode);
        }

        private void WithEpisodes()
        {
            // Season 1 maps to Scene Season 2 (one-to-one)
            WithEpisode(1, 12, 2, 3);
            WithEpisode(1, 13, 2, 4);

            // Season 2 maps to Scene Season 3 & 4 (one-to-one)
            WithEpisode(2, 1, 3, 11);
            WithEpisode(2, 2, 3, 12);
            WithEpisode(2, 3, 4, 11);
            WithEpisode(2, 4, 4, 12);

            // Season 3 maps to Scene Season 5 (partial)
            // Season 4 maps to Scene Season 5 & 6 (partial)
            WithEpisode(3, 1, 5, 11);
            WithEpisode(3, 2, 5, 12);
            WithEpisode(4, 1, 5, 13);
            WithEpisode(4, 2, 5, 14);
            WithEpisode(4, 3, 6, 11);
            WithEpisode(5, 1, 6, 12);

            // Season 7+ maps normally, so no mapping specified.
            WithEpisode(7, 1, null, null);
            WithEpisode(7, 2, null, null);
        }

        private List<SearchCriteriaBase> WatchForSearchCriteria()
        {
            var result = new List<SearchCriteriaBase>();

            _mockIndexer.Setup(v => v.Fetch(It.IsAny<SingleEpisodeSearchCriteria>()))
                .Callback<SingleEpisodeSearchCriteria>(s => result.Add(s))
                .Returns(Task.FromResult<IList<Parser.Model.ReleaseInfo>>(new List<Parser.Model.ReleaseInfo>()));

            _mockIndexer.Setup(v => v.Fetch(It.IsAny<SeasonSearchCriteria>()))
                .Callback<SeasonSearchCriteria>(s => result.Add(s))
                .Returns(Task.FromResult<IList<Parser.Model.ReleaseInfo>>(new List<Parser.Model.ReleaseInfo>()));

            _mockIndexer.Setup(v => v.Fetch(It.IsAny<DailyEpisodeSearchCriteria>()))
                .Callback<DailyEpisodeSearchCriteria>(s => result.Add(s))
                .Returns(Task.FromResult<IList<Parser.Model.ReleaseInfo>>(new List<Parser.Model.ReleaseInfo>()));

            _mockIndexer.Setup(v => v.Fetch(It.IsAny<DailySeasonSearchCriteria>()))
                .Callback<DailySeasonSearchCriteria>(s => result.Add(s))
                .Returns(Task.FromResult<IList<Parser.Model.ReleaseInfo>>(new List<Parser.Model.ReleaseInfo>()));

            _mockIndexer.Setup(v => v.Fetch(It.IsAny<AnimeEpisodeSearchCriteria>()))
                .Callback<AnimeEpisodeSearchCriteria>(s => result.Add(s))
                .Returns(Task.FromResult<IList<Parser.Model.ReleaseInfo>>(new List<Parser.Model.ReleaseInfo>()));

            _mockIndexer.Setup(v => v.Fetch(It.IsAny<AnimeSeasonSearchCriteria>()))
                .Callback<AnimeSeasonSearchCriteria>(s => result.Add(s))
                .Returns(Task.FromResult<IList<Parser.Model.ReleaseInfo>>(new List<Parser.Model.ReleaseInfo>()));

            _mockIndexer.Setup(v => v.Fetch(It.IsAny<SpecialEpisodeSearchCriteria>()))
                .Callback<SpecialEpisodeSearchCriteria>(s => result.Add(s))
                .Returns(Task.FromResult<IList<Parser.Model.ReleaseInfo>>(new List<Parser.Model.ReleaseInfo>()));

            return result;
        }

        [Test]
        public async Task Tags_IndexerTags_SeriesNoTags_IndexerNotIncluded()
        {
            _mockIndexer.SetupGet(s => s.Definition).Returns(new IndexerDefinition
            {
                Id = 1,
                Tags = new HashSet<int> { 3 }
            });

            WithEpisodes();

            var allCriteria = WatchForSearchCriteria();

            await Subject.EpisodeSearch(_xemEpisodes.First(), true, false);

            var criteria = allCriteria.OfType<SingleEpisodeSearchCriteria>().ToList();

            criteria.Count.Should().Be(0);
        }

        [Test]
        public async Task Tags_IndexerNoTags_SeriesTags_IndexerIncluded()
        {
            _mockIndexer.SetupGet(s => s.Definition).Returns(new IndexerDefinition
            {
                Id = 1
            });

            _xemSeries = Builder<Series>.CreateNew()
                .With(v => v.UseSceneNumbering = true)
                .With(v => v.Monitored = true)
                .With(v => v.Tags = new HashSet<int> { 3 })
                .Build();

            Mocker.GetMock<ISeriesService>()
                .Setup(v => v.GetSeries(_xemSeries.Id))
                .Returns(_xemSeries);

            WithEpisodes();

            var allCriteria = WatchForSearchCriteria();

            await Subject.EpisodeSearch(_xemEpisodes.First(), true, false);

            var criteria = allCriteria.OfType<SingleEpisodeSearchCriteria>().ToList();

            criteria.Count.Should().Be(1);
        }

        [Test]
        public async Task Tags_IndexerAndSeriesTagsMatch_IndexerIncluded()
        {
            _mockIndexer.SetupGet(s => s.Definition).Returns(new IndexerDefinition
            {
                Id = 1,
                Tags = new HashSet<int> { 1, 2, 3 }
            });

            _xemSeries = Builder<Series>.CreateNew()
                .With(v => v.UseSceneNumbering = true)
                .With(v => v.Monitored = true)
                .With(v => v.Tags = new HashSet<int> { 3, 4, 5 })
                .Build();

            Mocker.GetMock<ISeriesService>()
                .Setup(v => v.GetSeries(_xemSeries.Id))
                .Returns(_xemSeries);

            WithEpisodes();

            var allCriteria = WatchForSearchCriteria();

            await Subject.EpisodeSearch(_xemEpisodes.First(), true, false);

            var criteria = allCriteria.OfType<SingleEpisodeSearchCriteria>().ToList();

            criteria.Count.Should().Be(1);
        }

        [Test]
        public async Task Tags_IndexerAndSeriesTagsMismatch_IndexerNotIncluded()
        {
            _mockIndexer.SetupGet(s => s.Definition).Returns(new IndexerDefinition
            {
                Id = 1,
                Tags = new HashSet<int> { 1, 2, 3 }
            });

            _xemSeries = Builder<Series>.CreateNew()
                .With(v => v.UseSceneNumbering = true)
                .With(v => v.Monitored = true)
                .With(v => v.Tags = new HashSet<int> { 4, 5, 6 })
                .Build();

            Mocker.GetMock<ISeriesService>()
                .Setup(v => v.GetSeries(_xemSeries.Id))
                .Returns(_xemSeries);

            WithEpisodes();

            var allCriteria = WatchForSearchCriteria();

            await Subject.EpisodeSearch(_xemEpisodes.First(), true, false);

            var criteria = allCriteria.OfType<SingleEpisodeSearchCriteria>().ToList();

            criteria.Count.Should().Be(0);
        }

        [Test]
        public async Task scene_episodesearch()
        {
            WithEpisodes();

            var allCriteria = WatchForSearchCriteria();

            await Subject.EpisodeSearch(_xemEpisodes.First(), true, false);

            var criteria = allCriteria.OfType<SingleEpisodeSearchCriteria>().ToList();

            criteria.Count.Should().Be(1);
            criteria[0].SeasonNumber.Should().Be(2);
            criteria[0].EpisodeNumber.Should().Be(3);
        }

        [Test]
        public async Task scene_seasonsearch()
        {
            WithEpisodes();

            var allCriteria = WatchForSearchCriteria();

            await Subject.SeasonSearch(_xemSeries.Id, 1, false, false, true, false);

            var criteria = allCriteria.OfType<SeasonSearchCriteria>().ToList();

            criteria.Count.Should().Be(1);
            criteria[0].SeasonNumber.Should().Be(2);
        }

        [Test]
        public async Task scene_seasonsearch_should_search_multiple_seasons()
        {
            WithEpisodes();

            var allCriteria = WatchForSearchCriteria();

            await Subject.SeasonSearch(_xemSeries.Id, 2, false, false, true, false);

            var criteria = allCriteria.OfType<SeasonSearchCriteria>().ToList();

            criteria.Count.Should().Be(2);
            criteria[0].SeasonNumber.Should().Be(3);
            criteria[1].SeasonNumber.Should().Be(4);
        }

        [Test]
        public async Task scene_seasonsearch_should_search_single_episode_if_possible()
        {
            WithEpisodes();

            var allCriteria = WatchForSearchCriteria();

            await Subject.SeasonSearch(_xemSeries.Id, 4, false, false, true, false);

            var criteria1 = allCriteria.OfType<SeasonSearchCriteria>().ToList();
            var criteria2 = allCriteria.OfType<SingleEpisodeSearchCriteria>().ToList();

            criteria1.Count.Should().Be(1);
            criteria1[0].SeasonNumber.Should().Be(5);

            criteria2.Count.Should().Be(1);
            criteria2[0].SeasonNumber.Should().Be(6);
            criteria2[0].EpisodeNumber.Should().Be(11);
        }

        [Test]
        public async Task scene_seasonsearch_should_use_seasonnumber_if_no_scene_number_is_available()
        {
            WithEpisodes();

            var allCriteria = WatchForSearchCriteria();

            await Subject.SeasonSearch(_xemSeries.Id, 7, false, false, true, false);

            var criteria = allCriteria.OfType<SeasonSearchCriteria>().ToList();

            criteria.Count.Should().Be(1);
            criteria[0].SeasonNumber.Should().Be(7);
        }

        [Test]
        public async Task season_search_for_anime_should_search_for_each_monitored_episode()
        {
            WithEpisodes();
            _xemSeries.SeriesType = SeriesTypes.Anime;
            _xemEpisodes.ForEach(e => e.EpisodeFileId = 0);

            var seasonNumber = 1;
            var allCriteria = WatchForSearchCriteria();

            await Subject.SeasonSearch(_xemSeries.Id, seasonNumber, true, false, true, false);

            var criteria = allCriteria.OfType<AnimeEpisodeSearchCriteria>().ToList();

            criteria.Count.Should().Be(_xemEpisodes.Count(e => e.SeasonNumber == seasonNumber));
        }

        [Test]
        public async Task season_search_for_anime_should_not_search_for_unmonitored_episodes()
        {
            WithEpisodes();
            _xemSeries.SeriesType = SeriesTypes.Anime;
            _xemEpisodes.ForEach(e => e.Monitored = false);
            _xemEpisodes.ForEach(e => e.EpisodeFileId = 0);

            var seasonNumber = 1;
            var allCriteria = WatchForSearchCriteria();

            await Subject.SeasonSearch(_xemSeries.Id, seasonNumber, false, true, true, false);

            var criteria = allCriteria.OfType<AnimeEpisodeSearchCriteria>().ToList();

            criteria.Count.Should().Be(0);
        }

        [Test]
        public async Task season_search_for_anime_should_not_search_for_unaired_episodes()
        {
            WithEpisodes();
            _xemSeries.SeriesType = SeriesTypes.Anime;
            _xemEpisodes.ForEach(e => e.AirDateUtc = DateTime.UtcNow.AddDays(5));
            _xemEpisodes.ForEach(e => e.EpisodeFileId = 0);

            var seasonNumber = 1;
            var allCriteria = WatchForSearchCriteria();

            await Subject.SeasonSearch(_xemSeries.Id, seasonNumber, false, false, true, false);

            var criteria = allCriteria.OfType<AnimeEpisodeSearchCriteria>().ToList();

            criteria.Count.Should().Be(0);
        }

        [Test]
        public async Task season_search_for_anime_should_not_search_for_episodes_with_files()
        {
            WithEpisodes();
            _xemSeries.SeriesType = SeriesTypes.Anime;
            _xemEpisodes.ForEach(e => e.EpisodeFileId = 1);

            var seasonNumber = 1;
            var allCriteria = WatchForSearchCriteria();

            await Subject.SeasonSearch(_xemSeries.Id, seasonNumber, true, false, true, false);

            var criteria = allCriteria.OfType<AnimeEpisodeSearchCriteria>().ToList();

            criteria.Count.Should().Be(0);
        }

        [Test]
        public async Task season_search_for_anime_should_set_isSeasonSearch_flag()
        {
            WithEpisodes();
            _xemSeries.SeriesType = SeriesTypes.Anime;
            _xemEpisodes.ForEach(e => e.EpisodeFileId = 0);

            var seasonNumber = 1;
            var allCriteria = WatchForSearchCriteria();

            await Subject.SeasonSearch(_xemSeries.Id, seasonNumber, true, false, true, false);

            var criteria = allCriteria.OfType<AnimeEpisodeSearchCriteria>().ToList();

            criteria.Count.Should().Be(_xemEpisodes.Count(e => e.SeasonNumber == seasonNumber));
            criteria.ForEach(c => c.IsSeasonSearch.Should().BeTrue());
        }

        [Test]
        public async Task season_search_for_anime_should_search_for_each_monitored_season()
        {
            WithEpisodes();
            _xemSeries.SeriesType = SeriesTypes.Anime;
            _xemEpisodes.ForEach(e => e.EpisodeFileId = 0);

            var seasonNumber = 1;
            var allCriteria = WatchForSearchCriteria();

            await Subject.SeasonSearch(_xemSeries.Id, seasonNumber, true, false, true, false);

            var criteria = allCriteria.OfType<AnimeSeasonSearchCriteria>().ToList();

            var episodesForSeason1 = _xemEpisodes.Where(e => e.SeasonNumber == seasonNumber);
            criteria.Count.Should().Be(episodesForSeason1.Select(e => e.SeasonNumber).Distinct().Count());
        }

        [Test]
        public async Task season_search_for_anime_should_not_search_for_unmonitored_season()
        {
            WithEpisodes();
            _xemSeries.SeriesType = SeriesTypes.Anime;
            _xemEpisodes.ForEach(e => e.Monitored = false);
            _xemEpisodes.ForEach(e => e.EpisodeFileId = 0);

            var seasonNumber = 1;
            var allCriteria = WatchForSearchCriteria();

            await Subject.SeasonSearch(_xemSeries.Id, seasonNumber, false, true, true, false);

            var criteria = allCriteria.OfType<AnimeSeasonSearchCriteria>().ToList();

            criteria.Count.Should().Be(0);
        }

        [Test]
        public async Task season_search_for_anime_should_not_search_for_unaired_season()
        {
            WithEpisodes();
            _xemSeries.SeriesType = SeriesTypes.Anime;
            _xemEpisodes.ForEach(e => e.AirDateUtc = DateTime.UtcNow.AddDays(5));
            _xemEpisodes.ForEach(e => e.EpisodeFileId = 0);

            var seasonNumber = 1;
            var allCriteria = WatchForSearchCriteria();

            await Subject.SeasonSearch(_xemSeries.Id, seasonNumber, false, false, true, false);

            var criteria = allCriteria.OfType<AnimeSeasonSearchCriteria>().ToList();

            criteria.Count.Should().Be(0);
        }

        [Test]
        public async Task season_search_for_anime_should_not_search_for_season_with_files()
        {
            WithEpisodes();
            _xemSeries.SeriesType = SeriesTypes.Anime;
            _xemEpisodes.ForEach(e => e.EpisodeFileId = 1);

            var seasonNumber = 1;
            var allCriteria = WatchForSearchCriteria();

            await Subject.SeasonSearch(_xemSeries.Id, seasonNumber, true, false, true, false);

            var criteria = allCriteria.OfType<AnimeSeasonSearchCriteria>().ToList();

            criteria.Count.Should().Be(0);
        }

        [Test]
        public async Task season_search_for_daily_should_search_multiple_years()
        {
            WithEpisode(1, 1, null, null, "2005-12-30");
            WithEpisode(1, 2, null, null, "2005-12-31");
            WithEpisode(1, 3, null, null, "2006-01-01");
            WithEpisode(1, 4, null, null, "2006-01-02");
            _xemSeries.SeriesType = SeriesTypes.Daily;

            var allCriteria = WatchForSearchCriteria();

            await Subject.SeasonSearch(_xemSeries.Id, 1, false, false, true, false);

            var criteria = allCriteria.OfType<DailySeasonSearchCriteria>().ToList();

            criteria.Count.Should().Be(2);
            criteria[0].Year.Should().Be(2005);
            criteria[1].Year.Should().Be(2006);
        }

        [Test]
        public async Task season_search_for_daily_should_search_single_episode_if_possible()
        {
            WithEpisode(1, 1, null, null, "2005-12-30");
            WithEpisode(1, 2, null, null, "2005-12-31");
            WithEpisode(1, 3, null, null, "2006-01-01");
            _xemSeries.SeriesType = SeriesTypes.Daily;

            var allCriteria = WatchForSearchCriteria();

            await Subject.SeasonSearch(_xemSeries.Id, 1, false, false, true, false);

            var criteria1 = allCriteria.OfType<DailySeasonSearchCriteria>().ToList();
            var criteria2 = allCriteria.OfType<DailyEpisodeSearchCriteria>().ToList();

            criteria1.Count.Should().Be(1);
            criteria1[0].Year.Should().Be(2005);

            criteria2.Count.Should().Be(1);
            criteria2[0].AirDate.Should().Be(new DateTime(2006, 1, 1));
        }

        [Test]
        public async Task season_search_for_daily_should_not_search_for_unmonitored_episodes()
        {
            WithEpisode(1, 1, null, null, "2005-12-30");
            WithEpisode(1, 2, null, null, "2005-12-31");
            WithEpisode(1, 3, null, null, "2006-01-01");
            _xemSeries.SeriesType = SeriesTypes.Daily;
            _xemEpisodes[0].Monitored = false;

            var allCriteria = WatchForSearchCriteria();

            await Subject.SeasonSearch(_xemSeries.Id, 1, false, true, true, false);

            var criteria1 = allCriteria.OfType<DailySeasonSearchCriteria>().ToList();
            var criteria2 = allCriteria.OfType<DailyEpisodeSearchCriteria>().ToList();

            criteria1.Should().HaveCount(0);
            criteria2.Should().HaveCount(2);
        }

        [Test]
        public async Task getscenenames_should_use_seasonnumber_if_no_scene_seasonnumber_is_available()
        {
            WithEpisodes();

            var allCriteria = WatchForSearchCriteria();

            await Subject.SeasonSearch(_xemSeries.Id, 7, false, false, true, false);

            Mocker.GetMock<ISceneMappingService>()
                  .Verify(v => v.FindByTvdbId(_xemSeries.Id), Times.Once());

            allCriteria.Should().HaveCount(1);
            allCriteria.First().Should().BeOfType<SeasonSearchCriteria>();
            allCriteria.First().As<SeasonSearchCriteria>().SeasonNumber.Should().Be(7);
        }

        [Test]
        public async Task episode_search_should_use_all_available_numbering_from_services_and_xem()
        {
            WithEpisode(1, 12, 2, 3);

            Mocker.GetMock<ISceneMappingService>()
                .Setup(s => s.FindByTvdbId(It.IsAny<int>()))
                .Returns(new List<SceneMapping>
                {
                    new SceneMapping
                    {
                        TvdbId = _xemSeries.TvdbId,
                        SearchTerm = _xemSeries.Title,
                        ParseTerm = _xemSeries.Title,
                        FilterRegex = "(?i)-(BTN)$",
                        SeasonNumber = 1,
                        SceneSeasonNumber = 1,
                        SceneOrigin = "tvdb",
                        Type = "ServicesProvider"
                    }
                });

            var allCriteria = WatchForSearchCriteria();

            await Subject.EpisodeSearch(_xemEpisodes.First(), false, false);

            Mocker.GetMock<ISceneMappingService>()
                .Verify(v => v.FindByTvdbId(_xemSeries.Id), Times.Once());

            allCriteria.Should().HaveCount(2);

            allCriteria.First().Should().BeOfType<SingleEpisodeSearchCriteria>();
            allCriteria.First().As<SingleEpisodeSearchCriteria>().SeasonNumber.Should().Be(1);
            allCriteria.First().As<SingleEpisodeSearchCriteria>().EpisodeNumber.Should().Be(12);

            allCriteria.Last().Should().BeOfType<SingleEpisodeSearchCriteria>();
            allCriteria.Last().As<SingleEpisodeSearchCriteria>().SeasonNumber.Should().Be(2);
            allCriteria.Last().As<SingleEpisodeSearchCriteria>().EpisodeNumber.Should().Be(3);
        }

        [Test]
        public async Task episode_search_should_include_series_title_when_not_a_direct_title_match()
        {
            _xemSeries.Title = "Sonarr's Title";
            _xemSeries.CleanTitle = "sonarrstitle";

            WithEpisode(1, 12, 2, 3);

            Mocker.GetMock<ISceneMappingService>()
                .Setup(s => s.FindByTvdbId(It.IsAny<int>()))
                .Returns(new List<SceneMapping>
                {
                    new SceneMapping
                    {
                        TvdbId = _xemSeries.TvdbId,
                        SearchTerm = "Sonarrs Title",
                        ParseTerm = _xemSeries.CleanTitle,
                        SeasonNumber = 1,
                        SceneSeasonNumber = 1,
                        SceneOrigin = "tvdb",
                        Type = "ServicesProvider"
                    }
                });

            var allCriteria = WatchForSearchCriteria();

            await Subject.EpisodeSearch(_xemEpisodes.First(), false, false);

            Mocker.GetMock<ISceneMappingService>()
                .Verify(v => v.FindByTvdbId(_xemSeries.Id), Times.Once());

            allCriteria.Should().HaveCount(2);

            allCriteria.First().Should().BeOfType<SingleEpisodeSearchCriteria>();
            allCriteria.First().As<SingleEpisodeSearchCriteria>().SeasonNumber.Should().Be(1);
            allCriteria.First().As<SingleEpisodeSearchCriteria>().EpisodeNumber.Should().Be(12);

            allCriteria.Last().Should().BeOfType<SingleEpisodeSearchCriteria>();
            allCriteria.Last().As<SingleEpisodeSearchCriteria>().SeasonNumber.Should().Be(2);
            allCriteria.Last().As<SingleEpisodeSearchCriteria>().EpisodeNumber.Should().Be(3);
        }

        // Indexers with titles starting with "Required" have priority 1, "Failing" in the title makes the indexer fail after its delay
        private List<string> GivenIndexers(params (int DelayMs, string Title, int Score)[] indexers)
        {
            return GivenIndexersWithPriority(indexers.Select(i => (i.Title.StartsWith("Required") ? 1 : IndexerDefinition.DefaultPriority, i.DelayMs, i.Title, i.Score)).ToArray());
        }

        // Returns the titles of the indexers that were queried
        private List<string> GivenIndexersWithPriority(params (int Priority, int DelayMs, string Title, int Score)[] indexers)
        {
            var fetched = new List<string>();

            var result = indexers.Select((indexer, i) =>
            {
                var mock = new Mock<IIndexer>();

                mock.SetupGet(s => s.Definition).Returns(new IndexerDefinition { Id = i + 1, Name = indexer.Title, Priority = indexer.Priority });
                GivenQueryKeys(mock);

                Func<Task<IList<ReleaseInfo>>> fetch = () =>
                {
                    lock (fetched)
                    {
                        fetched.Add(indexer.Title);
                    }

                    return indexer.DelayMs == Timeout.Infinite
                        ? _neverAnswers.Task
                        : FetchDelayed(indexer.DelayMs, new ReleaseInfo { IndexerId = i + 1, Title = indexer.Title, Guid = indexer.Title, Size = indexer.Score });
                };

                mock.Setup(s => s.Fetch(It.IsAny<SingleEpisodeSearchCriteria>())).Returns(fetch);
                mock.Setup(s => s.Fetch(It.IsAny<SeasonSearchCriteria>())).Returns(fetch);

                return mock.Object;
            }).ToList();

            Mocker.GetMock<IIndexerFactory>()
                  .Setup(s => s.AutomaticSearchEnabled(true))
                  .Returns(result);

            Mocker.GetMock<IIndexerFactory>()
                  .Setup(s => s.InteractiveSearchEnabled(true))
                  .Returns(result);

            Mocker.GetMock<IMakeDownloadDecision>()
                .Setup(s => s.GetSearchDecision(It.IsAny<List<ReleaseInfo>>(), It.IsAny<SearchCriteriaBase>()))
                .Returns<List<ReleaseInfo>, SearchCriteriaBase>(Decide);

            Mocker.GetMock<IMakeDownloadDecision>()
                .Setup(s => s.GetSearchDecision(It.IsAny<List<ReleaseInfo>>(), It.IsAny<SearchCriteriaBase>(), It.IsAny<bool>()))
                .Returns<List<ReleaseInfo>, SearchCriteriaBase, bool>((reports, criteria, reportProgress) => Decide(reports, criteria));

            return fetched;
        }

        private static void GivenQueryKeys(Mock<IIndexer> indexer)
        {
            indexer.Setup(s => s.GetSearchQueryKey(It.IsAny<SearchCriteriaBase>()))
                   .Returns<SearchCriteriaBase>(c => $"{c.GetType().Name} {c}");
        }

        private void GivenQueryCache()
        {
            Mocker.SetConstant<ICacheManager>(Mocker.Resolve<CacheManager>());
            Mocker.GetMock<IConfigService>().SetupGet(s => s.SearchResultCacheLifetime).Returns(60);
        }

        private ICached<ReleaseSearchService.CachedQuery> GetQueryCache()
        {
            return Mocker.Resolve<ICacheManager>().GetCache<ReleaseSearchService.CachedQuery>(typeof(ReleaseSearchService), "searchQueries");
        }

        private ReleaseSearchService.CachedQuery GetCachedQuery(int indexerId)
        {
            return GetQueryCache().Values.Single(q => q.Releases.Any(r => r.IndexerId == indexerId));
        }

        // Releases with a Size of at least 10 meet the cutoff, titles starting with "Rejected" are rejected and titles starting with "Episode" cover only the first searched episode
        private static List<DownloadDecision> Decide(List<ReleaseInfo> reports, SearchCriteriaBase criteria)
        {
            return reports.Select(r =>
            {
                var remoteEpisode = new RemoteEpisode
                {
                    Release = r,
                    Series = criteria.Series,
                    ParsedEpisodeInfo = new ParsedEpisodeInfo { Quality = new QualityModel(r.Size >= 10 ? Quality.HDTV720p : Quality.SDTV) },
                    CustomFormats = new List<CustomFormat>(),
                    Episodes = r.Title.StartsWith("Episode") ? criteria.Episodes.Take(1).ToList() : criteria.Episodes.ToList()
                };

                return r.Title.StartsWith("Rejected")
                    ? new DownloadDecision(remoteEpisode, new DownloadRejection(DownloadRejectionReason.Unknown, "Rejected"))
                    : new DownloadDecision(remoteEpisode);
            }).ToList();
        }

        private static async Task<IList<ReleaseInfo>> FetchDelayed(int delayMs, ReleaseInfo release)
        {
            await Task.Delay(delayMs);

            if (release.Title.Contains("Failing"))
            {
                throw new InvalidOperationException("Indexer failed");
            }

            return new List<ReleaseInfo> { release };
        }

        private void GivenEarlySearchReturn(int minimumWait, int requiredPriority = 0)
        {
            Mocker.GetMock<IConfigService>().SetupGet(s => s.EarlySearchReturnRequiredPriority).Returns(requiredPriority);
            Mocker.GetMock<IConfigService>().SetupGet(s => s.EarlySearchReturn).Returns(true);
            Mocker.GetMock<IConfigService>().SetupGet(s => s.EarlySearchReturnMinimumWait).Returns(minimumWait);

            _xemSeries.QualityProfile = new QualityProfile();

            Mocker.GetMock<IUpgradableSpecification>()
                  .Setup(s => s.CutoffNotMet(It.IsAny<QualityProfile>(), It.IsAny<QualityModel>(), It.IsAny<List<CustomFormat>>(), null))
                  .Returns<QualityProfile, QualityModel, List<CustomFormat>, QualityModel>((profile, quality, formats, newQuality) => quality.Quality != Quality.HDTV720p);
        }

        private void GivenSeasonEpisodes()
        {
            WithEpisode(7, 1, null, null);
            WithEpisode(7, 2, null, null);

            _xemEpisodes[0].Id = 1;
            _xemEpisodes[1].Id = 2;
        }

        private async Task<List<string>> SearchTitles(bool interactiveSearch = false)
        {
            GivenSeasonEpisodes();

            var decisions = await Subject.EpisodeSearch(_xemEpisodes.First(), true, interactiveSearch);

            return decisions.Select(d => d.RemoteEpisode.Release.Title).ToList();
        }

        private async Task<List<string>> SeasonSearchTitles()
        {
            GivenSeasonEpisodes();

            var decisions = await Subject.SeasonSearch(_xemSeries.Id, 7, false, true, true, false);

            return decisions.Select(d => d.RemoteEpisode.Release.Title).ToList();
        }

        [Test]
        public async Task should_return_early_when_good_release_found()
        {
            GivenEarlySearchReturn(0);
            GivenIndexers((0, "Fast", 10), (Timeout.Infinite, "Slow", 100));

            var stopwatch = Stopwatch.StartNew();
            var titles = await SearchTitles();

            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
            titles.Should().BeEquivalentTo("Fast");
        }

        [Test]
        public async Task should_wait_for_minimum_wait_before_returning_early()
        {
            GivenEarlySearchReturn(2);
            GivenIndexers((0, "Fast", 10), (200, "Medium", 0), (Timeout.Infinite, "Slow", 100));

            var stopwatch = Stopwatch.StartNew();
            var titles = await SearchTitles();

            stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(1.5));
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
            titles.Should().BeEquivalentTo("Fast", "Medium");
        }

        [Test]
        public async Task should_wait_for_slow_indexer_when_no_good_release_found()
        {
            GivenEarlySearchReturn(0);
            GivenIndexers((0, "Fast", 5), (0, "Rejected", 100), (500, "Slow", 20));

            var titles = await SearchTitles();

            titles.Should().BeEquivalentTo("Fast", "Rejected", "Slow");
        }

        [Test]
        public async Task should_wait_for_slow_indexer_when_cutoff_not_met()
        {
            GivenEarlySearchReturn(0);
            GivenIndexers((0, "Fast", 5), (500, "Slow", 20));

            var titles = await SearchTitles();

            titles.Should().BeEquivalentTo("Fast", "Slow");
        }

        [Test]
        public async Task should_keep_results_of_answered_indexers_once_minimum_wait_passed()
        {
            GivenEarlySearchReturn(0);
            GivenIndexers((0, "Fast", 10), (0, "Other", 5), (Timeout.Infinite, "Slow", 20));

            var titles = await SearchTitles();

            titles.Should().BeEquivalentTo("Fast", "Other");
        }

        [Test]
        public async Task should_wait_for_all_indexers_when_early_search_return_disabled()
        {
            GivenIndexers((0, "Fast", 100), (500, "Slow", 20));

            var titles = await SearchTitles();

            titles.Should().BeEquivalentTo("Fast", "Slow");
        }

        [Test]
        public async Task should_wait_for_all_indexers_for_interactive_search()
        {
            GivenEarlySearchReturn(0);
            GivenIndexers((0, "Fast", 100), (500, "Slow", 20));

            var titles = await SearchTitles(true);

            titles.Should().BeEquivalentTo("Fast", "Slow");
        }

        [Test]
        public async Task should_return_season_search_early_when_good_season_pack_found()
        {
            GivenEarlySearchReturn(0);
            GivenIndexers((0, "Pack", 10), (Timeout.Infinite, "Slow", 100));

            var stopwatch = Stopwatch.StartNew();
            var titles = await SeasonSearchTitles();

            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
            titles.Should().BeEquivalentTo("Pack");
        }

        [Test]
        public async Task should_not_return_season_search_early_for_single_episode_release()
        {
            GivenEarlySearchReturn(0);
            GivenIndexers((0, "Episode", 100), (500, "Slow", 20));

            var titles = await SeasonSearchTitles();

            titles.Should().BeEquivalentTo("Episode", "Slow");
        }

        [Test]
        public async Task should_cache_releases_of_early_returned_search()
        {
            GivenQueryCache();

            GivenEarlySearchReturn(0);
            GivenIndexers((0, "Fast", 10), (Timeout.Infinite, "Slow", 100));

            await SearchTitles();

            Mocker.GetMock<IEpisodeService>()
                  .Setup(s => s.GetEpisode(_xemEpisodes.First().Id))
                  .Returns(_xemEpisodes.First());

            Mocker.GetMock<ISeriesService>()
                  .Setup(s => s.GetSeries(_xemSeries.Id))
                  .Returns(_xemSeries);

            Subject.CachedEpisodeSearch(_xemEpisodes.First().Id).Decisions.Select(d => d.RemoteEpisode.Release.Title).Should().BeEquivalentTo("Fast");
        }

        [Test]
        public async Task should_cache_result_of_query_abandoned_by_early_return_when_it_completes()
        {
            GivenQueryCache();
            GivenEarlySearchReturn(0);
            var fetched = GivenIndexers((0, "Fast", 10), (300, "Slow", 100));

            var titles = await SearchTitles();

            titles.Should().BeEquivalentTo("Fast");
            GetQueryCache().Count.Should().Be(1);

            var stopwatch = Stopwatch.StartNew();

            while (GetQueryCache().Count < 2 && stopwatch.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(20);
            }

            titles.Should().BeEquivalentTo("Fast");

            Mocker.GetMock<IEpisodeService>()
                  .Setup(s => s.GetEpisode(_xemEpisodes.First().Id))
                  .Returns(_xemEpisodes.First());

            Subject.CachedEpisodeSearch(_xemEpisodes.First().Id).Decisions.Select(d => d.RemoteEpisode.Release.Title).Should().BeEquivalentTo("Fast", "Slow");
            fetched.Should().HaveCount(2);
        }

        // The season query answers with a release named after the given title and score, every episode query with its own release after a short delay
        private List<AnimeEpisodeSearchCriteria> GivenAnimeSeason(int episodeCount, string packTitle, int packScore, Func<int> onEpisodeSearch = null, Action afterEpisodeSearch = null)
        {
            _xemSeries.SeriesType = SeriesTypes.Anime;

            for (var i = 1; i <= episodeCount; i++)
            {
                WithEpisode(1, i, null, null);
                _xemEpisodes.Last().Id = i;
            }

            var episodeSearches = new List<AnimeEpisodeSearchCriteria>();

            var mock = new Mock<IIndexer>();
            mock.SetupGet(s => s.Definition).Returns(new IndexerDefinition { Id = 1 });
            GivenQueryKeys(mock);

            mock.Setup(s => s.Fetch(It.IsAny<AnimeSeasonSearchCriteria>()))
                .Returns(Task.FromResult<IList<ReleaseInfo>>(new List<ReleaseInfo> { new ReleaseInfo { IndexerId = 1, Title = packTitle, Guid = packTitle, Size = packScore } }));

            mock.Setup(s => s.Fetch(It.IsAny<AnimeEpisodeSearchCriteria>()))
                .Returns<AnimeEpisodeSearchCriteria>(async criteria =>
                {
                    lock (episodeSearches)
                    {
                        episodeSearches.Add(criteria);
                    }

                    onEpisodeSearch?.Invoke();
                    await Task.Delay(50);
                    afterEpisodeSearch?.Invoke();

                    var title = "Episode " + criteria.EpisodeNumber;

                    return new List<ReleaseInfo> { new ReleaseInfo { IndexerId = 1, Title = title, Guid = title, Size = 5 } };
                });

            Mocker.GetMock<IIndexerFactory>()
                  .Setup(s => s.AutomaticSearchEnabled(true))
                  .Returns(new List<IIndexer> { mock.Object });

            Mocker.GetMock<IIndexerFactory>()
                  .Setup(s => s.InteractiveSearchEnabled(true))
                  .Returns(new List<IIndexer> { mock.Object });

            Mocker.GetMock<IMakeDownloadDecision>()
                .Setup(s => s.GetSearchDecision(It.IsAny<List<ReleaseInfo>>(), It.IsAny<SearchCriteriaBase>(), It.IsAny<bool>()))
                .Returns<List<ReleaseInfo>, SearchCriteriaBase, bool>((reports, criteria, reportProgress) => Decide(reports, criteria));

            Mocker.GetMock<IMakeDownloadDecision>()
                .Setup(s => s.GetSearchDecision(It.IsAny<List<ReleaseInfo>>(), It.IsAny<SearchCriteriaBase>()))
                .Returns<List<ReleaseInfo>, SearchCriteriaBase>(Decide);

            return episodeSearches;
        }

        private async Task<List<string>> AnimeSeasonSearchTitles(bool interactiveSearch = false)
        {
            var decisions = await Subject.SeasonSearch(_xemSeries.Id, 1, false, true, true, interactiveSearch);

            return decisions.Select(d => d.RemoteEpisode.Release.Title).ToList();
        }

        [Test]
        public async Task should_skip_episode_searches_when_season_pack_is_good_enough()
        {
            GivenEarlySearchReturn(0);
            var episodeSearches = GivenAnimeSeason(3, "Pack", 10);

            var titles = await AnimeSeasonSearchTitles();

            episodeSearches.Should().BeEmpty();
            titles.Should().BeEquivalentTo("Pack");
        }

        [Test]
        public async Task should_search_episodes_when_season_pack_does_not_meet_cutoff()
        {
            GivenEarlySearchReturn(0);
            var episodeSearches = GivenAnimeSeason(3, "Pack", 5);

            var titles = await AnimeSeasonSearchTitles();

            episodeSearches.Select(c => c.EpisodeNumber).Should().BeEquivalentTo(new[] { 1, 2, 3 });
            titles.Should().BeEquivalentTo("Pack", "Episode 1", "Episode 2", "Episode 3");
        }

        [Test]
        public async Task should_search_episodes_when_season_pack_only_covers_some_episodes()
        {
            GivenEarlySearchReturn(0);
            var episodeSearches = GivenAnimeSeason(3, "Episode Pack", 10);

            await AnimeSeasonSearchTitles();

            episodeSearches.Should().HaveCount(3);
        }

        [Test]
        public async Task should_search_episodes_in_interactive_search_when_season_pack_is_good_enough()
        {
            GivenEarlySearchReturn(0);
            var episodeSearches = GivenAnimeSeason(3, "Pack", 10);

            await AnimeSeasonSearchTitles(true);

            episodeSearches.Should().HaveCount(3);
        }

        [Test]
        public async Task should_serve_anime_season_search_from_its_cached_queries()
        {
            GivenQueryCache();
            GivenEarlySearchReturn(0);
            var episodeSearches = GivenAnimeSeason(3, "Pack", 5);

            await AnimeSeasonSearchTitles();

            Subject.CachedSeasonSearch(_xemSeries.Id, 1).Decisions.Select(d => d.RemoteEpisode.Release.Title).Should().BeEquivalentTo("Pack", "Episode 1", "Episode 2", "Episode 3");
            episodeSearches.Should().HaveCount(3);
        }

        [Test]
        public async Task should_search_episodes_when_early_search_return_disabled()
        {
            var episodeSearches = GivenAnimeSeason(3, "Pack", 10);

            await AnimeSeasonSearchTitles();

            episodeSearches.Should().HaveCount(3);
        }

        [TestCase(1)]
        [TestCase(3)]
        public async Task should_run_at_most_concurrency_episode_searches_at_once(int concurrency)
        {
            var running = 0;
            var maxRunning = 0;

            Mocker.GetMock<IConfigService>().SetupGet(s => s.SearchConcurrency).Returns(concurrency);

            var episodeSearches = GivenAnimeSeason(
                7,
                "Pack",
                5,
                () => InterlockedMax(ref maxRunning, Interlocked.Increment(ref running)),
                () => Interlocked.Decrement(ref running));

            var titles = await AnimeSeasonSearchTitles();

            maxRunning.Should().Be(concurrency);
            episodeSearches.Should().HaveCount(7);
            titles.Should().Equal("Pack", "Episode 1", "Episode 2", "Episode 3", "Episode 4", "Episode 5", "Episode 6", "Episode 7");
        }

        [Test]
        public async Task should_run_daily_season_year_searches_up_to_concurrency()
        {
            var running = 0;
            var maxRunning = 0;
            var years = new List<int>();

            foreach (var year in new[] { 2005, 2006, 2007, 2008 })
            {
                WithEpisode(1, year - 2000, null, null, $"{year}-01-01");
                WithEpisode(1, year - 1000, null, null, $"{year}-01-02");
            }

            _xemSeries.SeriesType = SeriesTypes.Daily;

            Mocker.GetMock<IConfigService>().SetupGet(s => s.SearchConcurrency).Returns(2);

            _mockIndexer.Setup(v => v.Fetch(It.IsAny<DailySeasonSearchCriteria>()))
                .Returns<DailySeasonSearchCriteria>(async criteria =>
                {
                    InterlockedMax(ref maxRunning, Interlocked.Increment(ref running));
                    await Task.Delay(50);
                    Interlocked.Decrement(ref running);

                    lock (years)
                    {
                        years.Add(criteria.Year);
                    }

                    return new List<ReleaseInfo>();
                });

            await Subject.SeasonSearch(_xemSeries.Id, 1, false, false, true, false);

            maxRunning.Should().Be(2);
            years.Should().BeEquivalentTo(new[] { 2005, 2006, 2007, 2008 });
        }

        [Test]
        public async Task should_share_concurrency_between_all_searches_of_one_command()
        {
            var running = 0;
            var maxRunning = 0;

            Mocker.GetMock<IConfigService>().SetupGet(s => s.SearchConcurrency).Returns(3);

            var episodeSearches = GivenAnimeSeason(
                4,
                "Pack",
                5,
                () => InterlockedMax(ref maxRunning, Interlocked.Increment(ref running)),
                () => Interlocked.Decrement(ref running));

            Mocker.GetMock<IProcessDownloadDecisions>()
                  .Setup(s => s.ProcessDecisions(It.IsAny<List<DownloadDecision>>()))
                  .Returns(Task.FromResult(new ProcessedDecisions(new List<DownloadDecision>(), new List<DownloadDecision>(), new List<DownloadDecision>())));

            await EpisodeSearchService.SearchAndProcess(new[] { 1, 2, 3 }, 3, Mocker.GetMock<IProcessDownloadDecisions>().Object, _ => Subject.SeasonSearch(_xemSeries.Id, 1, false, true, true, false, false));

            maxRunning.Should().Be(3);
            episodeSearches.Should().HaveCount(12);
        }

        private void GivenSearchIndexersInPriorityOrder()
        {
            Mocker.GetMock<IConfigService>().SetupGet(s => s.SearchIndexersInPriorityOrder).Returns(true);
        }

        [Test]
        public async Task should_search_all_indexers_at_once_when_priority_order_disabled()
        {
            GivenEarlySearchReturn(0);
            var fetched = GivenIndexersWithPriority((1, 0, "First", 10), (2, 0, "Second", 10));

            await SearchTitles();

            fetched.Should().BeEquivalentTo("First", "Second");
        }

        [Test]
        public async Task should_ignore_priority_order_when_early_search_return_disabled()
        {
            GivenSearchIndexersInPriorityOrder();
            var fetched = GivenIndexersWithPriority((1, 0, "First", 10), (2, 0, "Second", 10));

            var titles = await SearchTitles();

            fetched.Should().BeEquivalentTo("First", "Second");
            titles.Should().BeEquivalentTo("First", "Second");
        }

        [Test]
        public async Task should_stop_after_first_priority_group_with_good_enough_release()
        {
            GivenEarlySearchReturn(0);
            GivenSearchIndexersInPriorityOrder();
            var fetched = GivenIndexersWithPriority((1, 0, "First", 10), (1, 100, "Other", 5), (2, 0, "Second", 10));

            var titles = await SearchTitles();

            fetched.Should().BeEquivalentTo("First", "Other");
            titles.Should().Contain("First").And.NotContain("Second");
        }

        [Test]
        public async Task should_continue_with_next_priority_group_when_nothing_good_enough_found()
        {
            GivenEarlySearchReturn(0);
            GivenSearchIndexersInPriorityOrder();
            var fetched = GivenIndexersWithPriority((1, 0, "First", 5), (2, 0, "Rejected", 10), (3, 0, "Third", 10), (4, 0, "Fourth", 10));

            var titles = await SearchTitles();

            fetched.Should().Equal("First", "Rejected", "Third");
            titles.Should().BeEquivalentTo("First", "Rejected", "Third");
        }

        [Test]
        public async Task should_count_minimum_wait_from_start_of_search_over_priority_groups()
        {
            GivenEarlySearchReturn(2);
            GivenSearchIndexersInPriorityOrder();
            GivenIndexersWithPriority((1, 1500, "First", 5), (2, 0, "Fast", 10), (2, Timeout.Infinite, "Slow", 100));

            var stopwatch = Stopwatch.StartNew();
            var titles = await SearchTitles();

            stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(1.8));
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
            titles.Should().BeEquivalentTo("First", "Fast");
        }

        [Test]
        public async Task should_search_indexers_up_to_required_priority_as_first_group()
        {
            GivenEarlySearchReturn(0, 2);
            GivenSearchIndexersInPriorityOrder();
            var fetched = GivenIndexersWithPriority((1, 0, "First", 10), (2, 200, "Second", 5), (3, 0, "Third", 10));

            var titles = await SearchTitles();

            fetched.Should().BeEquivalentTo("First", "Second");
            titles.Should().BeEquivalentTo("First", "Second");
        }

        [Test]
        public async Task should_search_in_priority_order_for_interactive_search()
        {
            GivenEarlySearchReturn(0);
            GivenSearchIndexersInPriorityOrder();
            var fetched = GivenIndexersWithPriority((1, 0, "First", 10), (1, 200, "Other", 5), (2, 0, "Second", 10));

            var titles = await SearchTitles(true);

            fetched.Should().BeEquivalentTo("First", "Other");
            titles.Should().BeEquivalentTo("First", "Other");
        }

        [TestCase(10, new[] { "First" })]
        [TestCase(5, new[] { "First", "Second" })]
        public async Task should_serve_cached_queries_of_priority_order_search(int firstScore, string[] titles)
        {
            GivenQueryCache();

            GivenEarlySearchReturn(0);
            GivenSearchIndexersInPriorityOrder();
            GivenIndexersWithPriority((1, 0, "First", firstScore), (2, 0, "Second", 10));

            await SearchTitles();

            Mocker.GetMock<IEpisodeService>()
                  .Setup(s => s.GetEpisode(_xemEpisodes.First().Id))
                  .Returns(_xemEpisodes.First());

            Subject.CachedEpisodeSearch(_xemEpisodes.First().Id).Decisions.Select(d => d.RemoteEpisode.Release.Title).Should().BeEquivalentTo(titles);
        }

        [Test]
        public async Task should_serve_answered_queries_when_first_group_was_cut_short()
        {
            GivenQueryCache();

            GivenEarlySearchReturn(0);
            GivenSearchIndexersInPriorityOrder();
            GivenIndexersWithPriority((1, 0, "First", 10), (1, Timeout.Infinite, "Slow", 10), (2, 0, "Second", 10));

            await SearchTitles();

            Mocker.GetMock<IEpisodeService>()
                  .Setup(s => s.GetEpisode(_xemEpisodes.First().Id))
                  .Returns(_xemEpisodes.First());

            Subject.CachedEpisodeSearch(_xemEpisodes.First().Id).Decisions.Select(d => d.RemoteEpisode.Release.Title).Should().BeEquivalentTo("First");
        }

        private void GivenInteractiveSearchStore()
        {
            Mocker.SetConstant<ICacheManager>(Mocker.Resolve<CacheManager>());

            GivenSeasonEpisodes();

            Mocker.GetMock<IEpisodeService>()
                  .Setup(s => s.GetEpisode(_xemEpisodes.First().Id))
                  .Returns(_xemEpisodes.First());
        }

        private async Task<List<string>> InteractiveSearchTitles(bool refresh = false, bool searchRemaining = false)
        {
            var result = await Subject.InteractiveEpisodeSearch(_xemEpisodes.First().Id, refresh, searchRemaining);

            return result.Decisions.Select(d => d.RemoteEpisode.Release.Title).ToList();
        }

        private Dictionary<string, IndexerSearchStatusType> InteractiveStatuses()
        {
            return Subject.InteractiveEpisodeSearchStatus(_xemEpisodes.First().Id).Indexers.ToDictionary(i => i.Name, i => i.Status);
        }

        [Test]
        public void should_return_empty_status_without_interactive_search()
        {
            GivenInteractiveSearchStore();

            var status = Subject.InteractiveEpisodeSearchStatus(_xemEpisodes.First().Id);

            status.CachedAt.Should().BeNull();
            status.Indexers.Should().BeEmpty();
        }

        [Test]
        public async Task should_report_searched_and_skipped_indexers_of_interactive_search()
        {
            GivenInteractiveSearchStore();
            GivenEarlySearchReturn(0);
            GivenSearchIndexersInPriorityOrder();
            GivenIndexersWithPriority((1, 0, "First", 10), (1, 50, "Other", 5), (2, 0, "Second", 10));

            await InteractiveSearchTitles();

            var status = Subject.InteractiveEpisodeSearchStatus(_xemEpisodes.First().Id);

            status.CachedAt.Should().BeNull();
            status.Indexers.Select(i => i.Name).Should().Equal("First", "Other", "Second");
            status.Indexers.Select(i => i.Status).Should().Equal(IndexerSearchStatusType.Searched, IndexerSearchStatusType.Searched, IndexerSearchStatusType.Skipped);
            status.Indexers.Select(i => i.ReleaseCount).Should().Equal(1, 1, 0);
        }

        [Test]
        public async Task should_report_failed_and_timed_out_indexers_and_search_them_again_when_searching_remaining()
        {
            GivenInteractiveSearchStore();
            var fetched = GivenIndexersWithPriority((1, 0, "A", 10), (1, 0, "B", 10), (1, 0, "C", 10));
            var indexers = Mocker.GetMock<IIndexerFactory>().Object.InteractiveSearchEnabled();
            Mock.Get(indexers[1]).Setup(s => s.Fetch(It.IsAny<SingleEpisodeSearchCriteria>())).ThrowsAsync(new Exception("Indexer failed"));
            Mock.Get(indexers[2]).Setup(s => s.Fetch(It.IsAny<SingleEpisodeSearchCriteria>())).ThrowsAsync(new WebException("Http request timed out", WebExceptionStatus.Timeout));

            await InteractiveSearchTitles();

            var status = Subject.InteractiveEpisodeSearchStatus(_xemEpisodes.First().Id);

            status.Indexers.Select(i => i.Status).Should().Equal(IndexerSearchStatusType.Searched, IndexerSearchStatusType.Failed, IndexerSearchStatusType.TimedOut);
            status.Indexers.Single(i => i.Name == "B").Message.Should().Be("Indexer failed");

            await InteractiveSearchTitles(searchRemaining: true);

            fetched.Should().Equal("A");
            Mock.Get(indexers[1]).Verify(v => v.Fetch(It.IsAny<SingleEpisodeSearchCriteria>()), Times.Exactly(2));
            Mock.Get(indexers[2]).Verify(v => v.Fetch(It.IsAny<SingleEpisodeSearchCriteria>()), Times.Exactly(2));
            ExceptionVerification.ExpectedErrors(4);
        }

        [Test]
        public async Task should_report_response_times_of_interactive_search_and_successful_queries_of_all_searches()
        {
            GivenInteractiveSearchStore();
            GivenIndexersWithPriority((1, 100, "Slow", 10), (1, 0, "Failing", 10));
            var indexers = Mocker.GetMock<IIndexerFactory>().Object.InteractiveSearchEnabled();
            Mock.Get(indexers[1]).Setup(s => s.Fetch(It.IsAny<SingleEpisodeSearchCriteria>())).ThrowsAsync(new Exception("Indexer failed"));

            await Subject.EpisodeSearch(_xemEpisodes.First(), true, false);
            await InteractiveSearchTitles();

            var status = Subject.InteractiveEpisodeSearchStatus(_xemEpisodes.First().Id).Indexers.ToDictionary(i => i.Name);

            status["Slow"].QueryCount.Should().Be(1);
            status["Slow"].MedianResponseMs.Should().BeGreaterOrEqualTo(90);
            status["Slow"].HistoryCount.Should().Be(2);
            status["Slow"].HistoryLowMs.Should().BeInRange(90, status["Slow"].HistoryMedianMs.Value);
            status["Slow"].HistoryHighMs.Should().BeGreaterOrEqualTo(status["Slow"].HistoryMedianMs.Value);

            status["Failing"].QueryCount.Should().Be(1);
            status["Failing"].MedianResponseMs.Should().NotBeNull();
            status["Failing"].HistoryCount.Should().BeNull();
            status["Failing"].HistoryMedianMs.Should().BeNull();

            ExceptionVerification.ExpectedErrors(2);
        }

        [Test]
        public async Task should_report_cached_indexers_when_interactive_search_is_served_from_cache()
        {
            GivenInteractiveSearchStore();
            Mocker.GetMock<IConfigService>().SetupGet(s => s.SearchResultCacheLifetime).Returns(60);
            var fetched = GivenIndexersWithPriority((1, 0, "First", 10), (2, 0, "Second", 10));

            await Subject.EpisodeSearch(_xemEpisodes.First(), true, false);
            var titles = await InteractiveSearchTitles();

            fetched.Should().HaveCount(2);
            titles.Should().BeEquivalentTo("First", "Second");

            var status = Subject.InteractiveEpisodeSearchStatus(_xemEpisodes.First().Id);

            status.CachedAt.Should().NotBeNull();
            status.Indexers.Select(i => i.Status).Should().AllBeEquivalentTo(IndexerSearchStatusType.Cached);
            status.Indexers.Should().OnlyContain(i => i.CachedAt.HasValue);
            status.CachedAt.Should().Be(status.Indexers.Min(i => i.CachedAt));
            status.Indexers.Select(i => i.QueryCount).Should().AllBeEquivalentTo((int?)null);
        }

        [Test]
        public async Task should_send_only_uncached_queries_and_report_oldest_cached_at()
        {
            GivenInteractiveSearchStore();
            GivenQueryCache();
            GivenIndexersWithPriority((1, 0, "First", 10), (2, 0, "Second", 10));

            await Subject.EpisodeSearch(_xemEpisodes.First(), true, false);

            var fetched = GivenIndexersWithPriority((1, 0, "First", 10), (2, 0, "Second", 10), (3, 0, "Third", 10));
            var firstFetchedAt = DateTime.UtcNow.AddMinutes(-30);
            var secondFetchedAt = DateTime.UtcNow.AddMinutes(-10);
            GetCachedQuery(1).FetchedAt = firstFetchedAt;
            GetCachedQuery(2).FetchedAt = secondFetchedAt;

            var titles = await InteractiveSearchTitles();

            fetched.Should().Equal("Third");
            titles.Should().BeEquivalentTo("First", "Second", "Third");

            var status = Subject.InteractiveEpisodeSearchStatus(_xemEpisodes.First().Id);

            status.Indexers.Select(i => i.Status).Should().Equal(IndexerSearchStatusType.Cached, IndexerSearchStatusType.Cached, IndexerSearchStatusType.Searched);
            status.Indexers.Select(i => i.CachedAt).Should().Equal(firstFetchedAt, secondFetchedAt, null);
            status.Indexers.Select(i => i.QueryCount).Should().Equal(null, null, 1);
            status.CachedAt.Should().Be(firstFetchedAt);
        }

        [Test]
        public async Task should_send_expired_query_again_without_extending_others()
        {
            GivenInteractiveSearchStore();
            GivenQueryCache();
            var fetched = GivenIndexersWithPriority((1, 0, "First", 10), (2, 0, "Second", 10));

            await Subject.EpisodeSearch(_xemEpisodes.First(), true, false);

            var secondFetchedAt = DateTime.UtcNow.AddMinutes(-59);
            GetCachedQuery(1).FetchedAt = DateTime.UtcNow.AddMinutes(-61);
            GetCachedQuery(2).FetchedAt = secondFetchedAt;

            await InteractiveSearchTitles();

            fetched.Should().Equal("First", "Second", "First");
            InteractiveStatuses().Values.Should().Equal(IndexerSearchStatusType.Searched, IndexerSearchStatusType.Cached);
            GetCachedQuery(1).FetchedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
            GetCachedQuery(2).FetchedAt.Should().Be(secondFetchedAt);
        }

        [Test]
        public async Task should_share_cached_query_between_searches_sending_the_same_request()
        {
            GivenQueryCache();
            GivenSeasonEpisodes();
            var fetched = GivenIndexersWithPriority((1, 0, "First", 10));
            var indexer = Mocker.GetMock<IIndexerFactory>().Object.AutomaticSearchEnabled();
            Mock.Get(indexer[0]).Setup(s => s.GetSearchQueryKey(It.IsAny<SearchCriteriaBase>())).Returns("same request");

            await Subject.EpisodeSearch(_xemEpisodes[0], true, false);
            var decisions = await Subject.EpisodeSearch(_xemEpisodes[1], true, false);

            fetched.Should().HaveCount(1);
            GetQueryCache().Count.Should().Be(1);
            decisions.Select(d => d.RemoteEpisode.Release.Title).Should().BeEquivalentTo("First");
        }

        [Test]
        public async Task should_not_cache_failed_queries()
        {
            GivenQueryCache();
            var fetched = GivenIndexers((0, "Fast", 10), (0, "Failing", 10));

            await SearchTitles();
            await Subject.EpisodeSearch(_xemEpisodes.First(), true, false);

            fetched.Should().Equal("Fast", "Failing", "Failing");
            GetQueryCache().Count.Should().Be(1);
            ExceptionVerification.ExpectedErrors(2);
        }

        [TestCase(0)]
        [TestCase(60)]
        public async Task should_search_only_remaining_indexers_and_merge_results(int cacheLifetime)
        {
            GivenInteractiveSearchStore();
            Mocker.GetMock<IConfigService>().SetupGet(s => s.SearchResultCacheLifetime).Returns(cacheLifetime);
            GivenEarlySearchReturn(0);
            GivenSearchIndexersInPriorityOrder();
            var fetched = GivenIndexersWithPriority((1, 0, "First", 10), (2, 0, "Second", 10), (3, 0, "Third", 10));

            (await InteractiveSearchTitles()).Should().BeEquivalentTo("First");

            var titles = await InteractiveSearchTitles(searchRemaining: true);

            fetched.Should().BeEquivalentTo("First", "Second", "Third");
            titles.Should().BeEquivalentTo("First", "Second", "Third");
            InteractiveStatuses().Values.Should().AllBeEquivalentTo(IndexerSearchStatusType.Searched);
        }

        [Test]
        public async Task should_search_all_indexers_again_on_refresh()
        {
            GivenInteractiveSearchStore();
            Mocker.GetMock<IConfigService>().SetupGet(s => s.SearchResultCacheLifetime).Returns(60);
            var fetched = GivenIndexersWithPriority((1, 0, "First", 10), (2, 0, "Second", 10));

            await InteractiveSearchTitles();
            GetCachedQuery(1).FetchedAt = DateTime.UtcNow.AddMinutes(-30);
            await InteractiveSearchTitles(refresh: true);

            fetched.Should().HaveCount(4);
            InteractiveStatuses().Values.Should().AllBeEquivalentTo(IndexerSearchStatusType.Searched);
            GetCachedQuery(1).FetchedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        }

        private static int InterlockedMax(ref int target, int value)
        {
            int current;

            while (value > (current = target) && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }

            return value;
        }

        [Test]
        public async Task should_not_wait_for_required_priority_indexers_when_required_priority_disabled()
        {
            GivenEarlySearchReturn(0);
            GivenIndexers((0, "Fast", 10), (Timeout.Infinite, "Required", 100));

            var titles = await SearchTitles();

            titles.Should().BeEquivalentTo("Fast");
        }

        [Test]
        public async Task should_wait_for_slow_required_priority_indexer_before_returning_early()
        {
            GivenEarlySearchReturn(0, 10);
            GivenIndexers((0, "Fast", 10), (500, "Required", 5), (Timeout.Infinite, "Slow", 100));

            var stopwatch = Stopwatch.StartNew();
            var titles = await SearchTitles();

            stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(0.4));
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
            titles.Should().BeEquivalentTo("Fast", "Required");
        }

        [Test]
        public async Task should_return_early_once_required_priority_indexer_failed()
        {
            GivenEarlySearchReturn(0, 10);
            GivenIndexers((0, "Fast", 10), (300, "RequiredFailing", 100), (Timeout.Infinite, "Slow", 100));

            var stopwatch = Stopwatch.StartNew();
            var titles = await SearchTitles();

            stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(0.2));
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
            titles.Should().BeEquivalentTo("Fast");
            ExceptionVerification.ExpectedErrors(1);
        }
    }
}
