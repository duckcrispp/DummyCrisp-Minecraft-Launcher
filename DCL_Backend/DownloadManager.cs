using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Security.Cryptography;

namespace DCL_Launcher;

public sealed partial class DownloadManager
{
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(12);
    private readonly HttpClient _http;
    private readonly NetworkHealth _health = new();
    internal (int Ping, double Available, double Mbps) Metrics(string url) => _health.Metrics(url);
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
        string? uniqueKey = null) => EnqueueCore(CreateEntry(name, sub, icon, source, size), work, uniqueKey, null, false);

    private static DownloadTaskEntry CreateEntry(string name, string sub, string icon, string source, string size) => new DownloadTaskEntry
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

    private DownloadTaskEntry EnqueueCore(DownloadTaskEntry entry,
        Func<DownloadTaskEntry, IProgress<(int Progress, string Speed)>, CancellationToken, Task> work,
        string? uniqueKey, SavedJob? job, bool restore)
    {
        async Task RunAsync(CancellationTokenSource cts)
        {
            _currentTask.Value = entry.Id;
            var ct = cts.Token;
            var progress = new InlineProgress(p =>
            {
                lock (_lock)
                {
                    if (entry.Status != "downloading" || ct.IsCancellationRequested || !_runners.ContainsKey(entry.Id))
                        return;
                    entry.Progress = Math.Max(entry.Progress, Math.Clamp(p.Progress, 0, 100));
                    entry.Speed = p.Speed;
                    if (_jobs.ContainsKey(entry.Id) && Environment.TickCount64 >= _nextCheckpoint)
                    {
                        _nextCheckpoint = Environment.TickCount64 + 5000;
                        SaveQueueLocked();
                    }
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
                    _jobs.Remove(entry.Id);
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
                    if (!_runners.ContainsKey(entry.Id)) DropPartial(entry.Id);
                    SaveQueueLocked();
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
            if (job != null) _jobs.Add(entry.Id, job);
            if (!restore) { SaveQueueLocked(); StartRunnerLocked(entry.Id); }
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
                target.Speed = "恢复中...";
                StartRunnerLocked(taskId);
            }
            SaveQueueLocked();
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
            _jobs.Remove(taskId);
            SaveQueueLocked();
            if (_active.TryGetValue(taskId, out var cts)) cts.Cancel();
            else
            {
                var key = _uniqueTasks.FirstOrDefault(task => ReferenceEquals(task.Value, entry)).Key;
                if (key != null) _uniqueTasks.Remove(key);
                DropPartial(taskId);
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

    public static string ComputeSha1Hex(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        byte[] hash = SHA1.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<int> PingHostAsync(string url, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(2500);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (_health.WaitMs(url) > 0) return 9999;
            using var lease = await _health.EnterAsync(url, timeout.Token, false);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 255);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            CheckStatus(response, url);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var bytes = new byte[256];
            int count = await stream.ReadAsync(bytes, timeout.Token);
            string prefix = System.Text.Encoding.UTF8.GetString(bytes, 0, count).TrimStart('\uFEFF',' ','\t','\r','\n');
            if (!prefix.StartsWith('{') && !prefix.StartsWith('[')) throw new InvalidDataException("探测节点未返回 JSON");
            int ms = (int)Math.Max(1, sw.ElapsedMilliseconds);
            _health.Success(url, ms, 0);
            return ms;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
        {
            if (ex is not (BackoffException or NetworkHealth.CoolingException)) _health.Failure(url, ex);
            return 9999;
        }
    }
}
