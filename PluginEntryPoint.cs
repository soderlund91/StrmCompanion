using System;
using System.Collections.Generic;
using System.Threading;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using StrmCompanion.Analysis;
using StrmCompanion.IntroDetection;
using StrmCompanion.Jobs;
using StrmCompanion.MergeVersion;
using StrmCompanion.ScheduledTasks;

namespace StrmCompanion
{
    public class PluginEntryPoint : IServerEntryPoint
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IProviderManager _providerManager;
        private readonly IDirectoryService _directoryService;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly ILogger _logger;
        private readonly ILogManager _logManager;

        private MediaInfoScheduledTask _mediaInfoTask;

        private readonly Dictionary<long, Timer> _mergeAutoTimers = new Dictionary<long, Timer>();
        private readonly object _mergeAutoTimerLock = new object();

        public static JobManager JobManager { get; private set; }
        public static AnalysisTaskRegistry TaskRegistry { get; private set; }
        public static IItemRepository ItemRepository { get; private set; }
        public static MergeVersionService MergeVersionService { get; private set; }
        public static MediaInfoScheduledTask MediaInfoTask { get; private set; }

        public PluginEntryPoint(
            ILogManager logManager,
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            IProviderManager providerManager,
            IDirectoryService directoryService,
            IJsonSerializer jsonSerializer)
        {
            _logManager = logManager;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _providerManager = providerManager;
            _directoryService = directoryService;
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
                _libraryManager, _providerManager, _directoryService, JobManager, _logManager);

            var introTask = new IntroDetectionAnalysisTask(
                _libraryManager,
                _itemRepository,
                _jsonSerializer,
                JobManager,
                _logManager);

            TaskRegistry.Register(introTask);

            _mediaInfoTask = new MediaInfoScheduledTask(
                _libraryManager,
                _providerManager,
                _directoryService,
                _logManager);

            MediaInfoTask = _mediaInfoTask;

            if (Plugin.Instance?.Configuration?.MediaInfoAutoScan == true)
                _libraryManager.ItemAdded += OnItemAdded;

            _libraryManager.ItemAdded += OnItemAddedForMerge;

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

                var configuredIds = MediaInfoScheduledTask.ParseLibraryIds(cfg.MediaInfoLibraryIds);
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

        private void OnItemAddedForMerge(object sender, ItemChangeEventArgs e)
        {
            try
            {
                var item = e.Item;
                if (item == null) return;

                var cfg = Plugin.Instance?.Configuration;
                if (cfg == null || !cfg.MergeAutoDetect) return;

                var isMovie   = item is Movie;
                var isEpisode = item is Episode;
                if (!isMovie && !isEpisode) return;

                var expectedType = isMovie ? "movies" : "tvshows";
                CollectionFolder targetLibrary = null;

                foreach (var folder in _libraryManager.GetCollectionFolders(item))
                {
                    var cf  = folder as CollectionFolder;
                    var icf = folder as ICollectionFolder;
                    if (cf != null && icf?.CollectionType == expectedType)
                    {
                        targetLibrary = cf;
                        break;
                    }
                }

                if (targetLibrary == null) return;

                var libraryId    = targetLibrary.InternalId;
                var capturedLib  = targetLibrary;
                var capturedIsEp = isEpisode;

                lock (_mergeAutoTimerLock)
                {
                    Timer existing;
                    if (_mergeAutoTimers.TryGetValue(libraryId, out existing))
                    {
                        existing.Dispose();
                        _mergeAutoTimers.Remove(libraryId);
                    }

                    _mergeAutoTimers[libraryId] = new Timer(_ =>
                    {
                        lock (_mergeAutoTimerLock)
                            _mergeAutoTimers.Remove(libraryId);

                        try
                        {
                            MergeVersionService?.StartMergeForLibrary(capturedLib, capturedIsEp, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn("StrmCompanion MergeVersion auto-detect: failed to start merge: {0}", ex.Message);
                        }
                    }, null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("StrmCompanion MergeVersion auto-detect: ItemAdded handler error: {0}", ex.Message);
            }
        }

        public void Dispose()
        {
            _logger.Info("StrmCompanion shutting down");
            _libraryManager.ItemAdded -= OnItemAdded;
            _libraryManager.ItemAdded -= OnItemAddedForMerge;

            lock (_mergeAutoTimerLock)
            {
                foreach (var t in _mergeAutoTimers.Values) t.Dispose();
                _mergeAutoTimers.Clear();
            }

            JobManager?.CancelAll();
        }
    }
}
