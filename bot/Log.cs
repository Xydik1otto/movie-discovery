using System.Globalization;

namespace MovieDiscovery.Bot;

/// <summary>Мінімалістичний лог у stdout (контейнери читають stdout/stderr).</summary>
public static class Log
{
    private static string Ts() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static void Info(string message) => Console.WriteLine($"[{Ts()}] [info] {message}");

    public static void Error(string message) => Console.WriteLine($"[{Ts()}] [error] {message}");
}
