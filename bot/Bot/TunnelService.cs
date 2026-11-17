using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MovieDiscovery.Bot;

/// <summary>
/// Команда /tunnel перевіряє FRONT_PUBLIC_URL зареєстрованого тунелю.
/// Без нього підтримується старий quick-тунель із логом і перезапуском контейнера.
/// </summary>
public sealed class TunnelService
{
    /// <summary>Лог тунелю всередині bot (volume tunnel-state спільний із сервісом tunnel).</summary>
    public const string LogPath = "/state/tunnel.log";

    /// <summary>Ім'я контейнера тунелю (container_name у docker-compose).</summary>
    public const string ContainerName = "moviediscovery-tunnel";

    /// <summary>Таймаут HTTP-перевірки живості URL.</summary>
    public static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(5);

    private static readonly HttpClient HealthClient = new() { Timeout = HealthTimeout };
    private readonly HttpClient _healthClient;
    public string? PublicUrl { get; }
    public bool IsRegistered => PublicUrl is not null;

    public TunnelService(string? publicUrl = null, HttpClient? healthClient = null)
    {
        _healthClient = healthClient ?? HealthClient;
        if (string.IsNullOrWhiteSpace(publicUrl)) return;
        if (!Uri.TryCreate(publicUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("FRONT_PUBLIC_URL must be an absolute HTTP(S) URL without credentials, query or fragment.", nameof(publicUrl));
        PublicUrl = uri.AbsoluteUri.TrimEnd('/');
    }

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
        if (IsRegistered) return PublicUrl;
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
            using var res = await _healthClient.GetAsync(url.TrimEnd('/') + "/api/health", ct);
            return (int)res.StatusCode is >= 200 and < 300;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false; // таймаут/DNS/мережа — вважаємо тунель мертвим
        }
    }

    /// <summary>`docker restart {ContainerName}`; true — команда завершилась з кодом 0.</summary>
    public bool RestartTunnel()
    {
        if (IsRegistered) return false;
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
