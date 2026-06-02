using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Services;

namespace StrmCompanion.Api
{
    [Route("/strmcompanion/series", "GET", Summary = "List all TV series in library")]
    [Authenticated]
    public class GetSeriesList : IReturn<List<SeriesDto>> { }

    [Route("/strmcompanion/series/{SeriesId}/seasons", "GET", Summary = "List seasons for a series")]
    [Authenticated]
    public class GetSeasonList : IReturn<List<SeasonDto>>
    {
        public long SeriesId { get; set; }
    }

    public class SeriesDto
    {
        public long Id { get; set; }
        public string Name { get; set; }
    }

    public class SeasonDto
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public int? SeasonNumber { get; set; }
    }

    public class LibraryService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;

        public LibraryService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public object Get(GetSeriesList request)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Series" },
                IsVirtualItem = false,
                OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) }
            };

            var result = _libraryManager.QueryItems(query);
            return result.Items.Select(s => new SeriesDto
            {
                Id = s.InternalId,
                Name = s.Name
            }).ToList();
        }

        public object Get(GetSeasonList request)
        {
            var series = _libraryManager.GetItemById(request.SeriesId);
            if (series == null)
                return new List<SeasonDto>();

            var query = new InternalItemsQuery
            {
                AncestorIds = new[] { series.InternalId },
                IncludeItemTypes = new[] { "Season" },
                IsVirtualItem = false,
                OrderBy = new[] { (ItemSortBy.IndexNumber, SortOrder.Ascending) }
            };

            var result = _libraryManager.GetItemsResult(query);
            return result.Items.OfType<Season>().Select(s => new SeasonDto
            {
                Id = s.InternalId,
                Name = s.Name,
                SeasonNumber = s.IndexNumber
            }).ToList();
        }
    }
}
