using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

namespace KidSafe.Agent.Core;

/// <summary>Uninstall gate: offline daily PIN (derived from the device token) or the parent's account password via API.</summary>
public static class UninstallAuth
{
    public static string Pin(string deviceToken, DateOnly utcDate)
    {
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceToken))).ToLowerInvariant();
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(tokenHash), Encoding.UTF8.GetBytes($"uninstall:{utcDate:yyyy-MM-dd}"));
        var n = (uint)(mac[0] << 24 | mac[1] << 16 | mac[2] << 8 | mac[3]);
        return (n % 100_000_000).ToString("D8");
    }

    /// <summary>Accepts today's and yesterday's (UTC) PIN to survive a midnight rollover.</summary>
    public static bool VerifyPin(string deviceToken, string pin, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var candidates = new[] { Pin(deviceToken, today), Pin(deviceToken, today.AddDays(-1)) };
        var given = Encoding.ASCII.GetBytes(pin.Trim());
        return candidates.Any(c => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(c), given));
    }

    /// <returns>0 = allowed, 1 = denied, 2 = could not verify (offline and not a valid PIN).</returns>
    public static async Task<int> VerifyAsync(AgentState state, string secret, HttpClient http, string baseUrl, TimeProvider time, CancellationToken ct = default)
    {
        if (!state.Paired) return 0;                         // nothing to protect
        if (VerifyPin(state.DeviceToken!, secret, time.GetUtcNow())) return 0;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/agent/uninstall-auth")
            {
                Content = JsonContent.Create(new { password = secret }),
            };
            req.Headers.Authorization = new("Bearer", state.DeviceToken);
            using var res = await http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode) return 0;
            return (int)res.StatusCode is 401 or 403 or 429 ? 1 : 2;
        }
        catch (HttpRequestException) { return 2; }
        catch (TaskCanceledException) { return 2; }
    }
}
