namespace MovieDiscovery.Core.Abstractions;

/// <summary>
/// Ембедер (Ollama, модель bge-m3, 1024 dim). У разі недоступності сервісу або моделі
/// повертає null і логує — НЕ кидає винятків, застосунок працює далі (метадані-схожість).
/// </summary>
public interface IEmbedder
{
    /// <summary>Вектор тексту (очікується 1024 dim) або null, якщо ембедити не вдалося.</summary>
    Task<float[]?> EmbedAsync(string text, CancellationToken ct = default);

    /// <summary>Чи доступний сервіс ембедингів (для /api/health).</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}
