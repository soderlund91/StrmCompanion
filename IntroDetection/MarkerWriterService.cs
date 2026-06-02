using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace StrmCompanion.IntroDetection
{
    /// <summary>
    /// Writes IntroStart / IntroEnd markers into Emby's database via IItemRepository.
    /// Idempotent: removes any previously written intro markers before saving new ones.
    /// </summary>
    public class MarkerWriterService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly ILogger _logger;

        public MarkerWriterService(
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            ILogger logger)
        {
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _logger = logger;
        }

        /// <summary>
        /// Saves intro markers for an episode identified by its InternalId (long).
        /// IItemRepository.SaveChapters takes the InternalId (long), not the Guid.
        /// </summary>
        public void WriteIntroMarkers(long episodeInternalId, double startSeconds, double endSeconds)
        {
            var item = _libraryManager.GetItemById(episodeInternalId);
            if (item == null)
            {
                _logger.Warn("StrmCompanion: episode with InternalId {0} not found", episodeInternalId);
                return;
            }

            // Fetch existing chapters and strip old intro markers (idempotent)
            var existing = _itemRepository.GetChapters(item);
            var cleaned = existing
                .Where(c => c.MarkerType != MarkerType.IntroStart && c.MarkerType != MarkerType.IntroEnd)
                .ToList();

            cleaned.Add(new ChapterInfo
            {
                Name = "Intro",
                MarkerType = MarkerType.IntroStart,
                StartPositionTicks = TimeSpan.FromSeconds(startSeconds).Ticks
            });
            cleaned.Add(new ChapterInfo
            {
                Name = "After Intro",
                MarkerType = MarkerType.IntroEnd,
                StartPositionTicks = TimeSpan.FromSeconds(endSeconds).Ticks
            });

            // Emby requires chapters sorted by position
            cleaned.Sort((a, b) => a.StartPositionTicks.CompareTo(b.StartPositionTicks));

            // SaveChapters takes InternalId (long), not item.Id (Guid)
            _itemRepository.SaveChapters(item.InternalId, cleaned);

            _logger.Info("StrmCompanion: saved intro markers for '{0}' ({1:F1}s – {2:F1}s)",
                item.Name, startSeconds, endSeconds);
        }

        /// <summary>
        /// Returns true if the episode already has IntroStart or IntroEnd markers.
        /// </summary>
        public bool HasIntroMarkers(long episodeInternalId)
        {
            var item = _libraryManager.GetItemById(episodeInternalId);
            if (item == null) return false;

            var chapters = _itemRepository.GetChapters(item);
            return chapters.Any(c =>
                c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd);
        }

        /// <summary>
        /// Removes IntroStart and IntroEnd markers from an episode, leaving other chapters intact.
        /// Returns true if markers were present and removed.
        /// </summary>
        public bool DeleteIntroMarkers(long episodeInternalId)
        {
            var item = _libraryManager.GetItemById(episodeInternalId);
            if (item == null)
            {
                _logger.Warn("StrmCompanion: episode {0} not found for marker deletion", episodeInternalId);
                return false;
            }

            var existing = _itemRepository.GetChapters(item);
            var hadMarkers = existing.Any(c =>
                c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd);

            if (!hadMarkers)
                return false;

            var cleaned = existing
                .Where(c => c.MarkerType != MarkerType.IntroStart && c.MarkerType != MarkerType.IntroEnd)
                .ToList();

            _itemRepository.SaveChapters(item.InternalId, cleaned);
            _logger.Info("StrmCompanion: deleted intro markers for '{0}'", item.Name);
            return true;
        }
    }
}
