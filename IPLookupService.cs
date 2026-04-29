using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CS2IPSuite;

/// <summary>
/// Pulls geo/ISP information for an IPv4 address from the free ip-api.com
/// endpoint. The free tier allows ~45 req/min from a single IP and does not
/// need an API key, which is plenty for an admin tool.
/// </summary>
public sealed class IPLookupService : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public IPLookupService(ILogger logger)
    {
        _logger = logger;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CS2-IP-Suite/1.0 (+BONE)");
    }

    public async Task<IPInfo?> LookupAsync(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;

        // Treat private/loopback ranges as un-lookupable to avoid spamming the
        // public service with requests it can never resolve.
        if (IsPrivateIp(ip))
        {
            return new IPInfo
            {
                Ip      = ip,
                Isp     = "LAN / Private network",
                City    = "Local",
                Region  = "Local",
                Country = "Local",
            };
        }

        try
        {
            var url = $"http://ip-api.com/json/{Uri.EscapeDataString(ip)}" +
                      "?fields=status,message,country,regionName,city,isp,org,query";

            var resp = await _http.GetFromJsonAsync<IpApiResponse>(url).ConfigureAwait(false);
            if (resp == null || !string.Equals(resp.Status, "success", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("[CS2-IP-Suite] WHOIS lookup failed for {ip}: {msg}", ip, resp?.Message ?? "no response");
                return null;
            }

            return new IPInfo
            {
                Ip      = resp.Query   ?? ip,
                Isp     = !string.IsNullOrWhiteSpace(resp.Isp) ? resp.Isp! :
                          !string.IsNullOrWhiteSpace(resp.Org) ? resp.Org! : "Unknown",
                City    = resp.City        ?? "Unknown",
                Region  = resp.RegionName  ?? "Unknown",
                Country = resp.Country     ?? "Unknown",
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CS2-IP-Suite] WHOIS lookup threw for {ip}", ip);
            return null;
        }
    }

    private static bool IsPrivateIp(string ip)
    {
        if (!System.Net.IPAddress.TryParse(ip, out var addr)) return false;
        if (System.Net.IPAddress.IsLoopback(addr)) return true;

        var b = addr.GetAddressBytes();
        if (b.Length != 4) return false;

        // 10.0.0.0/8
        if (b[0] == 10) return true;
        // 172.16.0.0/12
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        // 192.168.0.0/16
        if (b[0] == 192 && b[1] == 168) return true;
        // 169.254.0.0/16 link-local
        if (b[0] == 169 && b[1] == 254) return true;
        // 127.0.0.0/8 already handled by IsLoopback but check anyway
        if (b[0] == 127) return true;
        return false;
    }

    public void Dispose() => _http.Dispose();

    private sealed class IpApiResponse
    {
        [JsonPropertyName("status")]     public string? Status     { get; set; }
        [JsonPropertyName("message")]    public string? Message    { get; set; }
        [JsonPropertyName("country")]    public string? Country    { get; set; }
        [JsonPropertyName("regionName")] public string? RegionName { get; set; }
        [JsonPropertyName("city")]       public string? City       { get; set; }
        [JsonPropertyName("isp")]        public string? Isp        { get; set; }
        [JsonPropertyName("org")]        public string? Org        { get; set; }
        [JsonPropertyName("query")]      public string? Query      { get; set; }
    }
}
