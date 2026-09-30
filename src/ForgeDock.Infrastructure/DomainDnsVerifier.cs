using DnsClient;
using System.Net;

namespace ForgeDock.Infrastructure;

public interface IDomainDnsResolver
{
    Task<IReadOnlyList<string>> TxtAsync(string hostname, CancellationToken ct);
    Task<IReadOnlyList<IPAddress>> AddressesAsync(string hostname, CancellationToken ct);
}

public sealed class DomainDnsResolver : IDomainDnsResolver
{
    private readonly LookupClient client = new(new LookupClientOptions(IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8"))
        { UseCache = false, Timeout = TimeSpan.FromSeconds(3), Retries = 1 });
    public async Task<IReadOnlyList<string>> TxtAsync(string hostname, CancellationToken ct)
    {
        var result = await client.QueryAsync(hostname, QueryType.TXT, cancellationToken: ct);
        return result.Answers.TxtRecords().Select(record => string.Concat(record.Text)).ToArray();
    }
    public async Task<IReadOnlyList<IPAddress>> AddressesAsync(string hostname, CancellationToken ct)
    {
        var results = await Task.WhenAll(client.QueryAsync(hostname, QueryType.A, cancellationToken: ct),
            client.QueryAsync(hostname, QueryType.AAAA, cancellationToken: ct));
        return results.SelectMany(result => result.Answers.ARecords().Select(record => record.Address)
            .Concat(result.Answers.AaaaRecords().Select(record => record.Address))).Distinct().ToArray();
    }
}

public sealed record DomainVerification(bool Verified, string? Error);
public sealed class DomainDnsVerifier(IDomainDnsResolver resolver)
{
    public async Task<DomainVerification> VerifyAsync(string hostname, string token, DomainSettings settings, CancellationToken ct)
    {
        if (settings.Validate() is { } configurationError) return new(false, configurationError);
        try
        {
            var records = await resolver.TxtAsync("_forgedock." + hostname, ct);
            if (!records.Contains(token, StringComparer.Ordinal))
                return new(false, "The TXT ownership record is missing or does not match. Add the exact record shown below and allow DNS to propagate.");
            var addresses = await resolver.AddressesAsync(hostname, ct);
            var expected = settings.Addresses.Select(IPAddress.Parse).ToHashSet();
            if (addresses.Count == 0)
                return new(false, "No A or AAAA address was found. Point the domain at this server using the address records or a CNAME to the provided target.");
            if (addresses.Any(address => !expected.Contains(address)))
                return new(false, "The domain resolves to a different server. Check all A and AAAA records and disable any DNS proxy while verifying.");
            return new(true, null);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { return new(false, "DNS could not be checked. Ensure the worker can reach public DNS resolvers on port 53, then retry."); }
    }
}
