using System;
using System.Linq;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Download
{
    public class DownloadProcessingService : IExecute<ProcessMonitoredDownloadsCommand>
    {
        private const string DeadlineMessageTitle = "Manual Import Timeout";

        private readonly IConfigService _configService;
        private readonly ICompletedDownloadService _completedDownloadService;
        private readonly IFailedDownloadService _failedDownloadService;
        private readonly ITrackedDownloadService _trackedDownloadService;
        private readonly IHistoryService _historyService;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public DownloadProcessingService(IConfigService configService,
                                         ICompletedDownloadService completedDownloadService,
                                         IFailedDownloadService failedDownloadService,
                                         ITrackedDownloadService trackedDownloadService,
                                         IHistoryService historyService,
                                         IEventAggregator eventAggregator,
                                         Logger logger)
        {
            _configService = configService;
            _completedDownloadService = completedDownloadService;
            _failedDownloadService = failedDownloadService;
            _trackedDownloadService = trackedDownloadService;
            _historyService = historyService;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        private void RemoveCompletedDownloads()
        {
            var trackedDownloads = _trackedDownloadService.GetTrackedDownloads()
                                                          .Where(t => !t.DownloadItem.Removed && t.DownloadItem.CanBeRemoved && t.State == TrackedDownloadState.Imported)
                                                          .ToList();

            foreach (var trackedDownload in trackedDownloads)
            {
                _eventAggregator.PublishEvent(new DownloadCanBeRemovedEvent(trackedDownload));
            }
        }

        private void CheckManualImportTimeout(TrackedDownload trackedDownload)
        {
            var requiresManualInteraction = trackedDownload.ImportRejectedPermanently &&
                                            (trackedDownload.State == TrackedDownloadState.ImportBlocked ||
                                             (trackedDownload.State == TrackedDownloadState.ImportPending &&
                                              trackedDownload.Status == TrackedDownloadStatus.Warning));

            if (!requiresManualInteraction)
            {
                trackedDownload.ManualInteractionRequiredSince = null;
                return;
            }

            trackedDownload.ManualInteractionRequiredSince ??= DateTime.UtcNow;

            var timeout = _configService.ManualImportTimeout;

            // Without a grab there is no release to blocklist or episode to search for again
            if (timeout < 0 || trackedDownload.Added == null)
            {
                return;
            }

            // Failing a partially imported download would blocklist a release that already provided episodes and search again for those
            if (_historyService.Find(trackedDownload.DownloadItem.DownloadId, EpisodeHistoryEventType.DownloadFolderImported).Any())
            {
                return;
            }

            var deadline = trackedDownload.ManualInteractionRequiredSince.Value.AddMinutes(timeout);

            if (DateTime.UtcNow < deadline)
            {
                // Status messages of blocked downloads survive between runs, replace an earlier deadline instead of adding another
                trackedDownload.Warn(trackedDownload.StatusMessages
                                                    .Where(m => m.Title != DeadlineMessageTitle)
                                                    .Append(new TrackedDownloadStatusMessage(DeadlineMessageTitle, $"Fails automatically at {deadline.ToLocalTime():yyyy-MM-dd HH:mm} if not imported"))
                                                    .ToArray());
                return;
            }

            _logger.Info("Download '{0}' has been waiting for manual interaction for more than {1} minutes, marking as failed", trackedDownload.DownloadItem.Title, timeout);
            trackedDownload.Fail($"Manual import timed out after {timeout} minutes");
        }

        public void Execute(ProcessMonitoredDownloadsCommand message)
        {
            var enableCompletedDownloadHandling = _configService.EnableCompletedDownloadHandling;
            var trackedDownloads = _trackedDownloadService.GetTrackedDownloads()
                                                          .Where(t => t.IsTrackable)
                                                          .ToList();

            foreach (var trackedDownload in trackedDownloads)
            {
                try
                {
                    // Process completed items followed by failed, this allows failed imports to have
                    // their state changed and be processed immediately instead of the next execution.

                    if (enableCompletedDownloadHandling && trackedDownload.State == TrackedDownloadState.ImportPending)
                    {
                        _completedDownloadService.Import(trackedDownload);
                    }

                    CheckManualImportTimeout(trackedDownload);

                    if (trackedDownload.State == TrackedDownloadState.FailedPending)
                    {
                        _failedDownloadService.ProcessFailed(trackedDownload);
                    }
                }
                catch (Exception e)
                {
                    _logger.Debug(e, "Failed to process download: {0}", trackedDownload.DownloadItem.Title);
                }
            }

            // Imported downloads are no longer trackable so process them after processing trackable downloads
            RemoveCompletedDownloads();

            _eventAggregator.PublishEvent(new DownloadsProcessedEvent());
        }
    }
}
