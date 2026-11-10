using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Services.Emailing;

namespace MovieDiscovery.Api.Auth;

/// <summary>
/// DI-реєстрація emailing (один рядок у Program.cs, using MovieDiscovery.Api.Auth вже є):
/// <code>builder.Services.AddEmailing(builder.Configuration);</code>
/// Без неї AuthController працює через env-фолбек (LoggingEmailSender / SmtpEmailSender за MAIL_ENABLED).
/// Розміщено в Api, бо DataProtectorTokenProvider живе в shared-framework
/// Microsoft.AspNetCore.Identity (split-пакет Microsoft.Extensions.Identity.* його не містить).
/// </summary>
public static class EmailingServiceCollectionExtensions
{
    public static IServiceCollection AddEmailing(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EmailOptions>().Bind(configuration.GetSection("Email"));
        // ENV-дефолти (MAIL_*/SMTP_*/FRONT_PUBLIC_URL) — лише для ключів, яких немає в конфізі.
        services.AddSingleton<IConfigureOptions<EmailOptions>, EmailEnvDefaults>();
        services.AddSingleton<EmailRateLimiter>();
        services.AddSingleton<IEmailSender>(sp =>
        {
            var options = sp.GetRequiredService<IOptionsMonitor<EmailOptions>>().CurrentValue;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            return options.Enabled
                ? new SmtpEmailSender(options, loggerFactory.CreateLogger<SmtpEmailSender>())
                : new LoggingEmailSender(loggerFactory.CreateLogger<LoggingEmailSender>());
        });

        // Identity-токен-провайдери для confirm/reset — БЕЗ них GenerateEmailConfirmationTokenAsync
        // / GeneratePasswordResetTokenAsync кидають NotSupportedException («No IUserTwoFactorTokenProvider…»).
        // Еквівалент AddDefaultTokenProviders(): DataProtectorTokenProvider — офіційний провайдер,
        // токен захищений DataProtection key-ring'ом і містить purpose (крос-призначення неможливі).
        services.Configure<IdentityOptions>(o =>
        {
            o.Tokens.ProviderMap[TokenOptions.DefaultEmailProvider] =
                new TokenProviderDescriptor(typeof(DataProtectorTokenProvider<ApplicationUser>)); // "EmailConfirmation"
            o.Tokens.ProviderMap[TokenOptions.DefaultProvider] =
                new TokenProviderDescriptor(typeof(DataProtectorTokenProvider<ApplicationUser>)); // "Default" — password reset
        });
        // UserManager у ctor робить services.GetRequiredService(ProviderType) —
        // DI не конструює незареєстровані concrete-типи, тож реєструємо і конкретний тип.
        services.AddTransient(typeof(DataProtectorTokenProvider<ApplicationUser>));
        services.AddTransient(typeof(IUserTwoFactorTokenProvider<ApplicationUser>),
            typeof(DataProtectorTokenProvider<ApplicationUser>));
        return services;
    }
}
