using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MovieDiscovery.Bot;

/// <summary>
/// Логіка команди /tunnel: дістати останній URL quick-тунелю з логу (спільний volume
/// tunnel-state із сервісом tunnel), перевірити живість через /api/health і за потреби
/// перезапустити контейнер tunnel через docker-сокет (єдиний docker-дозвіл бота).
/// </summary>
public sealed class TunnelService
{
    /// <summary>Лог тунелю всередині bot (volume tunnel-state спільний із сервісом tunnel).</summary>
    public const string LogPath = "/state/tunnel.log";

    /// <summary>Ім'я контейнера тунелю (container_name у docker-compose).</summary>
    public const string ContainerName = "moviediscovery-tunnel";

    /// <summary>Таймаут HTTP-перевірки живості URL.</summary>
    public static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Таймаут `docker restart` (зазвичай займає 1–2 с).</summary>
    public static readonly TimeSpan RestartTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Скільки чекати після рестарту, поки cloudflared видасть новий URL у лог.</summary>
    public static readonly TimeSpan RestartSettle = TimeSpan.FromSeconds(12);

    // trycloudflare пише URL у stderr (лог збирається через 2>&1 | tee); беремо ОСТАННІЙ збіг —
    // після рестарту tee обнуляє лог, тож останній URL завжди актуальний.
    private static readonly Regex TryCloudflareUrl = new(
        @"https://[a-z0-9-]*\.trycloudflare\.com",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Останній URL trycloudflare з логу тунелю; null — логу нема/порожній/без URL.</summary>
    public string? ReadUrl()
    {
        try
        {
            var text = File.ReadAllText(LogPath);
            var matches = TryCloudflareUrl.Matches(text);
            return matches.Count == 0 ? null : matches[matches.Count - 1].Value;
        }
        catch (Exception)
        {
            return null; // нема файлу (тунель ще не стартував) або лог недоступний
        }
    }

    /// <summary>Живий URL? GET {url}/api/health з таймаутом; відповідь 2xx = тунель працює.</summary>
    public async Task<bool> IsHealthyAsync(string url, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = HealthTimeout };
            using var res = await http.GetAsync(url.TrimEnd('/') + "/api/health", ct);
            return (int)res.StatusCode is >= 200 and < 300;
        }
        catch (Exception)
        {
            return false; // таймаут/DNS/мережа — вважаємо тунель мертвим
        }
    }

    /// <summary>`docker restart {ContainerName}`; true — команда завершилась з кодом 0.</summary>
    public bool RestartTunnel()
    {
        try
        {
            // вивід docker не перехоплюємо — він і так потрапить у лог контейнера bot
            using var proc = Process.Start(new ProcessStartInfo("docker", $"restart {ContainerName}")
            {
                UseShellExecute = false,
            });
            if (proc is null)
                return false;

            if (!proc.WaitForExit((int)RestartTimeout.TotalMilliseconds))
            {
                try { proc.Kill(); } catch { /* вже завершився сам */ }
                return false;
            }

            return proc.ExitCode == 0;
        }
        catch (Exception)
        {
            return false; // нема docker CLI/сокета — збій, обробник повідомить користувача
        }
    }
}
