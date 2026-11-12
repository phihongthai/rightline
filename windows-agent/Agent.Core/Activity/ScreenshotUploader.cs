using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace KidSafe.Agent.Core;

/// <summary>
/// Durable screenshot queue (max 50 files) + presigned upload. Files are deleted right after a successful upload.
/// Flow: POST presign -> PUT to storage -> POST confirm.
/// </summary>
public sealed class ScreenshotUploader(HttpClient http, string baseUrl, string queueDir, int maxQueue = 50)
{
    public const int MaxBytes = 400_000;
    private readonly SemaphoreSlim _flush = new(1, 1);

    public int QueuedCount => Directory.Exists(queueDir) ? Directory.GetFiles(queueDir, "*.jpg").Length : 0;

    public bool Enqueue(byte[] jpeg, DateTimeOffset capturedAt)
    {
        if (jpeg.Length is 0 or > MaxBytes) return false;
        Directory.CreateDirectory(queueDir);
        var name = $"{capturedAt.UtcDateTime:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.jpg";
        File.WriteAllBytes(Path.Combine(queueDir, name), jpeg);
        var files = Directory.GetFiles(queueDir, "*.jpg").OrderBy(f => f, StringComparer.Ordinal).ToList();
        foreach (var old in files.Take(Math.Max(0, files.Count - maxQueue))) TryDelete(old);   // drop oldest
        return true;
    }

    /// <returns>Number uploaded. Stops at the first transient failure and leaves the rest queued.</returns>
    public async Task<int> FlushAsync(string token, CancellationToken ct = default)
    {
        if (!Directory.Exists(queueDir)) return 0;
        await _flush.WaitAsync(ct);
        try
        {
            int sent = 0;
            foreach (var file in Directory.GetFiles(queueDir, "*.jpg").OrderBy(f => f, StringComparer.Ordinal))
            {
                var r = await UploadOneAsync(file, token, ct);
                if (r == Outcome.Retry) break;
                if (r == Outcome.Done) sent++;
                TryDelete(file);       // Done or permanently rejected
            }
            return sent;
        }
        finally { _flush.Release(); }
    }

    private enum Outcome { Done, Retry, Drop }

    private async Task<Outcome> UploadOneAsync(string file, string token, CancellationToken ct)
    {
        try
        {
            var data = await File.ReadAllBytesAsync(file, ct);
            var capturedAt = ParseTime(Path.GetFileName(file));

            using var pre = Req(HttpMethod.Post, "/api/agent/screenshots/presign", token, JsonContent.Create(new { content_type = "image/jpeg" }));
            using var preRes = await http.SendAsync(pre, ct);
            var cls = Classify(preRes.StatusCode);
            if (cls is not null) return cls.Value;
            var doc = await preRes.Content.ReadFromJsonAsync<JsonElement>(ct);
            var key = doc.GetProperty("storage_key").GetString()!;
            var url = doc.GetProperty("upload_url").GetString()!;

            using var put = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(data) };
            put.Content.Headers.ContentType = new("image/jpeg");
            using var putRes = await http.SendAsync(put, ct);
            if (!putRes.IsSuccessStatusCode) return Outcome.Retry;

            using var conf = Req(HttpMethod.Post, "/api/agent/screenshots/confirm", token,
                JsonContent.Create(new { storage_key = key, captured_at = capturedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") }));
            using var confRes = await http.SendAsync(conf, ct);
            if (confRes.StatusCode == HttpStatusCode.Created) return Outcome.Done;
            return Classify(confRes.StatusCode) ?? Outcome.Retry;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return Outcome.Retry;
        }
    }

    // 401/403/408/429/5xx = try again later; other 4xx = this file can never succeed.
    private static Outcome? Classify(HttpStatusCode c) => c switch
    {
        HttpStatusCode.OK or HttpStatusCode.Created => null,
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.RequestTimeout or (HttpStatusCode)429 => Outcome.Retry,
        >= HttpStatusCode.InternalServerError => Outcome.Retry,
        >= HttpStatusCode.BadRequest => Outcome.Drop,
        _ => Outcome.Retry,
    };

    private HttpRequestMessage Req(HttpMethod m, string path, string token, HttpContent body)
    {
        var r = new HttpRequestMessage(m, baseUrl.TrimEnd('/') + path) { Content = body };
        r.Headers.Authorization = new("Bearer", token);
        return r;
    }

    private static DateTimeOffset ParseTime(string name) =>
        DateTimeOffset.TryParseExact(name[..17], "yyyyMMddHHmmssfff", null, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : DateTimeOffset.UtcNow;

    private static void TryDelete(string f) { try { File.Delete(f); } catch { /* ignore */ } }
}
