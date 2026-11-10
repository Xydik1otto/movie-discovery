namespace MovieDiscovery.Api.Auth;

/// <summary>
/// uk-шаблони листів auth (простий HTML, без стилів) + лінки фронту.
/// Токен у лінку URL-encode (Uri.EscapeDataString): Identity-токен — base64
/// із «+»/«/»/«=», які в query-string без кодування ламаються.
/// </summary>
public static class AuthEmails
{
    public static string ConfirmationLink(string frontPublicUrl, string userId, string token) =>
        Link(frontPublicUrl, "confirm-email", userId, token);

    public static string ResetLink(string frontPublicUrl, string userId, string token) =>
        Link(frontPublicUrl, "reset-password", userId, token);

    private static string Link(string frontPublicUrl, string page, string userId, string token) =>
        $"{(frontPublicUrl ?? string.Empty).TrimEnd('/')}/{page}?userId={Uri.EscapeDataString(userId)}&token={Uri.EscapeDataString(token)}";

    public static (string Subject, string Html) Confirmation(string link) => (
        "КіноНавігатор — підтвердіть пошту",
        $"""
        <p>Вітаємо!</p>
        <p>Щоб підтвердити адресу електронної пошти, натисніть кнопку:</p>
        <p><a href="{link}">Підтвердити пошту</a></p>
        <p>Якщо кнопка не працює — скопіюйте лінк у браузер:<br>{link}</p>
        <p>Лінк діє 3 дні. Якщо ви не реєструвалися в КіноНавігаторі — проігноруйте цей лист.</p>
        """);

    public static (string Subject, string Html) Reset(string link) => (
        "КіноНавігатор — відновлення пароля",
        $"""
        <p>Хтось (сподіваємось, ви) запросив відновлення пароля.</p>
        <p>Щоб задати новий пароль, натисніть кнопку:</p>
        <p><a href="{link}">Відновити пароль</a></p>
        <p>Якщо кнопка не працює — скопіюйте лінк у браузер:<br>{link}</p>
        <p>Лінк діє 3 дні. Якщо ви не надсилали запит — проігноруйте лист, пароль не зміниться.</p>
        """);
}
