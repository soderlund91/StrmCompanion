using System;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using StrmCompanion.Analysis;
using StrmCompanion.IntroDetection;
using StrmCompanion.Jobs;
using StrmCompanion.MediaInfo;
using StrmCompanion.MergeVersion;

namespace StrmCompanion
{
    public class PluginEntryPoint : IServerEntryPoint
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly ILogger _logger;
        private readonly ILogManager _logManager;

        private MediaInfoAnalysisTask _mediaInfoTask;

        public static JobManager JobManager { get; private set; }
        public static AnalysisTaskRegistry TaskRegistry { get; private set; }
        public static IItemRepository ItemRepository { get; private set; }
        public static MergeVersionService MergeVersionService { get; private set; }

        public PluginEntryPoint(
            ILogManager logManager,
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            IJsonSerializer jsonSerializer)
        {
            _logManager = logManager;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _jsonSerializer = jsonSerializer;
            _logger = logManager.GetLogger(nameof(StrmCompanion));
        }

        public void Run()
        {
            _logger.Info("StrmCompanion v{0} starting", Plugin.Instance?.Version);

            JobManager = new JobManager(_logger);
            TaskRegistry = new AnalysisTaskRegistry();
            ItemRepository = _itemRepository;

            MergeVersionService = new MergeVersionService(
                _libraryManager, JobManager, _logManager);

            var introTask = new IntroDetectionAnalysisTask(
                _libraryManager,
                _itemRepository,
                _jsonSerializer,
                JobManager,
                _logManager);

            TaskRegistry.Register(introTask);

            _mediaInfoTask = new MediaInfoAnalysisTask(
                _libraryManager,
                _itemRepository,
                JobManager,
                _logManager);

            TaskRegistry.Register(_mediaInfoTask);

            if (Plugin.Instance?.Configuration?.MediaInfoAutoScan == true)
                _libraryManager.ItemAdded += OnItemAdded;

            _logger.Info("StrmCompanion: {0} analysis task(s) registered", TaskRegistry.GetAll().Count);
        }

        private void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            try
            {
                var item = e.Item;
                if (item == null) return;
                if (!(item.Path?.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) ?? false)) return;
                if (!(item is MediaBrowser.Controller.Entities.Movies.Movie ||
                      item is MediaBrowser.Controller.Entities.TV.Episode)) return;

                var cfg = Plugin.Instance?.Configuration;
                if (cfg == null || !cfg.MediaInfoAutoScan) return;

                var configuredIds = MediaInfoAnalysisTask.ParseLibraryIds(cfg.MediaInfoLibraryIds);
                if (configuredIds.Length == 0) return;

                var folders = _libraryManager.GetCollectionFolders(item);
                foreach (var folder in folders)
                {
                    foreach (var id in configuredIds)
                    {
                        if (folder.InternalId == id)
                        {
                            _mediaInfoTask.StartSingleItemScan(item.InternalId, System.Threading.CancellationToken.None);
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("StrmCompanion: auto-scan error for new item: {0}", ex.Message);
            }
        }

        public void Dispose()
        {
            _logger.Info("StrmCompanion shutting down");
            _libraryManager.ItemAdded -= OnItemAdded;
            JobManager?.CancelAll();
        }
    }
}
