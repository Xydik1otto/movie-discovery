using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace MovieDiscovery.Api;

/// <summary>
/// Спільна перевірка X-Admin-Key для /api/admin/* та admin-операцій (колись була
/// продубльована в AdminController.CheckAdmin і TrailersController.CheckAdmin —
/// дублі неминуче роз'їжджаються при правках). Поведінка 1:1 з колишніми копіями:
/// ADMIN_KEY не задано/порожній → 500 {error:"admin_not_configured"} (конфіг-помилка,
/// fail-closed — compose-дефолт порожній, див. ADMIN_KEY у docker-compose.yml);
/// заголовка нема або не збігається (ordinal) → 401 {error:"unauthorized"}.
/// Ключ читається з конфіга "Admin:Key" — те саме джерело, що AdminOptions.Key
/// (Program.cs: Configure&lt;AdminOptions&gt;(GetSection("Admin")) + MapEnvDefaults ADMIN_KEY).
/// </summary>
public static class AdminGate
{
    public static IActionResult? Verify(HttpContext http, IConfiguration configuration)
    {
        var expected = configuration["Admin:Key"];
        if (string.IsNullOrEmpty(expected))
            return new ObjectResult(new { error = "admin_not_configured", message = "ADMIN_KEY не задано в .env" })
            {
                StatusCode = StatusCodes.Status500InternalServerError,
            };

        var provided = http.Request.Headers["X-Admin-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(provided) || !string.Equals(provided, expected, StringComparison.Ordinal))
            return new ObjectResult(new { error = "unauthorized", message = "Потрібен заголовок X-Admin-Key" })
            {
                StatusCode = StatusCodes.Status401Unauthorized,
            };
        return null;
    }
}
