using Microsoft.AspNetCore.Authentication.Cookies;

namespace MovieDiscovery.Api.Auth;

/// <summary>
/// FR-PERSIST: спільні сталі cookie-автентифікації та DataProtection key-ring.
/// Program.cs читає звідси — значення в коді й у тестах (AuthRememberTests)
/// не можуть розійтися. InMemory-тести не ганяють файлові шляхи DataProtection,
/// тому файлова частина перевіряється окремим тестом із тимчасовим каталогом.
/// </summary>
public static class CookieSetup
{
    /// <summary>Стабільне ім'я додатку: дискримінатор ключів DataProtection не залежить
    /// від шляху збірки/контейнера, тож key-ring переживає перестворення контейнера.</summary>
    public const string ApplicationName = "moviediscovery";

    /// <summary>Ім'я auth-cookie (при великому payload чанкіться .C1/.C2 — e2e-чеки звіряють префікс).</summary>
    public const string CookieName = "md_auth";

    /// <summary>Змінна оточення з каталогом key-ring (volume мапиться в docker compose).</summary>
    public const string KeysDirectoryEnv = "ASPNETKEYS_DIRECTORY";

    /// <summary>
    /// Каталог key-ring: лише якщо ASPNETKEYS_DIRECTORY задана (compose мапить
    /// ./data/keys:/data/keys). Без неї лишається дефолтна поведінка DataProtection
    /// (у Linux-контейнері /root/.aspnet/DataProtection-Keys з попередженням у логах,
    /// на Windows-хості — профіль користувача): шлях /root на Windows-хості тестів
    /// невалідний (спроба створити C:\root).
    /// </summary>
    public static string? ResolveKeysDirectory() =>
        Environment.GetEnvironmentVariable(KeysDirectoryEnv) is { Length: > 0 } dir ? dir : null;
}
