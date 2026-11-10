namespace MovieDiscovery.Bot.Api;

/// <summary>
/// Результат виклику API — без викидання винятків назовні, щоб обробник бота
/// міг спокійно змапити помилку на дружнє повідомлення.
/// Класифікація: Status (HTTP-код) або Error: "timeout" | "unreachable" | "deserialization" | "empty".
/// </summary>
public sealed class ApiResult<T>
{
    public bool Ok { get; private init; }
    public T? Value { get; private init; }
    public int? Status { get; private init; }
    public string? Error { get; private init; }

    public static ApiResult<T> Success(T value) => new() { Ok = true, Value = value };

    public static ApiResult<T> Failure(int? status, string error) => new() { Ok = false, Status = status, Error = error };

    public bool IsNotFound => Status == 404;
    public bool IsServiceUnavailable => Status == 503;
    public bool IsTimeout => !Ok && Status is null && Error == "timeout";
    public bool IsUnreachable => !Ok && Status is null && Error == "unreachable";
}
