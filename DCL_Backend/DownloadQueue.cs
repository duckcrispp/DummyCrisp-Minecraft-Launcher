using System.Text.Json;
using System.Text.RegularExpressions;

namespace DCL_Launcher;

internal sealed record DownloadRecipe(string Kind, string Target = "", string Instance = "", string Version = "",
    string Url = "", string Digest = "", string Loader = "", string Vendor = "", int Major = 0,
    string Label = "", ResourceFile? File = null, bool Custom = false, string Mirror = "");

public sealed partial class DownloadManager
{
    private sealed record SavedJob(DownloadTaskEntry Entry, DownloadRecipe Recipe, string? UniqueKey);
    private readonly Dictionary<string, SavedJob> _jobs = new();
    private long _nextCheckpoint;
    private Func<DownloadRecipe, DownloadTaskEntry, IProgress<(int Progress, string Speed)>, CancellationToken, Task>? _executeJob;
    private string QueuePath => Path.Combine(_storage.ConfigDir, "download-queue.json");

    internal void RestoreQueue(Func<DownloadRecipe, DownloadTaskEntry, IProgress<(int Progress, string Speed)>, CancellationToken, Task> execute)
    {
        _executeJob = execute;
        if (!File.Exists(QueuePath)) return;
        try
        {
            var jobs = JsonSerializer.Deserialize<List<SavedJob>>(File.ReadAllText(QueuePath));
            foreach (var job in (jobs ?? []).AsEnumerable().Reverse())
            {
                if (!Regex.IsMatch(job.Entry.Id, "^dl-[0-9a-f]{9}$") || _jobs.ContainsKey(job.Entry.Id)) continue;
                job.Entry.Status = "paused";
                job.Entry.Speed = "可继续下载";
                RegisterJob(job, true);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            _storage.Log($"读取下载队列失败：{ex.Message}");
        }
    }

    internal DownloadTaskEntry QueueJob(string name, string sub, string icon, string source, string size, DownloadRecipe recipe, string? key = null)
    {
        var entry = CreateEntry(name, sub, icon, source, size);
        return RegisterJob(new SavedJob(entry, recipe, key), false);
    }

    private DownloadTaskEntry RegisterJob(SavedJob job, bool restore) =>
        EnqueueCore(job.Entry, (entry, progress, ct) => _executeJob!(job.Recipe, entry, progress, ct),
            job.UniqueKey, job, restore);

    private void SaveQueueLocked()
    {
        string temp = QueuePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(_tasks.Where(t => _jobs.ContainsKey(t.Id)).Select(t => _jobs[t.Id])));
            File.Move(temp, QueuePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _storage.Log($"保存下载队列失败：{ex.Message}"); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private void DropPartial(string id)
    {
        if (!Regex.IsMatch(id, "^dl-[0-9a-f]{9}$")) return;
        try
        {
            string directory = PartialDirectory(id);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _storage.Log($"清理下载缓存失败：{ex.Message}"); }
    }
}
