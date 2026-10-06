using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
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
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

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

        private void GivenIndexers(params (int DelayMs, string Title, int Score)[] indexers)
        {
            var result = indexers.Select((indexer, i) =>
            {
                var mock = new Mock<IIndexer>();
                mock.SetupGet(s => s.Definition).Returns(new IndexerDefinition { Id = i + 1 });

                Func<Task<IList<ReleaseInfo>>> fetch = () => indexer.DelayMs == Timeout.Infinite
                    ? _neverAnswers.Task
                    : FetchDelayed(indexer.DelayMs, new ReleaseInfo { IndexerId = i + 1, Title = indexer.Title, Guid = indexer.Title, Size = indexer.Score });

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

            return new List<ReleaseInfo> { release };
        }

        private void GivenEarlySearchReturn(int minimumWait)
        {
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
            Mocker.SetConstant<ICacheManager>(Mocker.Resolve<CacheManager>());
            Mocker.GetMock<IConfigService>().SetupGet(s => s.SearchResultCacheLifetime).Returns(60);

            GivenEarlySearchReturn(0);
            GivenIndexers((0, "Fast", 10), (Timeout.Infinite, "Slow", 100));

            await SearchTitles();

            Mocker.GetMock<IEpisodeService>()
                  .Setup(s => s.GetEpisode(_xemEpisodes.First().Id))
                  .Returns(_xemEpisodes.First());

            Mocker.GetMock<ISeriesService>()
                  .Setup(s => s.GetSeries(_xemSeries.Id))
                  .Returns(_xemSeries);

            Subject.CachedEpisodeSearch(_xemEpisodes.First().Id, false).Decisions.Select(d => d.RemoteEpisode.Release.Title).Should().BeEquivalentTo("Fast");
        }

        [Test]
        public async Task should_not_serve_early_returned_search_to_interactive_search()
        {
            Mocker.SetConstant<ICacheManager>(Mocker.Resolve<CacheManager>());
            Mocker.GetMock<IConfigService>().SetupGet(s => s.SearchResultCacheLifetime).Returns(60);

            GivenEarlySearchReturn(0);
            GivenIndexers((0, "Fast", 10), (Timeout.Infinite, "Slow", 100));

            await SearchTitles();

            Mocker.GetMock<IEpisodeService>()
                  .Setup(s => s.GetEpisode(_xemEpisodes.First().Id))
                  .Returns(_xemEpisodes.First());

            Subject.CachedEpisodeSearch(_xemEpisodes.First().Id, true).Should().BeNull();
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

        private static int InterlockedMax(ref int target, int value)
        {
            int current;

            while (value > (current = target) && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }

            return value;
        }
    }
}
