using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DataAugmentation.Scene;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Download
{
    public interface ICompletedDownloadService
    {
        void Check(TrackedDownload trackedDownload);
        void Import(TrackedDownload trackedDownload);
        bool VerifyImport(TrackedDownload trackedDownload, List<ImportResult> importResults);
    }

    public class CompletedDownloadService : ICompletedDownloadService
    {
        private static readonly HashSet<ImportRejectionReason> TransientRejectionReasons = new HashSet<ImportRejectionReason>
        {
            ImportRejectionReason.Unpacking,
            ImportRejectionReason.FileLocked,
            ImportRejectionReason.MinimumFreeSpace
        };

        private readonly IEventAggregator _eventAggregator;
        private readonly IHistoryService _historyService;
        private readonly IProvideImportItemService _provideImportItemService;
        private readonly IDownloadedEpisodesImportService _downloadedEpisodesImportService;
        private readonly IParsingService _parsingService;
        private readonly ISeriesService _seriesService;
        private readonly ITrackedDownloadAlreadyImported _trackedDownloadAlreadyImported;
        private readonly IEpisodeService _episodeService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IRejectedImportService _rejectedImportService;
        private readonly IConfigService _configService;
        private readonly ISceneMappingService _sceneMappingService;
        private readonly Logger _logger;

        public CompletedDownloadService(IEventAggregator eventAggregator,
                                        IHistoryService historyService,
                                        IProvideImportItemService provideImportItemService,
                                        IDownloadedEpisodesImportService downloadedEpisodesImportService,
                                        IParsingService parsingService,
                                        ISeriesService seriesService,
                                        ITrackedDownloadAlreadyImported trackedDownloadAlreadyImported,
                                        IEpisodeService episodeService,
                                        IMediaFileService mediaFileService,
                                        IRejectedImportService rejectedImportService,
                                        IConfigService configService,
                                        ISceneMappingService sceneMappingService,
                                        Logger logger)
        {
            _eventAggregator = eventAggregator;
            _historyService = historyService;
            _provideImportItemService = provideImportItemService;
            _downloadedEpisodesImportService = downloadedEpisodesImportService;
            _parsingService = parsingService;
            _seriesService = seriesService;
            _trackedDownloadAlreadyImported = trackedDownloadAlreadyImported;
            _episodeService = episodeService;
            _mediaFileService = mediaFileService;
            _rejectedImportService = rejectedImportService;
            _configService = configService;
            _sceneMappingService = sceneMappingService;
            _logger = logger;
        }

        public void Check(TrackedDownload trackedDownload)
        {
            if (trackedDownload.DownloadItem.Status != DownloadItemStatus.Completed)
            {
                return;
            }

            SetImportItem(trackedDownload);

            // Only process tracked downloads that are still downloading or have been blocked for importing due to an issue with matching
            if (trackedDownload.State != TrackedDownloadState.Downloading && trackedDownload.State != TrackedDownloadState.ImportBlocked)
            {
                return;
            }

            var grabbedHistories = _historyService.FindByDownloadId(trackedDownload.DownloadItem.DownloadId).Where(h => h.EventType == EpisodeHistoryEventType.Grabbed).ToList();
            var historyItem = grabbedHistories.MaxBy(h => h.Date);

            if (historyItem == null && trackedDownload.DownloadItem.Category.IsNullOrWhiteSpace())
            {
                trackedDownload.Warn("Download wasn't grabbed by Sonarr and not in a category, Skipping.");
                return;
            }

            if (!ValidatePath(trackedDownload))
            {
                return;
            }

            var series = _parsingService.GetSeries(trackedDownload.DownloadItem.Title);

            if (series == null)
            {
                if (historyItem != null)
                {
                    series = _seriesService.GetSeries(historyItem.SeriesId);
                }

                if (series == null)
                {
                    trackedDownload.Warn("Series title mismatch; automatic import is not possible. Check the download troubleshooting entry on the wiki for common causes.");
                    SetStateToImportBlocked(trackedDownload);

                    return;
                }

                Enum.TryParse(historyItem.Data.GetValueOrDefault(EpisodeHistory.SERIES_MATCH_TYPE, SeriesMatchType.Unknown.ToString()), out SeriesMatchType seriesMatchType);
                Enum.TryParse(historyItem.Data.GetValueOrDefault(EpisodeHistory.RELEASE_SOURCE, ReleaseSourceType.Unknown.ToString()), out ReleaseSourceType releaseSource);

                // Show a warning if the release was matched by ID and the source is not interactive search
                if (seriesMatchType == SeriesMatchType.Id && releaseSource != ReleaseSourceType.InteractiveSearch && !IsSimilarTitle(trackedDownload, series))
                {
                    trackedDownload.Warn("Found matching series via grab history, but release was matched to series by ID. Automatic import is not possible. See the FAQ for details.");
                    SetStateToImportBlocked(trackedDownload);

                    return;
                }
            }

            trackedDownload.State = TrackedDownloadState.ImportPending;
        }

        private bool IsSimilarTitle(TrackedDownload trackedDownload, Series series)
        {
            var minimumSimilarity = _configService.MinimumTitleSimilarity;

            if (minimumSimilarity <= 0)
            {
                return false;
            }

            var titleInfo = Parser.Parser.ParseTitle(trackedDownload.DownloadItem.Title)?.SeriesTitleInfo;

            if (titleInfo == null)
            {
                return false;
            }

            // Series releases rarely carry a year, but when they do it tells remakes and reboots apart
            if (titleInfo.Year > 0 && series.Year > 0 && Math.Abs(titleInfo.Year - series.Year) > 1)
            {
                return false;
            }

            var parsedTitles = new[] { titleInfo.TitleWithoutYear }
                .Concat(titleInfo.AllTitles ?? Array.Empty<string>())
                .Where(t => t.IsNotNullOrWhiteSpace())
                .ToList();

            var seriesTitles = new[] { series.Title }
                .Concat(series.TvdbId > 0 ? _sceneMappingService.FindByTvdbId(series.TvdbId).Select(m => m.Title) : Array.Empty<string>())
                .Where(t => t.IsNotNullOrWhiteSpace())
                .ToList();

            var best = parsedTitles
                .SelectMany(parsedTitle => seriesTitles.Select(seriesTitle => (ParsedTitle: parsedTitle, SeriesTitle: seriesTitle, Similarity: TitleSimilarity(parsedTitle, seriesTitle))))
                .MaxBy(m => m.Similarity);

            if (best.Similarity < minimumSimilarity)
            {
                return false;
            }

            _logger.Info("Release '{0}' was matched to series by ID, accepting it for import as '{1}' is {2:0}% similar to '{3}' ({4})", trackedDownload.DownloadItem.Title, best.ParsedTitle, best.Similarity, best.SeriesTitle, series.Year);

            return true;
        }

        // Dice coefficient over distinct words weighted by word length: independent of word order,
        // relative to the length of both titles and short words like "of" weigh little
        private static double TitleSimilarity(string first, string second)
        {
            var firstWords = TitleWords(first);
            var secondWords = TitleWords(second);
            var totalLength = firstWords.Sum(w => w.Length) + secondWords.Sum(w => w.Length);

            if (totalLength == 0)
            {
                return 0;
            }

            return 200.0 * firstWords.Intersect(secondWords).Sum(w => w.Length) / totalLength;
        }

        private static HashSet<string> TitleWords(string title)
        {
            return Parser.Parser.NormalizeTitle(title.Replace("&", " and ").Replace('-', ' ')).RemoveAccent()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet();
        }

        public void Import(TrackedDownload trackedDownload)
        {
            trackedDownload.ImportRejectedPermanently = false;

            SetImportItem(trackedDownload);

            if (!ValidatePath(trackedDownload))
            {
                return;
            }

            if (trackedDownload.RemoteEpisode == null)
            {
                trackedDownload.Warn("Unable to parse download, automatic import is not possible.");
                SetStateToImportBlocked(trackedDownload);

                return;
            }

            trackedDownload.State = TrackedDownloadState.Importing;

            var outputPath = trackedDownload.ImportItem.OutputPath.FullPath;
            var importResults = _downloadedEpisodesImportService.ProcessPath(outputPath,
                ImportMode.Auto,
                trackedDownload.RemoteEpisode.Series,
                trackedDownload.ImportItem);

            if (VerifyImport(trackedDownload, importResults))
            {
                return;
            }

            trackedDownload.State = TrackedDownloadState.ImportPending;

            var rejections = importResults.SelectMany(r => r.ImportDecision.Rejections).ToList();
            trackedDownload.ImportRejectedPermanently = rejections.Any() && !rejections.Any(r => TransientRejectionReasons.Contains(r.Reason));

            if (importResults.Empty())
            {
                trackedDownload.Warn("No files found are eligible for import in {0}", outputPath);

                return;
            }

            if (importResults.Count == 1)
            {
                var firstResult = importResults.First();

                if (_rejectedImportService.Process(trackedDownload, firstResult))
                {
                    return;
                }
            }

            var statusMessages = new List<TrackedDownloadStatusMessage>
                                 {
                                    new TrackedDownloadStatusMessage("One or more episodes expected in this release were not imported or missing from the release", new List<string>())
                                 };

            if (importResults.Any(c => c.Result != ImportResultType.Imported))
            {
                statusMessages.AddRange(
                    importResults
                        .Where(v => v.Result != ImportResultType.Imported && v.ImportDecision.LocalEpisode != null)
                        .OrderBy(v => v.ImportDecision.LocalEpisode.Path)
                        .Select(v =>
                            new TrackedDownloadStatusMessage(Path.GetFileName(v.ImportDecision.LocalEpisode.Path),
                                v.Errors)));
            }

            if (statusMessages.Any())
            {
                trackedDownload.Warn(statusMessages.ToArray());
                SetStateToImportBlocked(trackedDownload, trackedDownload.ImportRejectedPermanently);
            }
        }

        public bool VerifyImport(TrackedDownload trackedDownload, List<ImportResult> importResults)
        {
            var allEpisodesImported = importResults.Where(c => c.Result == ImportResultType.Imported)
                                                   .SelectMany(c => c.ImportDecision.LocalEpisode.Episodes)
                                                   .Count() >= Math.Max(1,
                                          trackedDownload.RemoteEpisode.Episodes.Count);

            var historyItems = _historyService.FindByDownloadId(trackedDownload.DownloadItem.DownloadId)
                .OrderByDescending(h => h.Date)
                .ToList();

            var grabbedHistory = historyItems.Where(h => h.EventType == EpisodeHistoryEventType.Grabbed).ToList();
            var releaseInfo = grabbedHistory.Count > 0 ? new GrabbedReleaseInfo(grabbedHistory) : null;

            if (allEpisodesImported)
            {
                _logger.Debug("All episodes were imported for {0}", trackedDownload.DownloadItem.Title);
                trackedDownload.State = TrackedDownloadState.Imported;

                _eventAggregator.PublishEvent(new DownloadCompletedEvent(trackedDownload,
                    trackedDownload.RemoteEpisode.Series.Id,
                    importResults.Where(c => c.Result == ImportResultType.Imported).Select(c => c.EpisodeFile).ToList(),
                    releaseInfo));

                return true;
            }

            // Double check if all episodes were imported by checking the history if at least one
            // file was imported. This will allow the decision engine to reject already imported
            // episode files and still mark the download complete when all files are imported.

            // EDGE CASE: This process relies on EpisodeIds being consistent between executions, if a series is updated
            // and an episode is removed, but later comes back with a different ID then Sonarr will treat it as incomplete.
            // Since imports should be relatively fast and these types of data changes are infrequent this should be quite
            // safe, but commenting for future benefit.

            var atLeastOneEpisodeImported = importResults.Any(c => c.Result == ImportResultType.Imported);
            var allEpisodesImportedInHistory = _trackedDownloadAlreadyImported.IsImported(trackedDownload, historyItems);

            if (allEpisodesImportedInHistory)
            {
                // Log different error messages depending on the circumstances, but treat both as fully imported, because that's the reality.
                // The second message shouldn't be logged in most cases, but continued reporting would indicate an ongoing issue.

                if (atLeastOneEpisodeImported)
                {
                    _logger.Debug("All episodes were imported in history for {0}", trackedDownload.DownloadItem.Title);
                }
                else
                {
                    _logger.ForDebugEvent()
                           .Message("No Episodes were just imported, but all episodes were previously imported, possible issue with download history.")
                           .Property("SeriesId", trackedDownload.RemoteEpisode.Series.Id)
                           .Property("DownloadId", trackedDownload.DownloadItem.DownloadId)
                           .Property("Title", trackedDownload.DownloadItem.Title)
                           .Property("Path", trackedDownload.ImportItem.OutputPath.ToString())
                           .WriteSentryWarn("DownloadHistoryIncomplete")
                           .Log();
                }

                var episodes = _episodeService.GetEpisodes(trackedDownload.RemoteEpisode.Episodes.Select(e => e.Id));
                var files = _mediaFileService.GetFiles(episodes.Select(e => e.EpisodeFileId).Where(i => i > 0).Distinct());

                trackedDownload.State = TrackedDownloadState.Imported;
                _eventAggregator.PublishEvent(new DownloadCompletedEvent(trackedDownload, trackedDownload.RemoteEpisode.Series.Id, files, releaseInfo));

                return true;
            }

            _logger.Debug("Not all episodes have been imported for the release '{0}'", trackedDownload.DownloadItem.Title);
            return false;
        }

        // Blocks without an import attempt (unmatched series, unparsable download) only resolve by manual interaction
        private void SetStateToImportBlocked(TrackedDownload trackedDownload, bool rejectedPermanently = true)
        {
            trackedDownload.State = TrackedDownloadState.ImportBlocked;
            trackedDownload.ImportRejectedPermanently = rejectedPermanently;

            if (!trackedDownload.HasNotifiedManualInteractionRequired)
            {
                var grabbedHistories = _historyService.FindByDownloadId(trackedDownload.DownloadItem.DownloadId).Where(h => h.EventType == EpisodeHistoryEventType.Grabbed).ToList();

                trackedDownload.HasNotifiedManualInteractionRequired = true;

                var releaseInfo = grabbedHistories.Count > 0 ? new GrabbedReleaseInfo(grabbedHistories) : null;
                var manualInteractionEvent = new ManualInteractionRequiredEvent(trackedDownload, releaseInfo);

                _eventAggregator.PublishEvent(manualInteractionEvent);
            }
        }

        private void SetImportItem(TrackedDownload trackedDownload)
        {
            trackedDownload.ImportItem = _provideImportItemService.ProvideImportItem(trackedDownload.DownloadItem, trackedDownload.ImportItem);
        }

        private bool ValidatePath(TrackedDownload trackedDownload)
        {
            var downloadItemOutputPath = trackedDownload.ImportItem.OutputPath;

            if (downloadItemOutputPath.IsEmpty)
            {
                trackedDownload.Warn("Download doesn't contain intermediate path, Skipping.");
                return false;
            }

            if ((OsInfo.IsWindows && !downloadItemOutputPath.IsWindowsPath) ||
                (OsInfo.IsNotWindows && !downloadItemOutputPath.IsUnixPath))
            {
                trackedDownload.Warn("[{0}] is not a valid local path. You may need a Remote Path Mapping. Check the download troubleshooting entry on the wiki for details.", downloadItemOutputPath);
                return false;
            }

            return true;
        }
    }
}
