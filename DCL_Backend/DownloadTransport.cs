using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DCL_Launcher;

public sealed partial class DownloadManager
{
    private const long ParallelThreshold = 4L * 1024 * 1024;
    private readonly AsyncLocal<string?> _currentTask = new();
    private readonly SemaphoreSlim[] _fileLocks = Enumerable.Range(0, 127).Select(_ => new SemaphoreSlim(1)).ToArray();
    private sealed record Representation(string Url, string Tag, long? Length);
    private sealed class RangeUnavailable : Exception { }
    private sealed class BackoffException(HttpRequestException error) : HttpRequestException(error.Message, error, error.StatusCode) { }

    private void CheckStatus(HttpResponseMessage response, string url)
    {
        if (response.IsSuccessStatusCode) return;
        try { response.EnsureSuccessStatusCode(); }
        catch (HttpRequestException ex) when ((int)response.StatusCode is 429 or 503)
        {
            var header = response.Headers.RetryAfter;
            _health.Failure(url, ex, header?.Delta ?? (header?.Date - DateTimeOffset.UtcNow));
            throw new BackoffException(ex);
        }
    }

    private string PartialDirectory(string? owner) => Path.Combine(_storage.CacheDir, "transfers", owner ?? "shared");

    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal async Task DownloadVerifiedFileAsync(IReadOnlyList<string> urls, string destPath, string? sha1, string? sha512,
        long? size, Action<long, long?>? onBytes, CancellationToken ct, Action<string, Exception>? onRetry = null,
        bool overwrite = true, string? sha256 = null)
    {
        if (urls.Count == 0) throw new InvalidDataException("缺少下载地址");
        if (size is <= 0) size = null;
        bool verified = !string.IsNullOrEmpty(sha1) || !string.IsNullOrEmpty(sha512) || !string.IsNullOrEmpty(sha256);
        string identity = verified ? $"{sha1}|{sha512}|{sha256}" : urls[^1] + "|" + Path.GetFullPath(destPath);
        string key = Fingerprint(identity);
        var gate = _fileLocks[Convert.ToInt32(key[..4], 16) % _fileLocks.Length];
        await gate.WaitAsync(ct);
        try
        {
            if (verified && File.Exists(destPath) && VerifyFile(destPath, sha1, sha512, size, ct, sha256))
            {
                long length = new FileInfo(destPath).Length;
                onBytes?.Invoke(length, length);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            string directory = PartialDirectory(_currentTask.Value);
            Directory.CreateDirectory(directory);
            string prefix = Path.Combine(directory, key);
            string whole = prefix + ".part";
            string metadata = prefix + ".json";
            string[] pieces = Enumerable.Range(0, 4).Select(i => prefix + ".p" + i).ToArray();
            void Reset()
            {
                foreach (string path in pieces.Append(whole).Append(metadata)) File.Delete(path);
            }

            Exception? failure = null;
            var remaining = _health.Order(urls).ToList();
            var retryable = new List<string>();
            bool retried = false;
            bool serial = File.Exists(whole);
            while (remaining.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                int index = remaining.FindIndex(u => _health.WaitMs(u) == 0);
                if (index < 0) index = remaining.Select((u, i) => (u, i)).MinBy(x => _health.WaitMs(x.u)).i;
                string url = remaining[index];
                remaining.RemoveAt(index);
                bool waitForCooling = !remaining.Any(u => _health.WaitMs(u) == 0);
                string host = new Uri(url).Host;
                string actualHost = host;
                void ObserveHost(string value) => Interlocked.Exchange(ref actualHost, value);
                var watch = Stopwatch.StartNew();
                long networkBytes = 0;
                long firstByte = -1;
                void RecordRead(int n)
                {
                    Interlocked.Add(ref networkBytes, n);
                    Interlocked.CompareExchange(ref firstByte, watch.ElapsedMilliseconds, -1);
                }
                try
                {
                    bool assembled = false;
                    if (!serial && verified && size >= ParallelThreshold)
                    {
                        try
                        {
                            await DownloadPiecesAsync(url, whole, pieces, size.Value, onBytes, RecordRead, ct, waitForCooling, ObserveHost);
                            assembled = true;
                        }
                        catch (RangeUnavailable) { serial = true; }
                    }
                    if (!assembled)
                    {
                        Representation? saved = null;
                        if (File.Exists(metadata))
                        {
                            try { saved = JsonSerializer.Deserialize<Representation>(await File.ReadAllTextAsync(metadata, ct)); }
                            catch (JsonException) { File.Delete(metadata); }
                        }
                        long offset = File.Exists(whole) ? new FileInfo(whole).Length : 0;
                        bool same = saved?.Url == url && EntityTagHeaderValue.TryParse(saved.Tag, out var tag) && !tag.IsWeak;
                        if (!verified && !same || size is long total && offset > total) offset = 0;
                        if (!(verified && size.HasValue && offset == size))
                            await ReadRangeAsync(url, whole, offset, null, size ?? (same && offset > 0 ? saved!.Length : null), same ? saved!.Tag : null,
                                (etag, length) =>
                                {
                                    try
                                    {
                                        if (etag is { IsWeak: false }) File.WriteAllText(metadata, JsonSerializer.Serialize(new Representation(url, etag.ToString(), length)));
                                        else if (File.Exists(metadata)) File.Delete(metadata);
                                    }
                                    catch (IOException) { }
                                }, onBytes, RecordRead, ct, waitForCooling, ObserveHost);
                    }
                    if (!VerifyFile(whole, sha1, sha512, size, ct, sha256))
                    {
                        Reset();
                        serial = true;
                        throw new InvalidDataException("下载文件的摘要或大小与清单不符");
                    }
                    ct.ThrowIfCancellationRequested();
                    await PublishFileAsync(whole, destPath, overwrite, ct);
                    Reset();
                    if (networkBytes > 0) _health.Success(url, watch.ElapsedMilliseconds, networkBytes, firstByte);
                    if (failure != null) _storage.Log($"下载续传或回退成功 [{host}] {Path.GetFileName(destPath)}");
                    ReportSource(actualHost);
                    return;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TimeoutException or InvalidDataException)
                {
                    failure = ex;
                    onRetry?.Invoke(host, ex);
                    if (ex is not (BackoffException or NetworkHealth.CoolingException)) _health.Failure(url, ex);
                    if (ex is InvalidDataException or HttpRequestException { StatusCode: HttpStatusCode.RequestedRangeNotSatisfiable })
                    {
                        Reset();
                        serial = true;
                    }
                    if (ex is not HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized })
                        retryable.Add(url);
                    _storage.Log($"下载重试 [{host}] {Path.GetFileName(destPath)}：{ex.Message}");
                    if (remaining.Count == 0 && !retried && retryable.Count != 0)
                    {
                        remaining.Add(retryable.MinBy(u => _health.WaitMs(u))!);
                        retried = true;
                    }
                }
            }
            throw new IOException($"无法下载 {Path.GetFileName(destPath)}（{failure?.Message}）", failure);
        }
        finally { gate.Release(); }
    }

