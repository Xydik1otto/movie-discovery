using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Core.Domain;

namespace MovieDiscovery.Infrastructure.Persistence;

/// <summary>Сід початкових даних: єдиний профіль «Головний» (is_default=true) при першому старті.</summary>
public static class DbSeeder
{
    public static async Task SeedDefaultProfileAsync(MovieDiscoveryDbContext db, CancellationToken ct = default)
    {
        if (!await db.Profiles.AnyAsync(ct))
        {
            db.Profiles.Add(new Profile { Name = "Головний", IsDefault = true });
            await db.SaveChangesAsync(ct);
        }
    }
}
