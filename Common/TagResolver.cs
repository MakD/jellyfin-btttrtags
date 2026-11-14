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

    private static readonly AwardList[] _awards =
    {
        new("oscars", "Oscar Winner", "Oscar Nominee", 1000, c => c.EnableOscarWinner, c => c.EnableOscarNominee,
            "Q102427", "Q105304", "Q106800", "Q111332",
            "Q917808", "Q1425151", "Q1324407", "Q22335251", "Q1401974", "Q3886464", "Q3886465", "Q2297175",
            "Q131520", "Q21995139", "Q21995136", "Q281939", "Q393686", "Q22917729",
            "Q277751", "Q22253133", "Q22253131", "Q277536", "Q22120066", "Q22120095", "Q487136",
            "Q830079", "Q488645", "Q488651", "Q22752868", "Q4671338", "Q22752811", "Q22235329",
            "Q22344608", "Q22235305", "Q22752734", "Q112243",
            "Q20052644", "Q3298549"),

        new("emmys", "Emmy Winner", "Emmy Nominee", 100, c => c.EnableEmmyWinner, c => c.EnableEmmyNominee,
            "Q989438", "Q2110156", "Q20714679", "Q13423511", "Q7243506",
            "Q337926", "Q3403236", "Q16987659", "Q7243514", "Q7243512", "Q18153303", "Q18153300",
            "Q7243517", "Q7243516", "Q25021464", "Q25021475", "Q17010308", "Q28445759",
            "Q7243507", "Q7243508", "Q17009259", "Q7243509", "Q7243505", "Q7243495", "Q7243497", "Q7243496",
            "Q7243504", "Q7243503",
            "Q17986500", "Q7243502", "Q7243501", "Q7243499", "Q7243500", "Q7243510", "Q7243511", "Q7243513",
            "Q17010286", "Q17985610", "Q17985609", "Q16254850", "Q16254853", "Q24902182", "Q25352357", "Q17092538"),

        new("palme-dor", "Palme d'Or", "Cannes Nominee", 50, c => c.EnablePalmeDor, c => c.EnableCannesNominee,
            "Q179808"),

        new("golden-lion", "Golden Lion", "Venice Nominee", 50, c => c.EnableGoldenLion, c => c.EnableVeniceNominee,
            "Q209459"),

        new("golden-bear", "Golden Bear", "Berlin Nominee", 50, c => c.EnableGoldenBear, c => c.EnableBerlinNominee,
            "Q154590"),

        new("baftas", "BAFTA Winner", "BAFTA Nominee", 150, c => c.EnableBaftaWinner, c => c.EnableBaftaNominee,
            "Q139184", "Q2663714", "Q2925687", "Q511553", "Q141142497", "Q240201", "Q115325414", "Q133252175",
            "Q338757", "Q4834542",
            "Q739633", "Q787127", "Q787145", "Q787098", "Q918617", "Q778870", "Q787104", "Q508166",
            "Q2925698", "Q2925696", "Q4969374", "Q16985329"),

        new("golden-globes", "Globe Winner", "Globe Nominee", 150, c => c.EnableGlobeWinner, c => c.EnableGlobeNominee,
            "Q1011509", "Q670282", "Q3110063", "Q387380", "Q2856444", "Q3946618", "Q3772959",
            "Q878902", "Q1422140", "Q1472235",
            "Q1255198", "Q596294", "Q265435"),

        new("national-film-awards", "National Award", null, 80, c => c.EnableNationalAward, null,
            "Q1435633", "Q516771", "Q1967210", "Q1494211", "Q1661436", "Q488042", "Q429290", "Q1967186",
            "Q1967157", "Q1398170",
            "Q1967162", "Q1798650", "Q1716697", "Q1720787", "Q1967179", "Q1967170", "Q1967189", "Q1967176",
            "Q251371", "Q1334229", "Q1967165",
            "Q1967131", "Q283473", "Q143430", "Q1967148", "Q1967211", "Q1967129", "Q933084"),

        new("filmfare", "Filmfare Winner", "Filmfare Nominee", 80, c => c.EnableFilmfareWinner, c => c.EnableFilmfareNominee,
            "Q1414525", "Q5449085", "Q5449087", "Q5449091", "Q1414573",
            "Q1414489", "Q5449068", "Q1414560", "Q1414545", "Q1414485", "Q578116", "Q5449100", "Q5449101", "Q5449099",
            "Q1414470", "Q1414473", "Q1414508", "Q1414550", "Q1414511"),
    };

    private static readonly string _userAgent =
        $"Jellyfin-BtttrTags/{typeof(TagResolver).Assembly.GetName().Version} (+https://github.com/MakD/jellyfin-btttrtags)";

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

        if (config.EnableTrendingRank || config.EnableTrending)
        {
            available &= await RefreshAsync(_trendingMovies, client, cancellationToken).ConfigureAwait(false);
            available &= await RefreshAsync(_trendingSeries, client, cancellationToken).ConfigureAwait(false);
        }

        foreach (var award in _awards)
        {
            if (award.WinnerEnabled(config) || award.NomineeEnabled?.Invoke(config) == true)
            {
                available &= await RefreshAsync(award.List, client, cancellationToken).ConfigureAwait(false);
            }
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
            using var request = new HttpRequestMessage(HttpMethod.Get, list.Url);
            request.Headers.UserAgent.ParseAdd(_userAgent);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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

    private static Dictionary<string, int> ParseWikidataAwards(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var results = new Dictionary<string, int>();
        foreach (var row in doc.RootElement.GetProperty("results").GetProperty("bindings").EnumerateArray())
        {
            if (!row.TryGetProperty("imdb", out var imdbProp) || !imdbProp.TryGetProperty("value", out var imdbValue))
            {
                continue;
            }

            var imdbId = imdbValue.GetString();
            if (string.IsNullOrEmpty(imdbId) || !imdbId.StartsWith("tt", StringComparison.Ordinal))
            {
                continue;
            }

            bool won = row.TryGetProperty("won", out var wonProp)
                && wonProp.TryGetProperty("value", out var wonValue)
                && string.Equals(wonValue.GetString(), "true", StringComparison.OrdinalIgnoreCase);

            int status = won ? AwardList.Won : AwardList.Nominated;
            if (!results.TryGetValue(imdbId, out int existing) || status > existing)
            {
                results[imdbId] = status;
            }
        }
        return results;
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

        var trending = item is Series ? _trendingSeries : _trendingMovies;
        if (!string.IsNullOrEmpty(imdbId) && trending.Ranks.TryGetValue(imdbId, out int trendRank))
        {
            if (trendRank <= 25 && config.EnableTrendingRank)
            {
                return new TagResult { Text = $"#{trendRank} Today", IsGold = false };
            }

            if (config.EnableTrending)
            {
                return new TagResult { Text = "Trending", IsGold = false };
            }
        }

        var now = DateTime.UtcNow;

        if (item is Series ? config.EnableNewSeries : config.EnableNewMovie)
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

        if (!string.IsNullOrEmpty(imdbId))
        {
            var imdbTop250 = item is Series ? _imdbTop250Shows : _imdbTop250Movies;
            if (config.EnableImdbTop250 && imdbTop250.Ranks.TryGetValue(imdbId, out int imdbRank))
            {
                return new TagResult { Text = $"IMDb #{imdbRank}", IsGold = true };
            }

            foreach (var award in _awards)
            {
                if (award.WinnerEnabled(config) && award.List.Ranks.TryGetValue(imdbId, out int status) && status == AwardList.Won)
                {
                    return new TagResult { Text = award.WinnerText, IsGold = true };
                }
            }

            foreach (var award in _awards)
            {
                if (award.NomineeText != null && award.NomineeEnabled?.Invoke(config) == true && award.List.Ranks.ContainsKey(imdbId))
                {
                    return new TagResult { Text = award.NomineeText, IsGold = false };
                }
            }
        }

        if (item is Series series && (config.EnableReturningSeries || config.EnableBingeReady))
        {
            var statusStr = series.Status?.ToString() ?? "";
            bool isContinuing = series.Status == SeriesStatus.Continuing
                || statusStr.Contains("Continuing", StringComparison.OrdinalIgnoreCase);

            if (isContinuing)
            {
                if (config.EnableReturningSeries)
                {
                    return new TagResult { Text = "Returning Series", IsGold = false };
                }
            }
            else
            {
                bool isEnded = series.Status == SeriesStatus.Ended
                    || series.EndDate.HasValue
                    || statusStr.Contains("Ended", StringComparison.OrdinalIgnoreCase);

                if (isEnded && config.EnableBingeReady)
                {
                    return new TagResult { Text = "Binge Ready", IsGold = false };
                }
            }
        }

        return null;
    }

    private sealed record SavedList(DateTime SavedAtUtc, Dictionary<string, int> Ranks);

    private sealed class AwardList
    {
        public const int Nominated = 1;
        public const int Won = 2;

        public AwardList(
            string key,
            string winnerText,
            string? nomineeText,
            int minEntries,
            Func<PluginConfiguration, bool> winnerEnabled,
            Func<PluginConfiguration, bool>? nomineeEnabled,
            params string[] categoryIds)
        {
            WinnerText = winnerText;
            NomineeText = nomineeText;
            WinnerEnabled = winnerEnabled;
            NomineeEnabled = nomineeEnabled;
            List = new RankedList($"awards-{key}", BuildQueryUrl(categoryIds), TimeSpan.FromDays(7), minEntries, ParseWikidataAwards);
        }

        public string WinnerText { get; }
        public string? NomineeText { get; }
        public Func<PluginConfiguration, bool> WinnerEnabled { get; }
        public Func<PluginConfiguration, bool>? NomineeEnabled { get; }
        public RankedList List { get; }

        private static string BuildQueryUrl(string[] categoryIds)
        {
            string values = string.Join(" ", categoryIds.Select(id => "wd:" + id));
            string query =
                "SELECT DISTINCT ?imdb ?won WHERE { "
                + "hint:Query hint:optimizer \"None\" . "
                + "VALUES ?award { " + values + " } "
                + "{ ?work wdt:P166 ?award . BIND(true AS ?won) } "
                + "UNION { ?work wdt:P1411 ?award . BIND(false AS ?won) } "
                + "UNION { ?st ps:P166 ?award ; pq:P1686 ?work . BIND(true AS ?won) } "
                + "UNION { ?st ps:P1411 ?award ; pq:P1686 ?work . BIND(false AS ?won) } "
                + "?work wdt:P345 ?imdb . "
                + "FILTER(STRSTARTS(?imdb, \"tt\")) }";
            return "https://query.wikidata.org/sparql?format=json&query=" + Uri.EscapeDataString(query);
        }
    }

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
