namespace ForgeDock.Infrastructure;

public sealed record ServiceStatus(string Name, string State, string? Health, string Image);
