using AnInfo.MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "logs"));
builder.Host.UseSerilog((context, _, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(builder.Environment.ContentRootPath, "logs", "mailserver-.log"), rollingInterval: RollingInterval.Day));

builder.WebHost.UseUrls(builder.Configuration["Health:Url"] ?? "http://127.0.0.1:8085");
builder.Services.AddMailServerInfrastructure(builder.Configuration);
builder.Services.AddHealthChecks().AddDbContextCheck<MailDbContext>("postgresql");

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
    await db.Database.MigrateAsync();
}
app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Ok(new { service = "AnInfo.MailServer", health = "/health" }));
try { Log.Information("Starting AnInfo.MailServer"); await app.RunAsync(); }
catch (Exception ex) { Log.Fatal(ex, "AnInfo.MailServer terminated unexpectedly"); throw; }
finally { await Log.CloseAndFlushAsync(); }
