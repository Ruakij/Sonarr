using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download.DownloadProcessingServiceTests
{
    [TestFixture]
    public class ManualImportTimeoutFixture : CoreTest<DownloadProcessingService>
    {
        private TrackedDownload _trackedDownload;

        [SetUp]
        public void Setup()
        {
            var downloadItem = Builder<DownloadClientItem>.CreateNew()
                                                          .With(h => h.Status = DownloadItemStatus.Completed)
                                                          .With(h => h.CanBeRemoved = false)
                                                          .Build();

            _trackedDownload = Builder<TrackedDownload>.CreateNew()
                                                       .With(c => c.State = TrackedDownloadState.ImportBlocked)
                                                       .With(c => c.DownloadItem = downloadItem)
                                                       .With(c => c.Added = DateTime.UtcNow.AddDays(-1))
                                                       .With(c => c.IsTrackable = true)
                                                       .With(c => c.ManualInteractionRequiredSince = null)
                                                       .With(c => c.ImportRejectedPermanently = true)
                                                       .Build();

            Mocker.GetMock<ITrackedDownloadService>()
                  .Setup(s => s.GetTrackedDownloads())
                  .Returns(new List<TrackedDownload> { _trackedDownload });

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.Find(It.IsAny<string>(), It.IsAny<EpisodeHistoryEventType>()))
                  .Returns(new List<EpisodeHistory>());
        }

        private void GivenTimeout(int minutes)
        {
            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.ManualImportTimeout)
                  .Returns(minutes);
        }

        private void GivenBlockedFor(int minutes)
        {
            _trackedDownload.ManualInteractionRequiredSince = DateTime.UtcNow.AddMinutes(-minutes);
        }

        private void GivenImportPendingWithWarning(bool rejectedPermanently)
        {
            _trackedDownload.State = TrackedDownloadState.ImportPending;
            _trackedDownload.ImportRejectedPermanently = rejectedPermanently;
            _trackedDownload.Warn("Import rejected");
        }

        private void VerifyFailed()
        {
            _trackedDownload.State.Should().Be(TrackedDownloadState.FailedPending);
            _trackedDownload.DownloadItem.CanBeRemoved.Should().BeTrue();

            Mocker.GetMock<IFailedDownloadService>()
                  .Verify(v => v.ProcessFailed(_trackedDownload), Times.Once());
        }

        private void VerifyNotFailed()
        {
            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportBlocked);

            Mocker.GetMock<IFailedDownloadService>()
                  .Verify(v => v.ProcessFailed(It.IsAny<TrackedDownload>()), Times.Never());
        }

        [Test]
        public void should_not_fail_when_timeout_is_disabled()
        {
            GivenTimeout(-1);
            GivenBlockedFor(60 * 24);

            Subject.Execute(new ProcessMonitoredDownloadsCommand());

            VerifyNotFailed();
        }

        [Test]
        public void should_fail_immediately_when_timeout_is_zero()
        {
            GivenTimeout(0);

            Subject.Execute(new ProcessMonitoredDownloadsCommand());

            VerifyFailed();
        }

        [Test]
        public void should_not_fail_before_timeout_is_reached()
        {
            GivenTimeout(10);
            GivenBlockedFor(5);

            Subject.Execute(new ProcessMonitoredDownloadsCommand());

            VerifyNotFailed();
            _trackedDownload.ManualInteractionRequiredSince.Should().NotBeNull();
        }

        [Test]
        public void should_fail_when_timeout_is_reached()
        {
            GivenTimeout(10);
            GivenBlockedFor(15);

            Subject.Execute(new ProcessMonitoredDownloadsCommand());

            VerifyFailed();
        }

        [Test]
        public void should_not_fail_download_that_was_not_grabbed()
        {
            GivenTimeout(0);
            _trackedDownload.Added = null;

            Subject.Execute(new ProcessMonitoredDownloadsCommand());

            VerifyNotFailed();
        }

        [Test]
        public void should_reset_start_when_no_longer_blocked()
        {
            GivenTimeout(10);
            GivenBlockedFor(15);
            _trackedDownload.State = TrackedDownloadState.Downloading;

            Subject.Execute(new ProcessMonitoredDownloadsCommand());

            _trackedDownload.ManualInteractionRequiredSince.Should().BeNull();
        }

        [Test]
        public void should_not_count_import_pending_without_permanent_rejection()
        {
            GivenTimeout(0);
            GivenImportPendingWithWarning(false);

            Subject.Execute(new ProcessMonitoredDownloadsCommand());

            _trackedDownload.State.Should().Be(TrackedDownloadState.ImportPending);
            _trackedDownload.ManualInteractionRequiredSince.Should().BeNull();

            Mocker.GetMock<IFailedDownloadService>()
                  .Verify(v => v.ProcessFailed(It.IsAny<TrackedDownload>()), Times.Never());
        }

        [Test]
        public void should_fail_import_pending_with_permanent_rejection()
        {
            GivenTimeout(0);
            GivenImportPendingWithWarning(true);

            Subject.Execute(new ProcessMonitoredDownloadsCommand());

            VerifyFailed();
        }

        [Test]
        public void should_keep_countdown_and_single_deadline_message_across_runs()
        {
            GivenTimeout(10);
            _trackedDownload.Warn("Series title mismatch");

            Subject.Execute(new ProcessMonitoredDownloadsCommand());

            var since = _trackedDownload.ManualInteractionRequiredSince;
            since.Should().NotBeNull();

            Subject.Execute(new ProcessMonitoredDownloadsCommand());

            VerifyNotFailed();
            _trackedDownload.ManualInteractionRequiredSince.Should().Be(since);
            _trackedDownload.StatusMessages.Should().HaveCount(2);
            _trackedDownload.StatusMessages.Last().Messages.Single().Should().StartWith("Fails automatically at ");
        }

        [Test]
        public void should_not_count_import_blocked_by_transient_rejections()
        {
            GivenTimeout(0);
            _trackedDownload.ImportRejectedPermanently = false;

            Subject.Execute(new ProcessMonitoredDownloadsCommand());

            VerifyNotFailed();
            _trackedDownload.ManualInteractionRequiredSince.Should().BeNull();
        }

        [Test]
        public void should_not_fail_partially_imported_download()
        {
            GivenTimeout(0);

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.Find(_trackedDownload.DownloadItem.DownloadId, EpisodeHistoryEventType.DownloadFolderImported))
                  .Returns(Builder<EpisodeHistory>.CreateListOfSize(1).BuildList());

            Subject.Execute(new ProcessMonitoredDownloadsCommand());

            VerifyNotFailed();
            _trackedDownload.StatusMessages.Should().NotContain(m => m.Title == "Manual Import Timeout");
        }

        [Test]
        public void should_report_timeout_as_failure_message()
        {
            GivenTimeout(10);
            GivenBlockedFor(15);

            Mocker.GetMock<IHistoryService>()
                  .Setup(s => s.Find(_trackedDownload.DownloadItem.DownloadId, EpisodeHistoryEventType.Grabbed))
                  .Returns(Builder<EpisodeHistory>.CreateListOfSize(1).BuildList());

            Mocker.SetConstant<IFailedDownloadService>(Mocker.Resolve<FailedDownloadService>());

            Subject.Execute(new ProcessMonitoredDownloadsCommand());

            _trackedDownload.State.Should().Be(TrackedDownloadState.Failed);

            Mocker.GetMock<IEventAggregator>()
                  .Verify(v => v.PublishEvent(It.Is<DownloadFailedEvent>(e => e.Message == "Manual import timed out after 10 minutes")), Times.Once());
        }
    }
}
