using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    [TestFixture]
    public class SeriesSearchServiceFixture : CoreTest<SeriesSearchService>
    {
        private Series _series;

        [SetUp]
        public void Setup()
        {
            _series = new Series
                      {
                          Id = 1,
                          Title = "Title",
                          Seasons = new List<Season>()
                      };

            Mocker.GetMock<ISeriesService>()
                  .Setup(s => s.GetSeries(It.IsAny<int>()))
                  .Returns(_series);

            Mocker.GetMock<ISearchForReleases>()
                  .Setup(s => s.SeasonSearch(_series.Id, It.IsAny<int>(), false, false, true, false, false))
                  .Returns(Task.FromResult(new List<DownloadDecision>()));

            Mocker.GetMock<IProcessDownloadDecisions>()
                  .Setup(s => s.ProcessDecisions(It.IsAny<List<DownloadDecision>>()))
                  .Returns(Task.FromResult(new ProcessedDecisions(new List<DownloadDecision>(), new List<DownloadDecision>(), new List<DownloadDecision>())));
        }

        [Test]
        public void should_only_include_monitored_seasons()
        {
            _series.Seasons = new List<Season>
                              {
                                  new Season { SeasonNumber = 0, Monitored = false },
                                  new Season { SeasonNumber = 1, Monitored = true }
                              };

            Subject.Execute(new SeriesSearchCommand { SeriesId = _series.Id, Trigger = CommandTrigger.Manual });

            Mocker.GetMock<ISearchForReleases>()
                .Verify(v => v.SeasonSearch(_series.Id, It.IsAny<int>(), false, true, true, false, false), Times.Exactly(_series.Seasons.Count(s => s.Monitored)));
        }

        [Test]
        public void should_start_with_lower_seasons_first()
        {
            var seasonOrder = new List<int>();

            _series.Seasons = new List<Season>
                              {
                                  new Season { SeasonNumber = 3, Monitored = true },
                                  new Season { SeasonNumber = 1, Monitored = true },
                                  new Season { SeasonNumber = 2, Monitored = true }
                              };

            Mocker.GetMock<ISearchForReleases>()
                  .Setup(s => s.SeasonSearch(_series.Id, It.IsAny<int>(), false, true, true, false, false))
                  .Returns(Task.FromResult(new List<DownloadDecision>()))
                  .Callback<int, int, bool, bool, bool, bool, bool>((seriesId, seasonNumber, missingOnly, monitoredOnly, userInvokedSearch, interactiveSearch, useCache) => seasonOrder.Add(seasonNumber));

            Subject.Execute(new SeriesSearchCommand { SeriesId = _series.Id, Trigger = CommandTrigger.Manual });

            seasonOrder.First().Should().Be(_series.Seasons.OrderBy(s => s.SeasonNumber).First().SeasonNumber);
        }

        private static DownloadDecision Decision(params int[] seasonNumbers)
        {
            var episodes = seasonNumbers.Select(s => new Episode { Id = s, SeasonNumber = s }).ToList();

            return new DownloadDecision(new RemoteEpisode { Episodes = episodes, Release = new ReleaseInfo { Title = string.Join("-", seasonNumbers) } });
        }

        // Searches finish the slower the lower the season, a season pack covers its own season, season 2 also finds the pack of seasons 1 and 2
        private List<List<string>> GivenSeasonSearches(int concurrency, int seasons, Action<int> onStart = null, Action onEnd = null)
        {
            var processed = new List<List<string>>();

            _series.Seasons = Enumerable.Range(1, seasons).Reverse().Select(n => new Season { SeasonNumber = n, Monitored = true }).ToList();

            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.EpisodeSearchConcurrency)
                  .Returns(concurrency);

            Mocker.GetMock<ISearchForReleases>()
                  .Setup(s => s.SeasonSearch(_series.Id, It.IsAny<int>(), false, true, true, false, false))
                  .Returns<int, int, bool, bool, bool, bool, bool>(async (seriesId, seasonNumber, missingOnly, monitoredOnly, userInvokedSearch, interactiveSearch, useCache) =>
                  {
                      onStart?.Invoke(seasonNumber);
                      await Task.Delay((seasons - seasonNumber + 1) * 20);
                      onEnd?.Invoke();

                      return seasonNumber == 2
                          ? new List<DownloadDecision> { Decision(1, 2), Decision(2) }
                          : new List<DownloadDecision> { Decision(1, 2), Decision(seasonNumber) };
                  });

            Mocker.GetMock<IProcessDownloadDecisions>()
                  .Setup(s => s.ProcessDecisions(It.IsAny<List<DownloadDecision>>()))
                  .Returns<List<DownloadDecision>>(decisions =>
                  {
                      processed.Add(decisions.Select(d => d.RemoteEpisode.Release.Title).ToList());

                      return Task.FromResult(new ProcessedDecisions(decisions.Take(1).ToList(), new List<DownloadDecision>(), new List<DownloadDecision>()));
                  });

            return processed;
        }

        [Test]
        public void should_run_season_searches_up_to_concurrency()
        {
            var running = 0;
            var maxRunning = 0;

            GivenSeasonSearches(2, 5, _ => maxRunning = Math.Max(maxRunning, Interlocked.Increment(ref running)), () => Interlocked.Decrement(ref running));

            Subject.Execute(new SeriesSearchCommand { SeriesId = _series.Id, Trigger = CommandTrigger.Manual });

            maxRunning.Should().Be(2);
        }

        [TestCase(2)]
        [TestCase(4)]
        public void should_process_seasons_like_sequential_search(int concurrency)
        {
            var sequential = GivenSeasonSearches(1, 4);
            Subject.Execute(new SeriesSearchCommand { SeriesId = _series.Id, Trigger = CommandTrigger.Manual });

            var parallel = GivenSeasonSearches(concurrency, 4);
            Subject.Execute(new SeriesSearchCommand { SeriesId = _series.Id, Trigger = CommandTrigger.Manual });

            parallel.Should().BeEquivalentTo(sequential, o => o.WithStrictOrdering());
        }

        [Test]
        public void should_not_process_release_for_episodes_grabbed_by_an_earlier_season()
        {
            var processed = GivenSeasonSearches(3, 3);

            Subject.Execute(new SeriesSearchCommand { SeriesId = _series.Id, Trigger = CommandTrigger.Manual });

            processed.Should().BeEquivalentTo(new List<List<string>>
            {
                new List<string> { "1-2", "1" },
                new List<string>(),
                new List<string> { "3" }
            }, o => o.WithStrictOrdering());
        }

        [Test]
        public void should_skip_failed_search_and_process_the_others()
        {
            var count = EpisodeSearchService.SearchAndProcess(new[] { 1, 2, 3 }, 3, Mocker.GetMock<IProcessDownloadDecisions>().Object, n =>
                n == 2 ? Task.FromResult<List<DownloadDecision>>(null) : Task.FromResult(new List<DownloadDecision> { Decision(n) })).GetAwaiter().GetResult();

            Mocker.GetMock<IProcessDownloadDecisions>()
                  .Verify(v => v.ProcessDecisions(It.IsAny<List<DownloadDecision>>()), Times.Exactly(2));
            count.Should().Be(0);
        }
    }
}
