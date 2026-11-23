using System.Text.Json;
using System.Text.Json.Serialization;
using Depot.Api.Endpoints;
using Depot.Api.Errors;
using Depot.Api.Security;
using Depot.Application;
using Depot.Infrastructure;
using Depot.Infrastructure.Persistence;
using Depot.Infrastructure.Storage;
using Microsoft.Extensions.FileProviders;

if (args is ["--healthcheck"])
{
    return await HealthEndpoints.ProbeAsync();
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);

// Enums travel as camelCase strings, such as "downloading", rather than numbers.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddSingleton(services => AccessToken.Load(builder.Configuration, services.GetRequiredService<StoragePaths>()));

var app = builder.Build();
await app.Services.MigrateDatabaseAsync();

var accessToken = app.Services.GetRequiredService<AccessToken>();
app.Logger.LogInformation("The access token comes from {Source}. The interface asks for it once per browser.", accessToken.Source);

app.UseExceptionHandler();
app.UseStatusCodePages();

// The interface must not be framed by another site or load anything from elsewhere. Images may
// be blob: URLs, which the page makes from the posters it fetched from /api with the token.
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

app.UseMiddleware<ApiRequestGuard>();

// The frontend, served from the same origin as the API. Frontend:Path is relative to the content root.
var frontendPath = Path.GetFullPath(builder.Configuration["Frontend:Path"] ?? "../../frontend", builder.Environment.ContentRootPath);
if (Directory.Exists(frontendPath))
{
    var frontend = new PhysicalFileProvider(frontendPath);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = frontend });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = frontend,
        // No build step means no versioned file names, so the browser revalidates every time.
        OnPrepareResponse = file => file.Context.Response.Headers.CacheControl = "no-cache",
    });
}
else
{
    app.Logger.LogWarning("The frontend folder {Path} doesn't exist, so only the API is served. Set Frontend:Path.", frontendPath);
}

app.MapHealthEndpoints();
app.MapSettingsEndpoints();
app.MapStatusEndpoints();
app.MapDownloadEndpoints();
app.MapLibraryEndpoints();
app.MapImportEndpoints();
app.MapMediaEndpoints();

await app.RunAsync();
return 0;

// Lets the tests start the app with WebApplicationFactory.
public partial class Program;
