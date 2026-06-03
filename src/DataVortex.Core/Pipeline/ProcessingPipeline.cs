using System.Threading.Channels;
using System.IO;
using DataVortex.Core.Abstractions;
using DataVortex.Core.Models;
using Microsoft.Extensions.Logging;

namespace DataVortex.Core.Pipeline;

/// <summary>
/// Stage B of the pipeline: a bounded <see cref="Channel{T}"/> feeding its own worker pool, completely
/// independent of the download stage. Each worker extracts <c>*.txt</c> from archives, writes a JSON
/// metadata record, and raises <see cref="FileArchived"/>.
/// </summary>
public sealed class ProcessingPipeline
{
    private readonly IArchiveExtractor _extractor;
    private readonly IStorageService _storage;
    private readonly IMetricsService _metrics;
    private readonly DataVortex.Core.Passculture.PasscultureClient? _passClient;
    private readonly PauseGate _pauseGate;
    private readonly ILogger<ProcessingPipeline> _log;

    private readonly Channel<ProcessingJob> _channel;
    private readonly int _workerCount;
    private int _queueDepth;
    private Task[] _workers = Array.Empty<Task>();

    public event Action<ProcessingJob>? JobChanged;
    public event Action<FileRecord>? FileArchived;

    public int QueueDepth => Volatile.Read(ref _queueDepth);

