using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Security.Cryptography;

namespace DCL_Launcher;

public sealed class DownloadManager
{
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(12);
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, long> _mirrorCooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly StorageService _storage;
    private readonly object _lock = new();
    private readonly List<DownloadTaskEntry> _tasks = new();
    private readonly Dictionary<string, CancellationTokenSource> _active = new();
    private readonly Dictionary<string, Func<CancellationTokenSource, Task>> _runners = new();
    private readonly Dictionary<string, DownloadTaskEntry> _uniqueTasks = new();

    public event Action? TasksChanged;

    public DownloadManager(StorageService storage, HttpClient http)
    {
        _storage = storage;
        _http = http;
    }

    public List<DownloadTaskEntry> GetTasksSnapshot()
    {
        lock (_lock)
        {
            return _tasks.Select(t => new DownloadTaskEntry
            {
                Id = t.Id,
                Name = t.Name,
                Sub = t.Sub,
                Icon = t.Icon,
                Source = t.Source,
                Size = t.Size,
                Speed = t.Speed,
                Progress = t.Progress,
                Status = t.Status
            }).ToList();
        }
    }

    public DownloadTaskEntry Enqueue(
        string name,
        string sub,
        string icon,
        string source,
        string size,
        Func<DownloadTaskEntry, IProgress<(int Progress, string Speed)>, CancellationToken, Task> work,
        string? uniqueKey = null)
    {
        var entry = new DownloadTaskEntry
        {
            Id = $"dl-{Guid.NewGuid():N}"[..12],
            Name = name,
            Sub = sub,
            Icon = string.IsNullOrWhiteSpace(icon) ? "assets/mc-icons/grass.png" : icon,
            Source = source,
            Size = string.IsNullOrWhiteSpace(size) ? "计算中..." : size,
            Speed = "准备中",
            Progress = 0,
            Status = "downloading"
        };

        async Task RunAsync(CancellationTokenSource cts)
        {
            var ct = cts.Token;
            var progress = new InlineProgress(p =>
            {
                lock (_lock)
                {
                    if (entry.Status != "downloading" || ct.IsCancellationRequested || !_runners.ContainsKey(entry.Id))
                        return;
                    entry.Progress = Math.Max(entry.Progress, Math.Clamp(p.Progress, 0, 100));
                    entry.Speed = p.Speed;
                }
                TasksChanged?.Invoke();
            });

            try
            {
                ct.ThrowIfCancellationRequested();
                _storage.Log($"开始下载 [{entry.Id}/{entry.Name}] · {entry.Source}");
                await work(entry, progress, ct);
                _storage.Log($"下载完成 [{entry.Id}/{entry.Name}]");
                lock (_lock)
                {
                    entry.Progress = 100;
                    entry.Status = "completed";
                    entry.Speed = entry.Size;
                    _runners.Remove(entry.Id);
                    TrimCompletedLocked();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // A resume requested during cleanup will start a new runner in finally.
            }
            catch (Exception ex)
            {
                _storage.Log($"下载任务失败 [{entry.Id}/{entry.Name}]：{ex}");
                lock (_lock)
                {
                    entry.Status = "paused";
                    entry.Speed = ex is OperationCanceledException ? "网络请求超时，可重试" : ex.Message;
                }
            }
            finally
            {
                lock (_lock)
                {
                    _active.Remove(entry.Id);
                    cts.Dispose();
                    if (_runners.ContainsKey(entry.Id) && entry.Status == "downloading")
                        StartRunnerLocked(entry.Id);
                    else if (uniqueKey != null && !_runners.ContainsKey(entry.Id))
                        _uniqueTasks.Remove(uniqueKey);
                }
                TasksChanged?.Invoke();
            }
        }

        lock (_lock)
        {
            if (uniqueKey != null)
            {
                if (_uniqueTasks.ContainsKey(uniqueKey))
                    throw new InvalidOperationException("该构建已在下载队列中，可前往队列查看或重试。");
                _uniqueTasks.Add(uniqueKey, entry);
            }
            _tasks.Insert(0, entry);
            TrimCompletedLocked();
            _runners.Add(entry.Id, RunAsync);
            StartRunnerLocked(entry.Id);
        }
        TasksChanged?.Invoke();
        return entry;
    }

    private void StartRunnerLocked(string taskId)
    {
        if (_active.ContainsKey(taskId)) return;
        var cts = new CancellationTokenSource();
        var runner = _runners[taskId];
        _active.Add(taskId, cts);
        _ = Task.Run(() => runner(cts));
    }

    public DownloadTaskEntry? TogglePauseResume(string taskId)
    {
        DownloadTaskEntry? target;
        lock (_lock)
        {
            target = _tasks.FirstOrDefault(t => t.Id == taskId);
            if (target == null || target.Status == "completed") return target;
            if (target.Status == "downloading")
            {
                target.Status = "paused";
                target.Speed = "已暂停";
                if (_active.TryGetValue(taskId, out var cts)) cts.Cancel();
            }
            else
            {
                target.Status = "downloading";
                target.Progress = 0;
                target.Speed = "恢复中...";
                StartRunnerLocked(taskId);
            }
        }
        TasksChanged?.Invoke();
        return target;
    }

    public bool DeleteTask(string taskId)
    {
        lock (_lock)
        {
            int index = _tasks.FindIndex(t => t.Id == taskId);
            if (index < 0) return false;
            var entry = _tasks[index];
            _tasks.RemoveAt(index);
            _runners.Remove(taskId);
            if (_active.TryGetValue(taskId, out var cts)) cts.Cancel();
            else
            {
                var key = _uniqueTasks.FirstOrDefault(task => ReferenceEquals(task.Value, entry)).Key;
                if (key != null) _uniqueTasks.Remove(key);
            }
        }
        TasksChanged?.Invoke();
        return true;
    }

    private sealed class InlineProgress(Action<(int Progress, string Speed)> report) : IProgress<(int Progress, string Speed)>
    {
        public void Report((int Progress, string Speed) value) => report(value);
    }

    /// <summary>Retains at most the 10 newest completed downloads (plus all active ones).</summary>
    private void TrimCompletedLocked()
    {
        int completedCount = 0;
        for (int i = 0; i < _tasks.Count; i++)
        {
            if (_tasks[i].Status != "completed") continue;
            completedCount++;
            if (completedCount > 10)
            {
                _tasks.RemoveAt(i);
                i--;
            }
        }
    }

    public Task DownloadFileWithSha1Async(
        string url,
        string destPath,
        string? expectedSha1,
        Action<long, long?>? onBytes,
        CancellationToken ct) => DownloadVerifiedFileAsync([url], destPath, expectedSha1, null, null, onBytes, ct);

    internal async Task DownloadVerifiedFileAsync(IReadOnlyList<string> urls, string destPath, string? sha1, string? sha512,
        long? size, Action<long, long?>? onBytes, CancellationToken ct, Action<string, Exception>? onRetry = null, bool overwrite = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        bool hasHash = !string.IsNullOrWhiteSpace(sha1) || !string.IsNullOrWhiteSpace(sha512);
        if (hasHash && File.Exists(destPath) && VerifyFile(destPath, sha1, sha512, size, ct))
        {
            var length = new FileInfo(destPath).Length;
            onBytes?.Invoke(length, length);
            return;
        }

        string tempPath = destPath + "." + Guid.NewGuid().ToString("N") + ".dcltmp";
        var candidates = urls.Distinct().ToArray();
        var unavailable = new HashSet<string>(StringComparer.Ordinal);
        string? lastAttempt = null;
        Exception? lastError = null;
        EntityTagHeaderValue? validator = null;
        string? previousUrl = null;
        long? previousLength = null;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            foreach (string url in candidates.Concat(candidates.TakeLast(1)))
            {
                ct.ThrowIfCancellationRequested();
                string host = new Uri(url).Host;
                if (unavailable.Contains(url) ||
                    (candidates.Length > 1 && _mirrorCooldowns.GetValueOrDefault(host) > Environment.TickCount64))
                    continue;
                if (url == lastAttempt) await Task.Delay(150, ct);
                try
                {
                    bool sameSource = url == previousUrl;
                    long offset = File.Exists(tempPath) && (hasHash || sameSource && validator is { IsWeak: false })
                        ? new FileInfo(tempPath).Length : 0;
                    bool usedParallel = false;
                    if (offset == 0 && (size == null || size >= 4L * 1024 * 1024))
                    {
                        usedParallel = await TryParallelTransferAsync(url, tempPath, size, onBytes, ct);
                    }
                    if (!usedParallel)
                    {
                        await TransferAsync(url, tempPath, offset, sameSource ? validator : null, size is > 0 ? size : previousLength, buffer, onBytes,
                            (tag, length) => { previousUrl = url; validator = tag; previousLength = length; }, ct);
                    }

                    ct.ThrowIfCancellationRequested();
                    if (!VerifyFile(tempPath, sha1, sha512, size, ct))
                        throw new InvalidDataException("内容校验失败");
                    ct.ThrowIfCancellationRequested();

                    File.Move(tempPath, destPath, overwrite);
                    if (lastAttempt != null) _storage.Log($"下载续传或回退成功 [{host}] {Path.GetFileName(destPath)}");
                    return;
                }
                catch (Exception ex) when (IsTransferFailure(ex))
                {
                    lastError = ex;
                    onRetry?.Invoke(host, ex);
                    if (ex is InvalidDataException || ex is HttpRequestException { StatusCode: HttpStatusCode.RequestedRangeNotSatisfiable })
                    {
                        if (File.Exists(tempPath)) File.Delete(tempPath);
                        previousLength = null;
                        validator = null;
                    }
                    _storage.Log($"下载重试 [{host}] {Path.GetFileName(destPath)}：{ex.Message}");
                    lastAttempt = url;
                    if (!CanRetry(ex)) unavailable.Add(url);
                    if (candidates.Length > 1 && DownloadRoutes.IsMirror(host) &&
                        ex is TimeoutException or HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable })
                    {
                        _mirrorCooldowns[host] = Environment.TickCount64 + 45000;
                    }
                }
            }

            throw new IOException($"无法下载 {Path.GetFileName(destPath)}（{lastError?.Message ?? "没有可用下载地址"}）", lastError);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    internal async Task<string> GetDownloadTextAsync(IReadOnlyList<string> urls, CancellationToken ct, Action<string>? validate = null)
    {
        Exception? lastError = null;
        foreach (string url in urls.Distinct())
        {
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            requestTimeout.CancelAfter(StallTimeout);
            try
            {
                string text = await _http.GetStringAsync(url, requestTimeout.Token);
                validate?.Invoke(text);
                return text;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = new TimeoutException("清单请求超时");
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException)
            {
                lastError = ex;
            }
            _storage.Log($"下载清单失败 [{new Uri(url).Host}]：{lastError.Message}");
        }
        throw new IOException($"无法下载清单（{lastError?.Message}）", lastError);
    }

    private async Task TransferAsync(string url, string tempPath, long offset, EntityTagHeaderValue? validator, long? expectedSize,
        byte[] buffer, Action<long, long?>? onBytes, Action<EntityTagHeaderValue?, long?> onHeaders, CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(StallTimeout);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("DCL-Launcher/1.0");
            if (offset > 0)
            {
                req.Headers.Range = new RangeHeaderValue(offset, null);
                if (validator is { IsWeak: false }) req.Headers.IfRange = new RangeConditionHeaderValue(validator);
            }
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, idle.Token);
            resp.EnsureSuccessStatusCode();

            long? total = resp.Content.Headers.ContentLength;
            if (resp.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = resp.Content.Headers.ContentRange;
                if (offset == 0 || range == null || range.From != offset || range.To == null || range.Length is not long length ||
                    range.To != length - 1 || (expectedSize is > 0 && length != expectedSize) ||
                    (total.HasValue && total != length - offset))
                    throw new InvalidDataException("服务器返回了无效的续传范围");
                total = length;
            }
            else
            {
                offset = 0;
            }

            onHeaders(resp.Headers.ETag, total);
            await using var remote = await resp.Content.ReadAsStreamAsync(idle.Token);
            await using var local = new FileStream(tempPath, offset > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long received = offset;
            var progressTimer = Stopwatch.StartNew();
            while (true)
            {
                idle.CancelAfter(StallTimeout);
                int read;
                try { read = await remote.ReadAsync(buffer, idle.Token); }
                catch (IOException ex) { throw new HttpRequestException("传输连接中断", ex); }
                if (read == 0) break;

                await local.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                if (progressTimer.ElapsedMilliseconds >= 120)
                {
                    onBytes?.Invoke(received, total);
                    progressTimer.Restart();
                }
            }

            if (total.HasValue && received != total)
                throw new HttpRequestException($"传输提前结束（{received}/{total} 字节）");
            onBytes?.Invoke(received, total ?? received);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"连续 {StallTimeout.TotalSeconds:0} 秒未收到下载数据");
        }
    }

    private async Task<bool> TryParallelTransferAsync(string url, string tempPath, long? expectedSize,
        Action<long, long?>? onBytes, CancellationToken ct)
    {
        const long minParallelSize = 4L * 1024 * 1024;
        const int segments = 4;

        long totalLength = expectedSize ?? 0;
        bool supportsRanges = false;
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromSeconds(5));
            using var headReq = new HttpRequestMessage(HttpMethod.Head, url);
            headReq.Headers.UserAgent.ParseAdd("DCL-Launcher/1.0");
            using var headResp = await _http.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, probeCts.Token);
            if (!headResp.IsSuccessStatusCode) return false;
            if (headResp.Content.Headers.ContentLength is long cl && cl > 0)
                totalLength = cl;
            supportsRanges = headResp.Headers.AcceptRanges.Any(r => r.Equals("bytes", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }

        if (!supportsRanges || totalLength < minParallelSize) return false;

        long chunkSize = (totalLength + segments - 1) / segments;
        long totalReceived = 0;
        long lastReportTick = Environment.TickCount64;
        using var fileHandle = File.OpenHandle(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
            FileOptions.Asynchronous | FileOptions.RandomAccess, preallocationSize: totalLength);
        RandomAccess.SetLength(fileHandle, totalLength);

        try
        {
            var tasks = new Task[segments];
            for (int i = 0; i < segments; i++)
            {
                long start = i * chunkSize;
                if (start >= totalLength)
                {
                    tasks[i] = Task.CompletedTask;
                    continue;
                }
                long end = Math.Min(totalLength - 1, start + chunkSize - 1);
                tasks[i] = Task.Run(async () =>
                {
                    byte[] segBuf = ArrayPool<byte>.Shared.Rent(128 * 1024);
                    try
                    {
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        idle.CancelAfter(StallTimeout);
                        using var req = new HttpRequestMessage(HttpMethod.Get, url);
                        req.Headers.UserAgent.ParseAdd("DCL-Launcher/1.0");
                        req.Headers.Range = new RangeHeaderValue(start, end);
                        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, idle.Token);
                        if (resp.StatusCode != HttpStatusCode.PartialContent)
                            throw new NotSupportedException("Server did not honor byte range.");
                        await using var remote = await resp.Content.ReadAsStreamAsync(idle.Token);
                        long pos = start;
                        while (pos <= end)
                        {
                            idle.CancelAfter(StallTimeout);
                            int read = await remote.ReadAsync(segBuf, idle.Token);
                            if (read == 0) break;
                            await RandomAccess.WriteAsync(fileHandle, segBuf.AsMemory(0, read), pos, ct);
                            pos += read;
                            long rec = Interlocked.Add(ref totalReceived, read);
                            long now = Environment.TickCount64;
                            long prev = Volatile.Read(ref lastReportTick);
                            if (now - prev >= 120 && Interlocked.CompareExchange(ref lastReportTick, now, prev) == prev)
                            {
                                onBytes?.Invoke(rec, totalLength);
                            }
                        }
                        if (pos != end + 1)
                            throw new HttpRequestException($"分段传输提前结束（{pos - start}/{end - start + 1}）");
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(segBuf);
                    }
                }, ct);
            }
            await Task.WhenAll(tasks);
            onBytes?.Invoke(totalLength, totalLength);
            return true;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsTransferFailure(Exception ex) => ex is TimeoutException or HttpRequestException or InvalidDataException;

    private static bool CanRetry(Exception ex) => ex is TimeoutException or InvalidDataException ||
        ex is HttpRequestException http && (http.StatusCode == null ||
            (int)http.StatusCode is 408 or 416 or 429 or >= 500);

    private static bool VerifyFile(string path, string? sha1, string? sha512, long? size, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (size is > 0 && new FileInfo(path).Length != size) return false;
        if (!string.IsNullOrWhiteSpace(sha1) && !ComputeSha1Hex(path).Equals(sha1, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(sha512)) return true;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA512.HashData(stream)).Equals(sha512, StringComparison.OrdinalIgnoreCase);
    }

    public static string ComputeSha1Hex(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        byte[] hash = SHA1.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<int> PingHostAsync(string url, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            req.Headers.UserAgent.ParseAdd("DCL-Launcher/1.0");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(2500);
            using var resp = await _http.SendAsync(req, cts.Token);
            sw.Stop();
            return (int)Math.Clamp(sw.ElapsedMilliseconds, 8, 1999);
        }
        catch
        {
            return 999;
        }
    }
}
