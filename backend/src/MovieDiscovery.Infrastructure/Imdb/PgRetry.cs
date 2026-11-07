using Npgsql;

namespace MovieDiscovery.Infrastructure.Imdb;

/// <summary>
/// Спільний харденінг IMDb bulk-синків: транзитні збої PG/інфраструктури (WSL2/Docker I/O:
/// 57P03 cannot_connect_now, 08006 connection_failure, 53300/53400 resource limits,
/// EINTR "Interrupted system call"), що заслуговують ретраю. Єдине джерело правди —
/// ImdbBulkService, ImdbAkasService, ImdbCrewService.
/// </summary>
internal static class PgRetry
{
    public static bool IsTransient(PostgresException ex) =>
        ex.SqlState is "57P03" or "08006" or "53300" or "53400"
        || ex.MessageText.Contains("Interrupted system call", StringComparison.OrdinalIgnoreCase);
}
