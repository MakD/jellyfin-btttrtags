using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.BtttrTags.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BtttrTags.Common;

public class TagResult
{
    public required string Text { get; set; }
    public bool IsGold { get; set; }
}

public class TagResolver
{
    private static readonly RankedList _imdbTop250Movies = new(
        "imdb-top-250-movies",
        "https://mdblist.com/lists/snoak/imdb-top-250-movies/json",
        TimeSpan.FromDays(7),
        200,
        ParseMdbList);

    private static readonly RankedList _imdbTop250Shows = new(
        "imdb-top-250-shows",
        "https://mdblist.com/lists/snoak/imdb-top-250-shows/json",
        TimeSpan.FromDays(7),
        200,
        ParseMdbList);

    private static readonly RankedList _trendingMovies = new(
        "trending-movies",
        "https://cinemeta-catalogs.strem.io/top/catalog/movie/top.json",
        TimeSpan.FromHours(12),
        10,
        ParseCinemeta);

    private static readonly RankedList _trendingSeries = new(
        "trending-series",
        "https://cinemeta-catalogs.strem.io/top/catalog/series/top.json",
        TimeSpan.FromHours(12),
        10,
        ParseCinemeta);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly string _cacheDirectory;

    public TagResolver(IHttpClientFactory httpClientFactory, ILogger logger, string cacheDirectory)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _cacheDirectory = cacheDirectory;
    }

    public async Task<bool> InitializeDataAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        bool available = true;

        if (config.EnableImdbTop250)
        {
            available &= await RefreshAsync(_imdbTop250Movies, client, cancellationToken).ConfigureAwait(false);
            available &= await RefreshAsync(_imdbTop250Shows, client, cancellationToken).ConfigureAwait(false);
        }

        if (config.EnableTrending)
        {
            available &= await RefreshAsync(_trendingMovies, client, cancellationToken).ConfigureAwait(false);
            available &= await RefreshAsync(_trendingSeries, client, cancellationToken).ConfigureAwait(false);
        }

        return available;
    }

    private async Task<bool> RefreshAsync(RankedList list, HttpClient client, CancellationToken cancellationToken)
    {
        if (list.Ranks.Count == 0)
        {
            LoadSavedCopy(list);
        }

        if (DateTime.UtcNow - list.LastFetch <= list.MaxAge)
        {
            return true;
        }

        try
        {
            var json = await client.GetStringAsync(list.Url, cancellationToken).ConfigureAwait(false);
            var ranks = list.Parse(json);
            if (ranks.Count < list.MinEntries)
            {
                throw new InvalidDataException($"Expected at least {list.MinEntries} entries but found {ranks.Count}.");
            }

            list.Ranks = ranks;
            list.LastFetch = DateTime.UtcNow;
            SaveCopy(list);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BtttrTags: Failed to update the {List} list. Keeping {Count} entries from {Date}.", list.Name, list.Ranks.Count, list.LastFetch);
        }

        return list.Ranks.Count > 0;
    }

    private void LoadSavedCopy(RankedList list)
    {
        string path = Path.Combine(_cacheDirectory, $"{list.Name}.json");
        if (!File.Exists(path)) return;

        try
        {
            var saved = JsonSerializer.Deserialize<SavedList>(File.ReadAllText(path));
            if (saved?.Ranks != null && saved.Ranks.Count > 0)
            {
                list.Ranks = saved.Ranks;
                list.LastFetch = saved.SavedAtUtc;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BtttrTags: Could not read the saved {List} list.", list.Name);
        }
    }

    private void SaveCopy(RankedList list)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            string path = Path.Combine(_cacheDirectory, $"{list.Name}.json");
            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(new SavedList(list.LastFetch, list.Ranks)));
            File.Move(tempPath, path, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BtttrTags: Could not save the {List} list.", list.Name);
        }
    }

    private static Dictionary<string, int> ParseMdbList(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var entries = new List<(int Rank, string ImdbId)>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (el.TryGetProperty("imdb_id", out var idProp) && idProp.ValueKind == JsonValueKind.String &&
                el.TryGetProperty("rank", out var rankProp) && rankProp.TryGetInt32(out int rank))
            {
                entries.Add((rank, idProp.GetString()!));
            }
        }

        var ranks = new Dictionary<string, int>();
        int position = 1;
        foreach (var entry in entries.OrderBy(e => e.Rank))
        {
            ranks.TryAdd(entry.ImdbId, position);
            position++;
        }
        return ranks;
    }

    private static Dictionary<string, int> ParseCinemeta(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var ranks = new Dictionary<string, int>();
        int rank = 1;
        foreach (var m in doc.RootElement.GetProperty("metas").EnumerateArray())
        {
            if (m.TryGetProperty("id", out var idProp))
            {
                var id = idProp.GetString();
                if (!string.IsNullOrEmpty(id))
                {
                    ranks.TryAdd(id, rank);
                }
            }
            rank++;
        }
        return ranks;
    }

    public TagResult? GetTagForItem(BaseItem item, PluginConfiguration config)
    {
        if (item is not Movie && item is not Series) return null;

        var imdbId = item.GetProviderId(MetadataProvider.Imdb);

        var imdbTop250 = item is Series ? _imdbTop250Shows : _imdbTop250Movies;
        if (config.EnableImdbTop250 && !string.IsNullOrEmpty(imdbId) && imdbTop250.Ranks.TryGetValue(imdbId, out int imdbRank))
        {
            return new TagResult { Text = $"IMDb #{imdbRank}", IsGold = true };
        }

        var trending = item is Series ? _trendingSeries : _trendingMovies;
        if (config.EnableTrending && !string.IsNullOrEmpty(imdbId) && trending.Ranks.TryGetValue(imdbId, out int trendRank))
        {
            return new TagResult { Text = trendRank <= 25 ? $"#{trendRank} Today" : "Trending", IsGold = false };
        }

        var now = DateTime.UtcNow;

        if (config.EnableNewMovies)
        {
            bool isNew = false;
            if (item.PremiereDate.HasValue)
            {
                var diff = (now - item.PremiereDate.Value).TotalDays;
                if (diff >= -30 && diff <= config.NewMovieDaysThreshold)
                {
                    isNew = true;
                }
            }
            else if (item.ProductionYear.HasValue && item.ProductionYear.Value == now.Year)
            {
                if (item.DateCreated != DateTime.MinValue && (now - item.DateCreated).TotalDays <= config.NewMovieDaysThreshold)
                {
                    isNew = true;
                }
            }

            if (isNew)
            {
                return new TagResult { Text = item is Series ? "New Series" : "New Movie", IsGold = false };
            }
        }

        if (config.EnableJustAdded)
        {
            if (item.DateCreated != DateTime.MinValue)
            {
                var diff = (now - item.DateCreated).TotalDays;
                if (diff >= 0 && diff <= config.JustAddedDaysThreshold)
                {
                    return new TagResult { Text = "Just Added", IsGold = false };
                }
            }
        }

        if (config.EnableTvSeriesStatus && item is Series series)
        {
            var statusStr = series.Status?.ToString() ?? "";
            bool isContinuing = series.Status == SeriesStatus.Continuing
                || statusStr.Contains("Continuing", StringComparison.OrdinalIgnoreCase);

            if (isContinuing)
            {
                return new TagResult { Text = "Returning Series", IsGold = false };
            }

            bool isEnded = series.Status == SeriesStatus.Ended
                || series.EndDate.HasValue
                || statusStr.Contains("Ended", StringComparison.OrdinalIgnoreCase);

            if (isEnded)
            {
                return new TagResult { Text = "Binge Ready", IsGold = false };
            }
        }

        return null;
    }

    private sealed record SavedList(DateTime SavedAtUtc, Dictionary<string, int> Ranks);

    private sealed class RankedList
    {
        public RankedList(string name, string url, TimeSpan maxAge, int minEntries, Func<string, Dictionary<string, int>> parse)
        {
            Name = name;
            Url = url;
            MaxAge = maxAge;
            MinEntries = minEntries;
            Parse = parse;
        }

        public string Name { get; }
        public string Url { get; }
        public TimeSpan MaxAge { get; }
        public int MinEntries { get; }
        public Func<string, Dictionary<string, int>> Parse { get; }
        public Dictionary<string, int> Ranks { get; set; } = new();
        public DateTime LastFetch { get; set; } = DateTime.MinValue;
    }
}
