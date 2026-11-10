using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Abstractions;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Infrastructure.Embeddings;

/// <summary>
/// Фоновий споживач черги embeddings: батчами перераховує вектори тайтлів через Ollama.
/// Помилки не валать сервіс — тайтл просто залишається без вектора.
/// </summary>
public sealed class EmbeddingBackgroundService(
    IServiceScopeFactory scopeFactory,
    IEmbedder embedder,
    EmbeddingQueue queue,
    IOptions<OllamaOptions> ollamaOptions,
    ILogger<EmbeddingBackgroundService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("EmbeddingBackgroundService запущено (модель {Model})", ollamaOptions.Value.EmbedModel);
        await foreach (var titleId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessOneAsync(titleId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Помилка перерахунку вектора для title_id={Id}", titleId);
            }
        }
    }

    private async Task ProcessOneAsync(int titleId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        var title = await db.Titles.AsNoTracking().FirstOrDefaultAsync(t => t.Id == titleId, ct);
        if (title is null) return;

        var text = EmbeddingTextBuilder.Build(title);
        if (string.IsNullOrWhiteSpace(text)) return;

        var vec = await embedder.EmbedAsync(text, ct);
        if (vec is null) return; // залоговано в ембедері

        var modelName = ollamaOptions.Value.EmbedModel;
        var row = await db.Embeddings.FirstOrDefaultAsync(e => e.TitleId == titleId, ct);
        if (row is null)
        {
            db.Embeddings.Add(new EmbeddingRow
            {
                TitleId = titleId,
                Model = modelName,
                Dim = vec.Length,
                Vec = new Pgvector.Vector(vec),
                UpdatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            row.Model = modelName;
            row.Dim = vec.Length;
            row.Vec = new Pgvector.Vector(vec);
            row.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }
}
