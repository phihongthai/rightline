using System.Net.Http.Json;

namespace KidSafe.Agent.Core;

public sealed record LogEntry(string LogType, string Target, string Title, int DurationSec, DateTimeOffset RecordedAt);

/// <summary>Aggregates app/web/system events and ships them in batches (spec: every 60s).</summary>
public sealed class ActivityLog(TimeProvider time, int maxPending = 2000)
{
    private readonly object _lock = new();
    private readonly Dictionary<(string, string), (double secs, DateTimeOffset at)> _apps = new();
    private readonly Dictionary<(string, bool), DateTimeOffset> _domains = new();
    private readonly List<LogEntry> _ready = [];

    public void RecordApp(string? app, string? title, double seconds)
    {
        if (string.IsNullOrWhiteSpace(app) || seconds <= 0) return;
        title = (title ?? "").Trim();
        if (title.Length > 200) title = title[..200];
        lock (_lock)
        {
            var k = (app, title);
            _apps[k] = _apps.TryGetValue(k, out var v) ? (v.secs + seconds, v.at) : (seconds, time.GetUtcNow());
        }
    }

    /// <summary>One entry per domain per flush window; blocked ones are marked in the title.</summary>
    public void RecordDomain(string domain, bool blocked)
    {
        lock (_lock)
        {
            if (_domains.Count >= 300 && !blocked) return;          // CDN noise guard
            _domains.TryAdd((domain, blocked), time.GetUtcNow());
        }
    }

    public void RecordSystem(string target, string title)
    {
        lock (_lock) Add(new LogEntry("system", target, title, 0, time.GetUtcNow()));
    }

    private void Add(LogEntry e)
    {
        _ready.Add(e);
        if (_ready.Count > maxPending) _ready.RemoveRange(0, _ready.Count - maxPending);
    }

    /// <summary>Moves aggregates to the ready list and returns up to <paramref name="max"/> entries (oldest first).</summary>
    public List<LogEntry> Drain(int max = 400)
    {
        lock (_lock)
        {
            foreach (var (k, v) in _apps) Add(new LogEntry("app", k.Item1, k.Item2, (int)Math.Round(v.secs), v.at));
            foreach (var (k, at) in _domains) Add(new LogEntry("web", k.Item1, k.Item2 ? "blocked" : "", 0, at));
            _apps.Clear(); _domains.Clear();
            var take = _ready.Take(max).ToList();
            _ready.RemoveRange(0, take.Count);
            return take;
        }
    }

    public void Requeue(List<LogEntry> entries)
    {
        lock (_lock) { _ready.InsertRange(0, entries); if (_ready.Count > maxPending) _ready.RemoveRange(0, _ready.Count - maxPending); }
    }

    public int Pending { get { lock (_lock) return _ready.Count + _apps.Count + _domains.Count; } }

    /// <returns>true if nothing to send or the batch was accepted.</returns>
    public async Task<bool> FlushAsync(HttpClient http, string baseUrl, string token, CancellationToken ct = default)
    {
        var batch = Drain();
        if (batch.Count == 0) return true;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/agent/logs/batch")
            {
                Content = JsonContent.Create(new
                {
                    logs = batch.Select(e => new
                    {
                        log_type = e.LogType, target = Trunc(e.Target, 500), title = Trunc(e.Title, 500),
                        duration_sec = Math.Clamp(e.DurationSec, 0, 86400), recorded_at = e.RecordedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                    }),
                }),
            };
            req.Headers.Authorization = new("Bearer", token);
            using var res = await http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode) return true;
            if ((int)res.StatusCode is >= 400 and < 500 and not 401 and not 429) return true;   // poison batch: drop
            Requeue(batch);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Requeue(batch);
            return false;
        }
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];
}
