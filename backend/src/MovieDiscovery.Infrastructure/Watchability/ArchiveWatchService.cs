using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Infrastructure.Watchability;

/// <summary>Документ advancedsearch Internet Archive.</summary>
public sealed record IaDoc(
    [property: JsonPropertyName("identifier")] string Identifier,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("year")] int? Year);

public sealed record IaSearchResponse(
    [property: JsonPropertyName("response")] IaSearchBody? Response);

public sealed record IaSearchBody(
    [property: JsonPropertyName("numFound")] int NumFound,
    [property: JsonPropertyName("docs")] List<IaDoc>? Docs);

/// <summary>Результат sync archive.org.</summary>
public sealed record ArchiveSyncResult(int Docs, int Matched, int Links);

/// <summary>
/// Internet Archive — free-провайдер (FR-14): advancedsearch `collection:(feature_films)`
/// пагінацією (500/стор), матчинг до titles за нормалізованою назвою + рік ±1.
/// Матч → watch_links(provider=archive, kind=free, region=*); тайтли НЕ створюються.
/// </summary>
public sealed partial class ArchiveWatchService(
    HttpClient http,
    MovieDiscoveryDbContext db,
    ILogger<ArchiveWatchService> log)
{
    public const string Source = "archive";
    public const string ProviderName = "archive";
    public const string Region = "*";
    public const string Kind = "free";
    public const int RowsPerPage = 500;

    [GeneratedRegex("\\((?<y>\\d{4})\\)\\s*$")]
    private static partial Regex TrailingYear();

    private const string SearchPath =
        "advancedsearch.php?q=collection%3A%28feature_films%29" +
        "&fl%5B%5D=identifier&fl%5B%5D=title&fl%5B%5D=year" +
        "&rows=500&page={page}&output=json";

    public async Task<ArchiveSyncResult> SyncAsync(int pages, CancellationToken ct = default)
    {
        var docs = new List<IaDoc>();
        for (var page = 1; page <= Math.Max(1, pages); page++)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await FetchPageAsync(page, ct);
            if (batch.Count == 0) break;
            docs.AddRange(batch);
        }
        var split = docs
            .Where(d => !string.IsNullOrWhiteSpace(d.Identifier) && !string.IsNullOrWhiteSpace(d.Title))
            .Select(d => (Doc: d, Parsed: SplitTitleYear(d)))
            .Where(x => x.Parsed.Title.Length > 0 && x.Parsed.Year is not null) // без року матч неможливий (±1)
            .ToList();

        var candidates = await LoadCandidatesAsync(split.Select(x => x.Parsed.Year).ToList(), ct);
        var byName = IndexCandidates(candidates);

        var existingLinks = await db.WatchLinks
            .Where(w => w.Provider == ProviderName)
            .ToListAsync(ct);

        var matched = 0;
        var links = 0;
        foreach (var (doc, parsed) in split)
        {
            var found = TitleMatcher.Find(byName.Lookup(TitleNormalizer.Normalize(parsed.Title)).ToList(), parsed.Title, null, parsed.Year);
            if (found is null) continue;
            matched++;

            var link = new WatchLink
            {
                TitleId = found.Id,
                Provider = ProviderName,
                Kind = Kind,
                Region = Region,
                Url = $"https://archive.org/details/{doc.Identifier}",
                IsAvailable = true,
                CheckedAt = DateTime.UtcNow,
            };
            var (resolved, isNew) = WatchLinkUpsertCore.Resolve(
                existingLinks.Where(w => w.TitleId == found.Id), link);
            if (isNew)
            {
                db.WatchLinks.Add(resolved);
                existingLinks.Add(resolved);
            }
            else WatchLinkUpsertCore.Merge(resolved, link);
            links++;
        }

        await db.SaveChangesAsync(ct);
        await UpdateSyncStateAsync(links, ct);
        log.LogInformation("archive.org sync: документів={Docs}, матчів={Matched}, посилань={Links}",
            docs.Count, matched, links);
        return new ArchiveSyncResult(docs.Count, matched, links);
    }

    /// <summary>«Ніч of the Living Dead (1968)» → («Night of the Living Dead», 1968).</summary>
    public static (string Title, int? Year) SplitTitleYear(IaDoc doc)
    {
        var m = TrailingYear().Match(doc.Title?.Trim() ?? "");
        if (!m.Success) return (doc.Title?.Trim() ?? "", doc.Year);
        var year = int.Parse(m.Groups["y"].Value);
        return (doc.Title!.Trim()[..m.Index].Trim(), doc.Year ?? year);
    }

    private async Task<List<IaDoc>> FetchPageAsync(int page, CancellationToken ct)
    {
        var url = SearchPath.Replace("{page}", page.ToString(), StringComparison.Ordinal);
        using var resp = await http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            log.LogWarning("archive.org {Status} на сторінці {Page} — пропущено", (int)resp.StatusCode, page);
            return [];
        }
        var json = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            var parsed = JsonSerializer.Deserialize<IaSearchResponse>(json);
            return parsed?.Response?.Docs ?? [];
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "archive.org: не вдалося розібрати відповідь (сторінка {Page})", page);
            return [];
        }
    }

    /// <summary>Кандидати: movie у вікні років документів ±1 (індекс titles(year)), одним запитом на чанк.</summary>
    private async Task<List<Title>> LoadCandidatesAsync(List<int?> years, CancellationToken ct)
    {
        var yearSet = new HashSet<int>();
        foreach (var y in years)
            if (y is { } v)
            {
                yearSet.Add(v - 1);
                yearSet.Add(v);
                yearSet.Add(v + 1);
            }
        var candidates = new List<Title>();
        foreach (var chunk in yearSet.Chunk(500))
        {
            var set = chunk.ToHashSet();
            candidates.AddRange(await db.Titles.AsNoTracking()
                .Where(t => t.Type == "movie" && t.Year != null && set.Contains(t.Year.Value))
                .ToListAsync(ct));
        }
        return candidates;
    }

    private static CandidateIndex IndexCandidates(List<Title> candidates)
    {
        var index = new CandidateIndex();
        foreach (var c in candidates)
        {
            var en = TitleNormalizer.Normalize(c.TitleEn);
            if (en.Length > 0) index.Add(en, c);
            var uk = TitleNormalizer.Normalize(c.TitleUk);
            if (uk.Length > 0 && uk != en) index.Add(uk, c);
        }
        return index;
    }

    /// <summary>Індекс кандидатів за нормалізованою назвою (одна назва → кілька тайтлів).</summary>
    private sealed class CandidateIndex
    {
        private readonly Dictionary<string, List<Title>> _map = new(StringComparer.Ordinal);

        public void Add(string normalized, Title title)
        {
            if (!_map.TryGetValue(normalized, out var list)) _map[normalized] = list = [];
            list.Add(title);
        }

        public IEnumerable<Title> Lookup(string normalized) =>
            _map.TryGetValue(normalized, out var list) ? list : [];
    }

    private async Task UpdateSyncStateAsync(int links, CancellationToken ct)
    {
        var state = await db.SyncStates.FirstOrDefaultAsync(s => s.Source == Source && s.Kind == "feature_films", ct);
        if (state is null)
        {
            state = new SyncState { Source = Source, Kind = "feature_films" };
            db.SyncStates.Add(state);
        }
        state.LastRunAt = DateTime.UtcNow;
        state.LastCount = links;
        await db.SaveChangesAsync(ct);
    }
}
