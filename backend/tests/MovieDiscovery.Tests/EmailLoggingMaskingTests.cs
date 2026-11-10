using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MovieDiscovery.Infrastructure.Services.Emailing;

namespace MovieDiscovery.Tests;

/// <summary>
/// Маскування листів у логах (MAIL_ENABLED=false, LoggingEmailSender):
/// Production → у лог ЛИШЕ to/subject (тіло з one-time токенами підтвердження/скидання
/// НЕ потрапляє в логи); у не-Production (dev/Testing) — чесний dev-режим як раніше.
/// </summary>
public class EmailLoggingMaskingTests
{
    private const string BodyWithToken =
        """<html>Ваш лінк: https://md.example/confirm?token=SECRET_ONE_TIME_TOKEN_42</html>""";

    private sealed class CapturingLogger : ILogger<LoggingEmailSender>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class FakeEnv(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static string Send(IHostEnvironment? env)
    {
        var logger = new CapturingLogger();
        new LoggingEmailSender(logger, env).SendAsync(
            "user@example.com", "Підтвердження email", BodyWithToken).Wait();
        return Assert.Single(logger.Messages);
    }

    [Fact]
    public void Production_MasksBody_LogsOnlyToAndSubject()
    {
        var message = Send(new FakeEnv("Production"));

        Assert.Contains("user@example.com", message);
        Assert.Contains("Підтвердження email", message);
        Assert.Contains("приховано", message);
        // Тіло з one-time токеном НЕ в логах:
        Assert.DoesNotContain("SECRET_ONE_TIME_TOKEN_42", message);
        Assert.DoesNotContain("confirm", message);
    }

    [Fact]
    public void NonProduction_KeepsFullBodyAsBefore()
    {
        // dev-зручність (чесний лог-режим) не ламалась: тіло з лінком видно.
        var devMessage = Send(new FakeEnv("Development"));
        Assert.Contains("SECRET_ONE_TIME_TOKEN_42", devMessage);

        var testingMessage = Send(new FakeEnv("Testing"));
        Assert.Contains("SECRET_ONE_TIME_TOKEN_42", testingMessage);
    }

    [Fact]
    public void NullEnv_KeepsFullBodyAsBefore()
    {
        // Фолбек-хост без IHostEnvironment (стара сигнатура) → попередня поведінка.
        var message = Send(env: null);
        Assert.Contains("SECRET_ONE_TIME_TOKEN_42", message);
    }
}
