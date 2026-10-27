using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;

namespace StrmCompanion.MergeVersion
{
    // Shared refresh settings used after merging or splitting versions.
    internal static class RefreshOptionsFactory
    {
        public static MetadataRefreshOptions FullMetadataRefresh(ILogger logger, IFileSystem fileSystem) =>
            new MetadataRefreshOptions(new DirectoryService(logger, fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode    = MetadataRefreshMode.ValidationOnly,
                ReplaceAllImages    = false
            };
    }
}
