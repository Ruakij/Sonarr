using NzbDrone.Core.Configuration;
using Sonarr.Http.REST;

namespace Sonarr.Api.V3.Config
{
    public class DownloadClientConfigResource : RestResource
    {
        public string DownloadClientWorkingFolders { get; set; }

        public bool EnableCompletedDownloadHandling { get; set; }
        public int MinimumTitleSimilarity { get; set; }
        public bool AutoRedownloadFailed { get; set; }
        public bool AutoRedownloadFailedFromInteractiveSearch { get; set; }
        public int? ManualImportTimeout { get; set; }
    }

    public static class DownloadClientConfigResourceMapper
    {
        public static DownloadClientConfigResource ToResource(IConfigService model)
        {
            return new DownloadClientConfigResource
            {
                DownloadClientWorkingFolders = model.DownloadClientWorkingFolders,

                EnableCompletedDownloadHandling = model.EnableCompletedDownloadHandling,
                MinimumTitleSimilarity = model.MinimumTitleSimilarity,
                AutoRedownloadFailed = model.AutoRedownloadFailed,
                AutoRedownloadFailedFromInteractiveSearch = model.AutoRedownloadFailedFromInteractiveSearch,
                ManualImportTimeout = model.ManualImportTimeout < 0 ? null : model.ManualImportTimeout
            };
        }
    }
}
