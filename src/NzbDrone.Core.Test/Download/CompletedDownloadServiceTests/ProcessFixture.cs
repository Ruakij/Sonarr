using System.Collections.Generic;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DataAugmentation.Scene;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Tv;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Download.CompletedDownloadServiceTests
{
    [TestFixture]
    public class ProcessFixture : CoreTest<CompletedDownloadService>
    {
        private TrackedDownload _trackedDownload;

        [SetUp]
        public void Setup()
        {
            var completed = Builder<DownloadClientItem>.CreateNew()
                                                    .With(h => h.Status = DownloadItemStatus.Completed)
                                                    .With(h => h.OutputPath = new OsPath(@"C:\DropFolder\MyDownload".AsOsAgnostic()))
                                                    .With(h => h.Title = "Drone.S01E01.HDTV")
                                                    .Build();

            var remoteEpisode = BuildRemoteEpisode();

            _trackedDownload = Builder<TrackedDownload>.CreateNew()
                    .With(c => c.State = TrackedDownloadState.Downloading)
                    .With(c => c.DownloadItem = completed)
                    .With(c => c.RemoteEpisode = remoteEpisode)
                    .Build();

            Mocker.GetMock<IDownloadClient>()
              .SetupGet(c => c.Definition)
              .Returns(new DownloadClientDefinition { Id = 1, Name = "testClient" });

            Mocker.GetMock<IProvideDownloadClient>()
                  .Setup(c => c.Get(It.IsAny<int>()))
                  .Returns(Mocker.GetMock<IDownloadClient>().Object);

            Mocker.GetMock<IProvideImportItemService>()
                  .Setup(c => c.ProvideImportItem(It.IsAny<DownloadClientItem>(), It.IsAny<DownloadClientItem>()))
                  .Returns((DownloadClientItem item, DownloadClientItem previous) => item);

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(_trackedDownload.DownloadItem.DownloadId))
                  .Returns(new List<EpisodeHistory>());

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetSeries("Drone.S01E01.HDTV"))
                  .Returns(remoteEpisode.Series);
        }

        private RemoteEpisode BuildRemoteEpisode()
        {
            return new RemoteEpisode
            {
                Series = new Series(),
                Episodes = new List<Episode> { new Episode { Id = 1 } }
            };
        }

        private void GivenNoGrabbedHistory()
        {
            Mocker.GetMock<IHistoryService>()
                .Setup(s => s.FindByDownloadId(_trackedDownload.DownloadItem.DownloadId))
                .Returns(new List<EpisodeHistory>());
        }

        private void GivenSeriesMatch()
        {
            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetSeries(It.IsAny<string>()))
                  .Returns(_trackedDownload.RemoteEpisode.Series);
        }

        private void GivenABadlyNamedDownload()
        {
            _trackedDownload.DownloadItem.DownloadId = "1234";
            _trackedDownload.DownloadItem.Title = "Droned Pilot"; // Set a badly named download
            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId(It.Is<string>(i => i == "1234")))
                  .Returns(new List<EpisodeHistory>
                  {
                      new EpisodeHistory() { SourceTitle = "Droned S01E01", EventType = EpisodeHistoryEventType.Grabbed }
                  });

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetSeries(It.IsAny<string>()))
                  .Returns((Series)null);

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetSeries("Droned S01E01"))
                  .Returns(BuildRemoteEpisode().Series);
        }

        private void GivenGrabbedByIdMatch(string releaseTitle, string title, int year, string sceneMappingTitle = null, SeriesMatchType matchType = SeriesMatchType.Id, ReleaseSourceType releaseSource = ReleaseSourceType.Search)
        {
            var series = new Series { Id = 10, TvdbId = 20, Title = title, Year = year };

            Mocker.GetMock<ISceneMappingService>()
                  .Setup(s => s.FindByTvdbId(series.TvdbId))
                  .Returns(sceneMappingTitle == null ? new List<SceneMapping>() : new List<SceneMapping> { new SceneMapping { Title = sceneMappingTitle, TvdbId = series.TvdbId } });

            _trackedDownload.DownloadItem.DownloadId = "1234";
            _trackedDownload.DownloadItem.Title = releaseTitle;

            var history = new EpisodeHistory { SeriesId = series.Id, SourceTitle = releaseTitle, EventType = EpisodeHistoryEventType.Grabbed };
            history.Data[EpisodeHistory.SERIES_MATCH_TYPE] = matchType.ToString();
            history.Data[EpisodeHistory.RELEASE_SOURCE] = releaseSource.ToString();

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.FindByDownloadId("1234"))
                  .Returns(new List<EpisodeHistory> { history });

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetSeries(It.IsAny<string>()))
                  .Returns((Series)null);

            Mocker.GetMock<ISeriesService>()
                  .Setup(s => s.GetSeries(series.Id))
                  .Returns(series);
        }

        private void GivenMinimumTitleSimilarity(int minimumTitleSimilarity)
        {
            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.MinimumTitleSimilarity)
                  .Returns(minimumTitleSimilarity);
        }

        [Test]
        public void should_not_process_id_matched_release_when_minimum_title_similarity_is_disabled()
        {
            GivenGrabbedByIdMatch("Breaking.Bad.S01E01.1080p.BluRay.x264-GRP", "Breaking Bad", 2008);

            Subject.Check(_trackedDownload);

            AssertNotReadyToImport();
        }

        [TestCase("Breaking.Bad.S01E01.1080p.BluRay.x264-GRP", "Breaking Bad", 2008, null)]
        [TestCase("Shingeki.no.Kyojin.S01E01.1080p.BluRay.x264-GRP", "Attack on Titan", 2013, "Shingeki no Kyojin")]
        [TestCase("Marvels.Daredevil.S01E01.1080p.WEB-DL.DDP5.1.H.264-GRP", "Daredevil", 2015, null)]
        [TestCase("Doctor.Who.2005.S01E01.1080p.BluRay.x264-GRP", "Doctor Who", 2005, null)]
        [TestCase("Doctor.Who.2006.S01E01.1080p.BluRay.x264-GRP", "Doctor Who", 2005, null)]
        [TestCase("Law.and.Order.S01E01.1080p.WEB-DL.x264-GRP", "Law & Order", 1990, null)]
        [TestCase("Spider.Man.S01E01.1080p.WEB-DL.x264-GRP", "Spider-Man", 1994, null)]
        [TestCase("X.Men.S01E01.1080p.WEB-DL.x264-GRP", "X-Men", 1992, null)]
        public void should_process_id_matched_release_with_similar_title(string releaseTitle, string title, int year, string sceneMappingTitle)
        {
            GivenMinimumTitleSimilarity(60);
            GivenGrabbedByIdMatch(releaseTitle, title, year, sceneMappingTitle);

            Subject.Check(_trackedDownload);

            AssertReadyToImport();
        }

        [TestCase("Battlestar.Galactica.2003.S01E01.1080p.BluRay.x264-GRP", "Battlestar Galactica", 1978)]
        [TestCase("Lost.S01E01.1080p.BluRay.x264-GRP", "Heroes", 2006)]
        public void should_not_process_id_matched_release_with_dissimilar_title_or_year(string releaseTitle, string title, int year)
        {
            GivenMinimumTitleSimilarity(60);
            GivenGrabbedByIdMatch(releaseTitle, title, year);

            Subject.Check(_trackedDownload);

            AssertNotReadyToImport();
        }

        [Test]
        public void should_process_id_matched_release_without_year_regardless_of_series_year()
        {
            GivenMinimumTitleSimilarity(60);
            GivenGrabbedByIdMatch("Battlestar.Galactica.S01E01.1080p.BluRay.x264-GRP", "Battlestar Galactica", 1978);

            Subject.Check(_trackedDownload);

            AssertReadyToImport();
        }

        // "Dead Poets" vs "Dead Poets Society" is exactly 72% similar
        [TestCase(72, true)]
        [TestCase(73, false)]
        public void should_compare_title_similarity_against_minimum_inclusively(int minimumTitleSimilarity, bool expectedImport)
        {
            GivenMinimumTitleSimilarity(minimumTitleSimilarity);
            GivenGrabbedByIdMatch("Dead.Poets.S01E01.1080p.BluRay.x264-GRP", "Dead Poets Society", 2020);

            Subject.Check(_trackedDownload);

            _trackedDownload.State.Should().Be(expectedImport ? TrackedDownloadState.ImportPending : TrackedDownloadState.ImportBlocked);
        }

        [TestCase(SeriesMatchType.Id, ReleaseSourceType.InteractiveSearch)]
        [TestCase(SeriesMatchType.Title, ReleaseSourceType.Search)]
        public void should_process_release_not_matched_by_id_or_grabbed_interactively_without_title_similarity_check(SeriesMatchType matchType, ReleaseSourceType releaseSource)
        {
            GivenMinimumTitleSimilarity(60);
            GivenGrabbedByIdMatch("Lost.S01E01.1080p.BluRay.x264-GRP", "Heroes", 2006, matchType: matchType, releaseSource: releaseSource);

            Subject.Check(_trackedDownload);

            AssertReadyToImport();
            Mocker.GetMock<ISceneMappingService>()
                  .Verify(s => s.FindByTvdbId(It.IsAny<int>()), Times.Never());
        }

        [TestCase(DownloadItemStatus.Downloading)]
        [TestCase(DownloadItemStatus.Failed)]
        [TestCase(DownloadItemStatus.Queued)]
        [TestCase(DownloadItemStatus.Paused)]
        [TestCase(DownloadItemStatus.Warning)]
        public void should_not_process_if_download_status_isnt_completed(DownloadItemStatus status)
        {
            _trackedDownload.DownloadItem.Status = status;

            Subject.Check(_trackedDownload);

            AssertNotReadyToImport();
        }

        [Test]
        public void should_not_process_if_matching_history_is_not_found_and_no_category_specified()
        {
            _trackedDownload.DownloadItem.Category = null;
            GivenNoGrabbedHistory();

            Subject.Check(_trackedDownload);

            AssertNotReadyToImport();
        }

        [Test]
        public void should_process_if_matching_history_is_not_found_but_category_specified()
        {
            _trackedDownload.DownloadItem.Category = "tv";
            GivenNoGrabbedHistory();
            GivenSeriesMatch();

            Subject.Check(_trackedDownload);

            AssertReadyToImport();
        }

        [Test]
        public void should_not_process_if_output_path_is_empty()
        {
            _trackedDownload.DownloadItem.OutputPath = default(OsPath);

            Subject.Check(_trackedDownload);

            AssertNotReadyToImport();
        }

        [Test]
        public void should_not_process_if_the_download_cannot_be_tracked_using_the_source_title_as_it_was_initiated_externally()
        {
            GivenABadlyNamedDownload();

            Mocker.GetMock<IDownloadedEpisodesImportService>()
                  .Setup(v => v.ProcessPath(It.IsAny<string>(), It.IsAny<ImportMode>(), It.IsAny<Series>(), It.IsAny<DownloadClientItem>()))
                  .Returns(new List<ImportResult>
                           {
                               new ImportResult(new ImportDecision(new LocalEpisode { Path = @"C:\TestPath\Droned.S01E01.mkv" }))
                           });

            Subject.Check(_trackedDownload);

            AssertNotReadyToImport();
        }

        [Test]
        public void should_not_process_when_there_is_a_title_mismatch()
        {
            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetSeries("Drone.S01E01.HDTV"))
                  .Returns((Series)null);

            Subject.Check(_trackedDownload);

            AssertNotReadyToImport();
        }

        private void AssertNotReadyToImport()
        {
            _trackedDownload.State.Should().NotBe(TrackedDownloadState.ImportPending);
        }

        private void AssertReadyToImport()
        {
            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportPending);
        }
    }
}
