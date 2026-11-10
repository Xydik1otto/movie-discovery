using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;

namespace MovieDiscovery.Api.Controllers;

/// <summary>Збережені добірки (FR-21). Усі дії, крім пресетів, — лише для залогінених (401 без логіну);
/// власник добірки ТІЛЬКИ з cookie-юзера (ізоляція як у TasteProfileService) — тіло запиту не довіряється.</summary>
[ApiController]
[Route("api/collections")]
[Authorize]
public sealed class CollectionsController(CollectionsService collections, MovieDiscoveryDbContext db) : ControllerBase
{
    public sealed class CreateCollectionRequest
    {
        public string? Name { get; set; }
        public List<long> Items { get; set; } = [];
    }

    /// <summary>Добірка у компакт-формі: items — TitleCard у порядку ord.</summary>
    public sealed record CollectionItemResponse(int Id, string Name, DateTime CreatedAt, IReadOnlyList<TitleCard> Items);

    /// <summary>Список своїх добірок: {collections:[…]}.</summary>
    public sealed record CollectionsListResponse(IReadOnlyList<CollectionItemResponse> Collections);

    /// <summary>Пресет: ключ локалізації (preset.mood/preset.evening/preset.duration — фронт локалізує) + тайтли.</summary>
    public sealed record CollectionPresetResponse(string Key, IReadOnlyList<TitleCard> Items);

    /// <summary>Список пресетів: {presets:[…]}.</summary>
    public sealed record PresetsResponse(IReadOnlyList<CollectionPresetResponse> Presets);

    /// <summary>Створена добірка: 201 → {id, name, createdAt, itemIds}.</summary>
    public sealed record CollectionCreatedResponse(int Id, string Name, DateTime CreatedAt, IReadOnlyList<long> ItemIds);

    /// <summary>Створити добірку: {name, items:[titleId…]} — до 30 унікальних тайтлів, усі мають існувати.
    /// 201 → {id, name, createdAt, itemIds}; 400 invalid_name/too_many_items/unknown_titles; 401 без логіну.</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCollectionRequest req, CancellationToken ct)
    {
        if (!TryUserId(out var userId))
            return Unauthorized(new { error = "unauthorized", message = "Потрібен вхід" });

        var (created, error, unknown) = await collections.CreateAsync(userId, req.Name, req.Items, ct);
        if (created is null)
            return error switch
            {
                "unknown_titles" => BadRequest(new
                {
                    error,
                    message = "Деякі тайтли не знайдено",
                    unknownTitleIds = unknown,
                }),
                "too_many_items" => BadRequest(new
                {
                    error,
                    message = $"Добірка може містити щонайбільше {CollectionsService.MaxItems} тайтлів",
                }),
                _ => BadRequest(new
                {
                    error = error ?? "invalid_name",
                    message = $"name має бути непорожнім рядком до {CollectionsService.MaxNameLength} символів",
                }),
            };

        return Created($"/api/collections/{created.Id}", new CollectionCreatedResponse(
            created.Id,
            created.Name,
            created.CreatedAt,
            created.Items.OrderBy(i => i.Ord).Select(i => i.TitleId).ToList()));
    }

    /// <summary>Свої добірки з items у компакт-формі (TitleCard, порядок ord). 401 без логіну.</summary>
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? lang, CancellationToken ct)
    {
        if (!TryUserId(out var userId))
            return Unauthorized(new { error = "unauthorized", message = "Потрібен вхід" });

        var langSafe = SearchController.Lang(lang);
        var cols = await collections.GetOwnAsync(userId, ct);

        var titleIds = cols.SelectMany(c => c.Items).Select(i => (int)i.TitleId).Distinct().ToList();
        var titles = titleIds.Count == 0
            ? new Dictionary<int, Core.Domain.Title>()
            : await db.Titles.AsNoTracking()
                .Where(t => titleIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, ct);

        var response = cols.Select(c => new CollectionItemResponse(
            c.Id,
            c.Name,
            c.CreatedAt,
            // відсутні тайтли (видалені з каталогу — FK на titles нема) компакт-форма пропускає
            c.Items
                .Where(i => titles.ContainsKey((int)i.TitleId))
                .Select(i => TitleMapper.ToCard(titles[(int)i.TitleId], langSafe))
                .ToList())).ToList();
        return Ok(new CollectionsListResponse(response));
    }

    /// <summary>Видалити свою добірку. 204; чужа/неіснуюча → 404 (без розрізнення).</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        if (!TryUserId(out var userId))
            return Unauthorized(new { error = "unauthorized", message = "Потрібен вхід" });

        var deleted = await collections.DeleteAsync(userId, id, ct);
        return deleted
            ? NoContent()
            : NotFound(new { error = "not_found", message = "Добірку не знайдено" });
    }

    /// <summary>Серверні пресети добірок (FR-21, анонімно ОК): mood/evening/duration — по 12 тайтлів,
    /// пріоритет тайтлам з watch_links. Ключі preset.mood/preset.evening/preset.duration НЕ локалізуються сервером.</summary>
    [HttpGet("presets")]
    [AllowAnonymous]
    public async Task<IActionResult> Presets([FromQuery] string? lang, CancellationToken ct)
    {
        var langSafe = SearchController.Lang(lang);
        var presets = await collections.GetPresetsAsync(ct);
        return Ok(new PresetsResponse(presets.Select(p => new CollectionPresetResponse(
            p.Key,
            p.Items.Select(t => TitleMapper.ToCard(t, langSafe)).ToList())).ToList()));
    }

    private bool TryUserId(out string userId)
    {
        userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
        return User.Identity?.IsAuthenticated == true && userId.Length > 0;
    }
}