    private static async Task PublishFileAsync(string source, string dest, bool overwrite, CancellationToken ct)
    {
        if (string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(dest), StringComparison.OrdinalIgnoreCase))
        {
            File.Move(source, dest, overwrite);
            return;
        }
        string temp = dest + "." + Guid.NewGuid().ToString("N") + ".dcltmp";
        try
        {
            await using (var input = File.OpenRead(source))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                await input.CopyToAsync(output, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, dest, overwrite);
        }
        finally { File.Delete(temp); }
    }

    private void ReportSource(string host)
    {
        if (_currentTask.Value is not string id || string.IsNullOrWhiteSpace(host)) return;
        bool changed = false;
        lock (_lock)
        {
            var entry = _tasks.FirstOrDefault(t => t.Id == id);
            if (entry != null && !string.Equals(entry.Source, host, StringComparison.OrdinalIgnoreCase))
            {
                entry.Source = host;
                changed = true;
            }
        }
        if (changed) TasksChanged?.Invoke();
    }

    private async Task DownloadPiecesAsync(string url, string whole, string[] pieces, long length,
        Action<long, long?>? progress, Action<int> read, CancellationToken ct, bool waitForCooling, Action<string> observeHost)
    {
        using var batch = CancellationTokenSource.CreateLinkedTokenSource(ct);
        long width = (length + pieces.Length - 1) / pieces.Length;
        long received = 0, lastReport = Environment.TickCount64;
        Exception? first = null;
        string? objectTag = null;
        var tasks = pieces.Select(async (file, i) =>
        {
            long start = i * width;
            long end = Math.Min(length, start + width) - 1;
            long saved = File.Exists(file) ? new FileInfo(file).Length : 0;
            if (saved > end - start + 1) { File.Delete(file); saved = 0; }
            Interlocked.Add(ref received, saved);
            if (start + saved > end) return;
            try
            {
                await ReadRangeAsync(url, file, start + saved, end, length, null, (tag, _) =>
                {
                    if (tag is not { IsWeak: false }) return;
                    string value = tag.ToString();
                    if (Interlocked.CompareExchange(ref objectTag, value, null) is string previous && previous != value)
                        throw new InvalidDataException("下载期间文件版本已变化");
                }, null, n =>
                {
                    read(n);
                    long done = Interlocked.Add(ref received, n);
                    long now = Environment.TickCount64, previous = Volatile.Read(ref lastReport);
                    if (now - previous >= 160 && Interlocked.CompareExchange(ref lastReport, now, previous) == previous)
                        progress?.Invoke(done, length);
                }, batch.Token, waitForCooling, observeHost);
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref first, ex, null);
                batch.Cancel();
                throw;
            }
        }).ToArray();
        try { await Task.WhenAll(tasks); }
        catch { ct.ThrowIfCancellationRequested(); System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first!).Throw(); }

        // Merge only after all four intervals are present. A crash during merge must never become a resumable prefix.
        string merged = whole + ".merge";
        try
        {
            await using (var output = new FileStream(merged, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
                foreach (var file in pieces)
                {
                    await using var input = File.OpenRead(file);
                    await input.CopyToAsync(output, ct);
                }
            File.Move(merged, whole, true);
        }
        finally { File.Delete(merged); }
        progress?.Invoke(length, length);
    }
}
