using ForgeDock.Infrastructure;
using ForgeDock.Worker;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);
var connection = builder.Configuration.GetConnectionString("ForgeDock")
    ?? throw new InvalidOperationException("Set ConnectionStrings__ForgeDock to a PostgreSQL connection string.");
builder.Services.AddSingleton(new SecretProtector(builder.Configuration["ForgeDock:SecretKey"]
    ?? throw new InvalidOperationException("Set ForgeDock__SecretKey to the same key used by the API.")));
builder.Services.AddDbContext<ForgeDockDbContext>(o => o.UseNpgsql(connection));
builder.Services.AddSingleton<ProcessRunner>();
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();
