using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace MovieDiscovery.Infrastructure.Services.Emailing;

/// <summary>Відправка листів (auth: підтвердження пошти, скидання пароля).</summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default);
}

/// <summary>
/// Опції листів (auth-email). Bind: конфіг-секція «Email», ENV-дефолти — EmailEnvDefaults
/// (клієнт конфігу старший за env). ENV-назви: MAIL_ENABLED, SMTP_HOST, SMTP_PORT,
/// SMTP_USER, SMTP_PASSWORD, MAIL_FROM, FRONT_PUBLIC_URL, MAIL_REQUIRE_CONFIRMED_EMAIL.
/// </summary>
public sealed class EmailOptions
{
    /// <summary>false (дефолт) → LoggingEmailSender пише лист/лінк у лог (чесний dev-режим).</summary>
    public bool Enabled { get; set; }

    public string? Host { get; set; }

    /// <summary>587 (STARTTLS) — дефолт; 465 (implicit SSL) System.Net.Mail.SmtpClient не вміє.</summary>
    public int Port { get; set; } = 587;

    public string? User { get; set; }
    public string? Password { get; set; }
    public string? From { get; set; }

    /// <summary>База фронту для лінків /confirm-email та /reset-password.</summary>
    public string FrontPublicUrl { get; set; } = "http://localhost:3001";

    public bool UseSsl { get; set; } = true;

    /// <summary>
    /// Гейт логіна непідтверджених (403 email_not_confirmed) + без авто-сесії на register.
    /// null → наслідує Enabled (SMTP налаштовано → підтвердження обов'язкове).
    /// </summary>
    public bool? RequireConfirmedEmail { get; set; }

    public bool EffectiveRequireConfirmedEmail => RequireConfirmedEmail ?? Enabled;

    /// <summary>
    /// Фолбек без DI (коли Program.cs ще без AddEmailing): читає ті самі env-назви напряму.
    /// Пріоритети як в Options-патерну: явний конфіг (через AddEmailing) &gt; env &gt; дефолти.
    /// </summary>
    public static EmailOptions FromEnvironment()
    {
        var o = new EmailOptions();
        if (EnvBool("MAIL_ENABLED") is { } enabled) o.Enabled = enabled;
        o.Host = Env("SMTP_HOST") ?? o.Host;
        if (int.TryParse(Env("SMTP_PORT"), out var port)) o.Port = port;
        o.User = Env("SMTP_USER") ?? o.User;
        o.Password = Env("SMTP_PASSWORD") ?? o.Password;
        o.From = Env("MAIL_FROM") ?? o.From;
        o.FrontPublicUrl = Env("FRONT_PUBLIC_URL") ?? o.FrontPublicUrl;
        if (EnvBool("MAIL_REQUIRE_CONFIRMED_EMAIL") is { } required) o.RequireConfirmedEmail = required;
        return o;
    }

    internal static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    internal static bool? EnvBool(string name)
    {
        var value = Env(name);
        return value is null ? null : bool.TryParse(value, out var parsed) ? parsed : null;
    }
}

/// <summary>ENV-дефолти EmailOptions: застосовуються лише для ключів, яких немає в конфізі.</summary>
public sealed class EmailEnvDefaults(IConfiguration configuration) : IConfigureOptions<EmailOptions>
{
    public void Configure(EmailOptions o)
    {
        if (configuration["Email:Enabled"] is null && EnvBool("MAIL_ENABLED") is { } enabled)
            o.Enabled = enabled;
        if (configuration["Email:Host"] is null)
            o.Host = Env("SMTP_HOST") ?? o.Host;
        if (configuration["Email:Port"] is null && int.TryParse(Env("SMTP_PORT"), out var port))
            o.Port = port;
        if (configuration["Email:User"] is null)
            o.User = Env("SMTP_USER") ?? o.User;
        if (configuration["Email:Password"] is null)
            o.Password = Env("SMTP_PASSWORD") ?? o.Password;
        if (configuration["Email:From"] is null)
            o.From = Env("MAIL_FROM") ?? o.From;
        if (configuration["Email:FrontPublicUrl"] is null)
            o.FrontPublicUrl = Env("FRONT_PUBLIC_URL") ?? o.FrontPublicUrl;
        if (configuration["Email:UseSsl"] is null && EnvBool("SMTP_USE_SSL") is { } useSsl)
            o.UseSsl = useSsl;
        if (configuration["Email:RequireConfirmedEmail"] is null && EnvBool("MAIL_REQUIRE_CONFIRMED_EMAIL") is { } required)
            o.RequireConfirmedEmail = required;
    }

    private static string? Env(string name) => EmailOptions.Env(name);

    private static bool? EnvBool(string name) => EmailOptions.EnvBool(name);
}
