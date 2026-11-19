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
    private async Task ReadRangeAsync(string url, string file, long offset, long? end, long? expected,
        string? tag, Action<EntityTagHeaderValue?, long?> headers, Action<long, long?>? progress, Action<int> record, CancellationToken ct, bool waitForCooling, Action<string> observeHost)
    {
        using var lease = await _health.EnterAsync(url, ct, waitForCooling);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(StallTimeout);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.AcceptEncoding.ParseAdd("identity");
            if (end.HasValue || offset > 0)
            {
                req.Headers.Range = new RangeHeaderValue(offset, end);
                if (tag != null) req.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(tag));
            }
            using var response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, idle.Token);
            CheckStatus(response, url);
            observeHost((response.RequestMessage?.RequestUri ?? req.RequestUri)!.Host);
            long? count = response.Content.Headers.ContentLength;
            long? total = expected ?? count;
            bool append = offset > 0;
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (range?.Unit != "bytes" || range.From != offset || range.Length is not long length ||
                    range.To != (end ?? length - 1) || range.To >= length || (expected.HasValue && expected != length) ||
                    (count.HasValue && count != range.To - offset + 1) ||
                    (tag != null && response.Headers.ETag is { } returned && returned.ToString() != tag))
                    throw new InvalidDataException("服务器返回了错误的文件范围或对象版本");
                total = length;
                count = range.To - offset + 1;
            }
            else
            {
                if (end.HasValue) throw new RangeUnavailable();
                if (response.StatusCode != HttpStatusCode.OK || (count.HasValue && expected.HasValue && count != expected))
                    throw new InvalidDataException("服务器返回了错误的文件长度或状态");
                offset = 0;
                append = false;
                count ??= expected;
            }

            await using var remote = await response.Content.ReadAsStreamAsync(idle.Token);
            await using var local = new FileStream(file, append && File.Exists(file) ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            headers(response.Headers.ETag ?? (response.StatusCode == HttpStatusCode.PartialContent && tag != null ? EntityTagHeaderValue.Parse(tag) : null), total);
            var buffer = ArrayPool<byte>.Shared.Rent(131072);
            long bytes = 0;
            var timer = Stopwatch.StartNew();
            try
            {
                while (true)
                {
                    idle.CancelAfter(StallTimeout);
                    int n;
                    try { n = await remote.ReadAsync(buffer.AsMemory(), idle.Token); }
                    catch (IOException ex) { throw new HttpRequestException("传输连接中断", ex); }
                    if (n == 0) break;
                    if (count is long limit && n > limit - bytes) throw new InvalidDataException("服务器返回了超出范围的数据");
                    await local.WriteAsync(buffer.AsMemory(0, n), ct);
                    bytes += n;
                    record(n);
                    if (timer.ElapsedMilliseconds >= 160)
                    {
                        progress?.Invoke(offset + bytes, total);
                        timer.Restart();
                    }
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            if (count.HasValue && count != bytes) throw new HttpRequestException($"传输提前结束（{bytes}/{count} 字节）");
            progress?.Invoke(offset + bytes, total ?? offset + bytes);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"连续 {StallTimeout.TotalSeconds:0} 秒未收到下载数据");
        }
    }

    internal static bool VerifyFile(string path, string? sha1, string? sha512, long? size, CancellationToken ct, string? sha256 = null)
    {
        ct.ThrowIfCancellationRequested();
        if (size is > 0 && new FileInfo(path).Length != size) return false;
        using var stream = File.OpenRead(path);
        bool Matches(string? digest, HashAlgorithmName algorithm)
        {
            if (string.IsNullOrEmpty(digest)) return true;
            stream.Position = 0;
            using var hash = IncrementalHash.CreateHash(algorithm);
            var buffer = ArrayPool<byte>.Shared.Rent(131072);
            try
            {
                int read;
                while ((read = stream.Read(buffer)) > 0) { ct.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
                return Convert.ToHexString(hash.GetHashAndReset()).Equals(digest, StringComparison.OrdinalIgnoreCase);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        return Matches(sha1, HashAlgorithmName.SHA1) && Matches(sha512, HashAlgorithmName.SHA512) && Matches(sha256, HashAlgorithmName.SHA256);
    }

    internal async Task<string> GetDownloadTextAsync(IReadOnlyList<string> urls, CancellationToken ct,
        Action<string>? validate = null, string? sha1 = null)
    {
        string[] ordered = _health.Order(urls);
        using var winner = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task<string> Read(string url, int index)
        {
            if (index > 0) await Task.Delay(650 * index, winner.Token);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(winner.Token);
            budget.CancelAfter(TimeSpan.FromSeconds(7));
            var clock = Stopwatch.StartNew();
            try
            {
                using var lease = await _health.EnterAsync(url, budget.Token);
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, budget.Token);
                CheckStatus(response, url);
                long firstByte = clock.ElapsedMilliseconds;
                byte[] bytes = await response.Content.ReadAsByteArrayAsync(budget.Token);
                if (!string.IsNullOrEmpty(sha1) && !Convert.ToHexString(SHA1.HashData(bytes)).Equals(sha1, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("版本清单 SHA-1 校验失败");
                string text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
                validate?.Invoke(text);
                _health.Success(url, clock.ElapsedMilliseconds, bytes.Length, firstByte);
                return text;
            }
            catch (Exception ex) when (!winner.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException or IOException or InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException)
            {
                if (ex is not BackoffException) _health.Failure(url, ex);
                throw new IOException($"清单请求失败 [{new Uri(url).Host}]：{ex.Message}", ex);
            }
        }
        var attempts = ordered.Select(Read).ToList();
        Exception? last = null;
        try
        {
            while (attempts.Count > 0)
            {
                var done = await Task.WhenAny(attempts);
                attempts.Remove(done);
                try { return await done; }
                catch (IOException ex) { last = ex; }
            }
            throw new IOException($"无法获取清单（{last?.Message}）", last);
        }
        finally
        {
            winner.Cancel();
            foreach (var attempt in attempts)
                _ = attempt.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }
}
