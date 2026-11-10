using System.Text.Json;
using System.Text.Json.Serialization;
using Depot.Api.Endpoints;
using Depot.Api.Errors;
using Depot.Api.Health;
using Depot.Api.Security;
using Depot.Application;
using Depot.Infrastructure;
using Depot.Infrastructure.Backup;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;

if (args is ["--healthcheck"])
{
    return await HealthProbe.RunAsync();
}

// "--backup <folder>" or "--restore <archive>": the app's configuration, without starting it.
if (BackupCommand.Handles(args))
{
    var settings = WebApplication.CreateBuilder();
    return await BackupCommand.RunAsync(args, settings.Configuration, settings.Environment, Console.Out);
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);

// Enums travel as camelCase strings, such as "downloading", rather than numbers.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddDepotRateLimits();

// No CORS: the API serves the frontend itself (below), so the browser only ever calls it from
// the same origin, and pages on other sites are refused.
builder.Services.AddSingleton(services => AccessToken.Load(builder.Configuration, services.GetRequiredService<StoragePaths>()));

// The database decides whether Depot is healthy; qBittorrent only whether it's degraded.
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database")
    .AddCheck<QBittorrentHealthCheck>("qbittorrent")
    .AddCheck<JellyfinHealthCheck>("jellyfin");

var app = builder.Build();

var accessToken = app.Services.GetRequiredService<AccessToken>();
app.Logger.LogInformation("Depot's access token comes from {Source}. The interface asks for it once per browser.", accessToken.Source);

app.UseExceptionHandler();

// The interface must not be framed by another site (clickjacking) or load anything from elsewhere.
// Images may also be blob: URLs, which the page makes from the posters it fetched from /api with the token.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers.XFrameOptions = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers.ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data: blob:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    await next(context);
});

app.UseRateLimiter();
app.UseMiddleware<ApiRequestGuard>();

// The frontend, from the same origin as the API. Frontend:Path is relative to the content root.
var frontendPath = Path.GetFullPath(builder.Configuration["Frontend:Path"] ?? "../../frontend", builder.Environment.ContentRootPath);
if (Directory.Exists(frontendPath))
{
    var frontend = new PhysicalFileProvider(frontendPath);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = frontend });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = frontend,
        // No build step means no versioned file names, so the browser checks for changes each time.
        OnPrepareResponse = file => file.Context.Response.Headers.CacheControl = "no-cache",
    });
}
else
{
    app.Logger.LogWarning("The frontend folder {Path} doesn't exist, so only the API is served. Set Frontend:Path.", frontendPath);
}

// 200 when healthy or degraded, 503 when unhealthy.
app.MapHealthChecks("/api/health", new HealthCheckOptions
{
    ResponseWriter = HealthResponseWriter.WriteAsync,
});

app.MapQBittorrentSettingsEndpoints();
app.MapStatusEndpoints();
app.MapDownloadEndpoints();
app.MapOrganizationEndpoints();
app.MapJellyfinSettingsEndpoints();
app.MapJellyfinEndpoints();

app.Run();
return 0;

// Lets the tests start the app with WebApplicationFactory.
public partial class Program;
