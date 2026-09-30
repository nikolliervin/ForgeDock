var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:" + (Environment.GetEnvironmentVariable("PORT") ?? "8080"));
var app = builder.Build();
app.MapGet("/health", () => new { status = "ok" });
app.MapGet("/", () => new { message = "Hello from ForgeDock" });
app.Run();
