using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MyScorer.Api.Services;
using MyScorer.Application.Data;
using MyScorer.Application.Services;
using MyScorer.Application.Services.Providers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSingleton<IMatchStateService, InMemoryMatchStateService>();
builder.Services.AddScoped<IMatchRegistrationService, EfCoreMatchRegistrationService>();
builder.Services.AddDbContext<MyScorerDbContext>(options =>
    options.UseSqlite("Data Source=myscorer.db"));
builder.Services.AddScoped<IAdminStateService, EfCoreAdminStateService>();
builder.Services.AddSingleton<PlayHqSpectatorService>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var logger = sp.GetRequiredService<ILogger<PlayHqSpectatorService>>();
    var wsUrl = config["PlayHQ:SpectatorWsUrl"];
    return new PlayHqSpectatorService(wsUrl, logger);
});
builder.Services.AddHttpClient<IScoreExtractionService, ScoreExtractionService>(client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    client.Timeout = TimeSpan.FromSeconds(15);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AutomaticDecompression = DecompressionMethods.All,
    UseCookies = true,
    AllowAutoRedirect = true,
    MaxAutomaticRedirections = 5
});

builder.Services.AddHostedService<MyScorer.Api.Services.MaintenanceService>();

var app = builder.Build();

// One-time DB creation and schema migration
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MyScorerDbContext>();
    db.Database.EnsureCreated();
    db.EnsureNewTablesCreated();
}

var setupIdRegex = new Regex(@"^[a-zA-Z0-9\-]{1,20}$", RegexOptions.Compiled);
var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "admin", "api", "overlay", "setup", "cheesecake", "favicon.ico", "health"
};

app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;

    if (path == "/" ||
        path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/cheesecake/admin", StringComparison.OrdinalIgnoreCase) ||
        Path.HasExtension(path))
    {
        await next();
        return;
    }

    // Block direct access to /setup and /overlay directories
    if (path.StartsWith("/setup", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/overlay", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = 404;
        return;
    }

    // Route /{setupId}/live to the live scoreboard overlay
    var segments = path.Trim('/').Split('/');
    if (segments.Length == 2 && segments[1].Equals("live", StringComparison.OrdinalIgnoreCase)
        && setupIdRegex.IsMatch(segments[0]) && !reservedPaths.Contains(segments[0]))
    {
        var liveFile = Path.Combine(app.Environment.ContentRootPath, "wwwroot", "overlay", "live.html");
        if (File.Exists(liveFile))
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.SendFileAsync(liveFile);
            return;
        }
    }

    // Only serve setup page for single-segment paths like /{setupId}
    if (segments.Length == 1 && setupIdRegex.IsMatch(segments[0]) && !reservedPaths.Contains(segments[0]))
    {
        var fallbackFile = Path.Combine(app.Environment.ContentRootPath, "wwwroot", "setup", "index.html");
        if (File.Exists(fallbackFile))
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.SendFileAsync(fallbackFile);
            return;
        }
    }

    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();

var startTime = DateTime.UtcNow;
app.MapGet("/health", () =>
{
    var uptime = DateTime.UtcNow - startTime;
    var memInfo = GC.GetGCMemoryInfo();
    return Results.Ok(new
    {
        status = "healthy",
        uptimeMinutes = Math.Round(uptime.TotalMinutes, 1),
        memoryMB = Math.Round(GC.GetTotalMemory(false) / (1024.0 * 1024.0), 1),
        heapSizeMB = Math.Round(memInfo.HeapSizeBytes / (1024.0 * 1024.0), 1),
        gen0Collections = GC.CollectionCount(0),
        gen1Collections = GC.CollectionCount(1),
        gen2Collections = GC.CollectionCount(2),
        timestamp = DateTime.UtcNow
    });
});

// Detailed diagnostics (admin use — queries DB)
app.MapGet("/health/detail", (MyScorerDbContext db) =>
{
    var uptime = DateTime.UtcNow - startTime;
    var memInfo = GC.GetGCMemoryInfo();
    var maint = MyScorer.Api.Services.MaintenanceService.GetStats();
    return Results.Ok(new
    {
        status = "healthy",
        uptimeMinutes = Math.Round(uptime.TotalMinutes, 1),
        memoryMB = Math.Round(GC.GetTotalMemory(false) / (1024.0 * 1024.0), 1),
        heapSizeMB = Math.Round(memInfo.HeapSizeBytes / (1024.0 * 1024.0), 1),
        gen0Collections = GC.CollectionCount(0),
        gen1Collections = GC.CollectionCount(1),
        gen2Collections = GC.CollectionCount(2),
        threadPoolThreads = ThreadPool.ThreadCount,
        deviceCount = db.StreamingDevices.Count(),
        commands = new
        {
            pending = db.DeviceCommands.Count(c => c.Status == "Pending"),
            delivered = db.DeviceCommands.Count(c => c.Status == "Delivered"),
            completed = db.DeviceCommands.Count(c => c.Status == "Completed"),
            total = db.DeviceCommands.Count()
        },
        maintenance = new
        {
            lastRunAt = maint.LastRunAt,
            lastSuccessAt = maint.LastSuccessAt,
            cycleCount = maint.CycleCount,
            totalEvicted = maint.TotalEvicted,
            totalPurged = maint.TotalPurged,
            totalRecovered = maint.TotalRecovered
        },
        scoreCacheCount = MyScorer.Application.Services.ScoreExtractionService.CacheCount,
        timestamp = DateTime.UtcNow
    });
});

app.Run();