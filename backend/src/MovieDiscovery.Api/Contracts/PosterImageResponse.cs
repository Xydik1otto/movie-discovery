namespace MovieDiscovery.Api.Contracts;

/// <summary>
/// Метадані постера з ВЛАСНОГО сховища MinIO (контейнер moviediscovery-minio, бакет md-posters).
/// GET /api/img/posters/{*key}?meta=1 повертає цей JSON замість бінарного стріму — фронт може
/// чесно перевірити наявність/тип/розмір постера без завантаження зображення (споживає стільки
/// ж байтів, скільки сам файл, — але дає можливість умовного рендера).
/// </summary>
public sealed record PosterImageResponse(string Key, string ContentType, long SizeBytes);