    public ProcessingPipeline(
        IArchiveExtractor extractor, IStorageService storage, IMetricsService metrics,
        PauseGate pauseGate, ILogger<ProcessingPipeline> log, int workerCount, int queueCapacity,
        DataVortex.Core.Passculture.PasscultureClient? passClient = null)
    {
        _extractor = extractor;
        _storage = storage;
        _metrics = metrics;
        _pauseGate = pauseGate;
        _log = log;
        _passClient = passClient;
        _workerCount = Math.Max(1, workerCount);
        _channel = Channel.CreateBounded<ProcessingJob>(new BoundedChannelOptions(Math.Max(16, queueCapacity))
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public void Start(CancellationToken ct)
    {
        _workers = Enumerable.Range(0, _workerCount)
            .Select(i => Task.Run(() => WorkerLoop(i, ct), ct))
            .ToArray();
        _log.LogInformation("Processing pipeline started with {Workers} workers", _workerCount);
    }

    public async Task StopAsync()
    {
        _channel.Writer.TryComplete();
        try { await Task.WhenAll(_workers).ConfigureAwait(false); }
        catch { /* cancellation during shutdown */ }
    }

    public async ValueTask EnqueueAsync(ProcessingJob job, CancellationToken ct)
    {
        Interlocked.Increment(ref _queueDepth);
        _metrics.SetProcessingQueueDepth(QueueDepth);
        job.Status = ProcessingStatus.Queued;
        JobChanged?.Invoke(job);
        await _channel.Writer.WriteAsync(job, ct).ConfigureAwait(false);
    }

    private async Task WorkerLoop(int id, CancellationToken ct)
    {
        try
        {
            await foreach (var job in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _queueDepth);
                _metrics.SetProcessingQueueDepth(QueueDepth);
                await _pauseGate.WaitAsync(ct).ConfigureAwait(false);
                await ProcessJob(job, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogError(ex, "Processing worker {Id} crashed", id); }
    }

    private async Task ProcessJob(ProcessingJob job, CancellationToken ct)
    {
        try
        {
            job.Status = ProcessingStatus.Processing;
            JobChanged?.Invoke(job);

            var destDir = Path.Combine(
                _storage.Paths.Extracted,
                $"{Sanitize(job.ChannelTitle)}_{job.ChannelId}",
                job.MessageId.ToString());

            var result = await _extractor.ExtractTextFilesAsync(
                job.LocalPath, destDir, job.MessageText, () => _metrics.ReportExtractedFile(), ct).ConfigureAwait(false);

            job.Kind = result.Kind;
            job.ExtractedCount = result.ExtractedFiles.Count;
            job.Status = result.Errors.Count > 0
                ? ProcessingStatus.Failed
                : result.ExtractedFiles.Count == 0
                    ? ProcessingStatus.Ignored
                    : ProcessingStatus.Completed;
            if (result.Errors.Count > 0) job.Error = string.Join("; ", result.Errors);
            JobChanged?.Invoke(job);

            // Scan extracted text files for credentials (e.g. passculture entries)
            var credentials = new List<CredentialEntry>();
            foreach (var path in result.ExtractedFiles)
            {
                try
                {
                    var found = Extraction.CredentialScanner.ScanFile(path);
                    if (found?.Count > 0) credentials.AddRange(found);
                }
                catch
                {
                    // ignore scanning errors per-file
                }
            }

            var record = new FileRecord
            {
                SourceChannelId = job.ChannelId,
                SourceChannelTitle = job.ChannelTitle,
                MessageId = job.MessageId,
                OriginalFileName = job.FileName,
                SizeBytes = job.SizeBytes,
                MimeType = job.MimeType,
                ReceivedUtc = job.ReceivedUtc,
                ProcessedUtc = DateTime.UtcNow,
                DownloadPath = job.LocalPath,
                Kind = result.Kind,
                Status = job.Status,
                ExtractedTextFiles = result.ExtractedFiles.ToList(),
                Credentials = credentials,
                Error = job.Error
            };
            await _storage.SaveRecordAsync(record, ct).ConfigureAwait(false);

            // If passClient is available, automatically test credentials that are not yet tested
            if (_passClient is not null && record.Credentials is not null && record.Credentials.Count > 0)
            {
                for (int i = 0; i < record.Credentials.Count; i++)
                {
                    try
                    {
                        var cred = record.Credentials[i];
                        if (cred.Tested) continue;

                        // Try to reuse a previously-tested credential from existing metadata to avoid re-testing
                        try
                        {
                            var all = _storage.LoadRecords();
                            var found = all
                                .Where(r => r.Credentials is not null)
                                .SelectMany(r => r.Credentials!, (r, c) => c)
                                .FirstOrDefault(c => string.Equals(c.Username ?? string.Empty, cred.Username ?? string.Empty, StringComparison.Ordinal) 
                                                     && string.Equals(c.Password ?? string.Empty, cred.Password ?? string.Empty, StringComparison.Ordinal)
                                                     && string.Equals(c.Url ?? string.Empty, cred.Url ?? string.Empty, StringComparison.Ordinal)
                                                     && c.Tested);
                            if (found is not null)
                            {
                                var reused = cred with
                                {
                                    Tested = true,
                                    TestSuccess = found.TestSuccess,
                                    TestMessage = found.TestMessage,
                                    TestedUtc = found.TestedUtc,
                                    AccessToken = found.AccessToken,
                                    RefreshToken = found.RefreshToken,
                                    Credit = found.Credit,
                                    BirthDate = found.BirthDate,
                                    StatusCode = found.StatusCode
                                };
                                record.Credentials[i] = reused;
                                continue;
                            }
                        }
                        catch
                        {
                            // ignore reuse errors and fall back to live testing
                        }
                        var signin = await _passClient.SignInAsync(cred.Username ?? string.Empty, cred.Password ?? string.Empty, null, ct).ConfigureAwait(false);
                        var access = signin.AccessToken;
                        var refresh = signin.RefreshToken;
                        decimal? credit = null;
                        string? birth = null;
                        // If server returned 400, mark as tested but invalid and do not show in accounts list
                        if (signin.StatusCode == 400)
                        {
                            var updatedInvalid = cred with { Tested = true, TestSuccess = false, TestMessage = signin.Raw, TestedUtc = DateTime.UtcNow, StatusCode = signin.StatusCode };
                            record.Credentials[i] = updatedInvalid;
                            continue;
                        }

                        if (signin.Success && access is not null)
                        {
                            try
                            {
                                var me = await _passClient.GetMeAsync(access, ct).ConfigureAwait(false);
                                credit = me.DomainsCreditRemaining;
                                birth = me.BirthDate;
                            }
                            catch { }
                        }
                        var updated = cred with { Tested = true, TestSuccess = signin.Success, TestMessage = signin.Raw, TestedUtc = DateTime.UtcNow, AccessToken = access, RefreshToken = refresh, Credit = credit, BirthDate = birth };
                        record.Credentials[i] = updated;
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Automatic credential test failed for {File}", job.FileName);
                    }
                }
                // persist updated record
                await _storage.SaveRecordAsync(record, ct).ConfigureAwait(false);
            }

            FileArchived?.Invoke(record);
            // After metadata saved and notification sent, remove the downloaded archive and extracted txt files
            try
            {
                // Delete each extracted text file
                if (record.ExtractedTextFiles is not null)
                {
                    foreach (var f in record.ExtractedTextFiles)
                    {
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(f) && File.Exists(f))
                            {
                                File.Delete(f);
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "Failed to delete extracted file {File}", f);
                        }
                    }

                    // Try to remove the immediate parent directory if empty (message folder)
                    try
                    {
                        var first = record.ExtractedTextFiles.FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(first))
                        {
                            var parent = Path.GetDirectoryName(first) ?? string.Empty;
                            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                            {
                                if (!Directory.EnumerateFileSystemEntries(parent).Any())
                                {
                                    Directory.Delete(parent);
                                    _log.LogInformation("Deleted empty extracted folder {Folder}", parent);
                                }
                            }
                        }
                    }
                    catch { /* best-effort cleanup */ }
                }

                // Delete the downloaded archive/file itself
                try
                {
                    if (!string.IsNullOrWhiteSpace(record.DownloadPath) && File.Exists(record.DownloadPath))
                    {
                        File.Delete(record.DownloadPath);
                        _log.LogInformation("Deleted downloaded file {File}", record.DownloadPath);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to delete downloaded file {File}", record.DownloadPath);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error while cleaning up files for {File}", job.FileName);
            }
            _metrics.ReportProcessingCompleted();
            _log.LogInformation("Processed {File}: kind={Kind}, extracted {Count} txt file(s)",
                job.FileName, result.Kind, result.ExtractedFiles.Count);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            job.Status = ProcessingStatus.Failed;
            job.Error = ex.Message;
            JobChanged?.Invoke(job);
            _log.LogError(ex, "Processing failed for {File}", job.FileName);
        }
    }

    private static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unknown";
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(clean) ? "unknown" : clean;
    }
}
