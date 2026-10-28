using System.Diagnostics;
using System.Runtime.InteropServices;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => "HelloApi is running. Try GET /api/info.");

// Shows where the API runs, so a request from the host proves it reached the app inside the container.
app.MapGet("/api/info", (HttpContext context) => new
{
    Os = RuntimeInformation.OSDescription,
    MachineName = Environment.MachineName,
    InContainer = Environment.GetEnvironmentVariable("REMOTE_CONTAINERS") is not null
        || File.Exists("/.dockerenv")
        || File.Exists("/run/.containerenv"),
    DebuggerAttached = Debugger.IsAttached,
    Listening = app.Urls,
    RequestFrom = context.Connection.RemoteIpAddress?.ToString(),
    Time = DateTimeOffset.Now,
});

app.MapGet("/api/echo/{text}", (string text) => new { Text = text });

app.Run();
