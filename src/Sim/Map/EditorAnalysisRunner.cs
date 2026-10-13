using System.Diagnostics;
using Nitrogenesis.Sim.Racing;

namespace Nitrogenesis.Sim.Map;

/// <summary>
/// Runs <see cref="EditorAnalysis"/> off the UI thread (PLAN §3.2, §6.3): <see cref="Request"/> is debounced
/// <see cref="DebounceMilliseconds"/> after the last call, a newer request replaces a waiting one and cancels a
/// running one, and only the newest request ever produces a result. The UI polls <see cref="TryTakeResult"/> each
/// frame and shows a result only if its <see cref="EditorAnalysisResult.Revision"/> equals the document's.
/// </summary>
/// <remarks>Thread-safe. One dedicated background thread; the lock guards only the hand-over, never the computation.</remarks>
public sealed class EditorAnalysisRunner : IDisposable
{
    /// <summary>Quiet time after the last request before the analysis starts.</summary>
    public const int DebounceMilliseconds = 150;

    public delegate EditorAnalysisResult Analyzer(Track track, RacingSettings settings, CancellationToken ct);

    private sealed record Pending(Track Track, long Revision, long DueTicks);

    private readonly object _gate = new();
    private readonly RacingSettings _settings;
    private readonly Analyzer _analyze;
    private readonly long _debounceTicks;
    private readonly Thread _thread;
    private Pending? _pending;
    private CancellationTokenSource? _running;
    private EditorAnalysisResult? _latest;
    private Exception? _lastError;
    private long _latestTimestamp;
    private bool _disposed;

    public EditorAnalysisRunner(RacingSettings? settings = null, int debounceMilliseconds = DebounceMilliseconds, Analyzer? analyzer = null)
    {
        _settings = settings ?? new RacingSettings();
        _analyze = analyzer ?? EditorAnalysis.Run;
        _debounceTicks = debounceMilliseconds * Stopwatch.Frequency / 1000;
        _thread = new Thread(Loop) { IsBackground = true, Name = "EditorAnalysis" };
        _thread.Start();
    }

    /// <summary>
    /// Asks for an analysis of <paramref name="snapshot"/> (a track nobody else changes, e.g. <c>EditDocument.Snapshot()</c>)
    /// made at document revision <paramref name="revision"/>. Supersedes every earlier request.
    /// </summary>
    public void Request(Track snapshot, long revision)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (_disposed) return;
            _pending = new Pending(snapshot, revision, Stopwatch.GetTimestamp() + _debounceTicks);
            _running?.Cancel();
            _latest = null; // an older result must not be shown any more
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary><see cref="Stopwatch.GetTimestamp"/> at the moment the result now waiting (or last taken) was published; for timing logs.</summary>
    public long LatestTimestamp
    {
        get { lock (_gate) return _latestTimestamp; }
    }

    /// <summary>Takes the newest finished result, once; false when there is none.</summary>
    public bool TryTakeResult(out EditorAnalysisResult result)
    {
        lock (_gate)
        {
            result = _latest!;
            if (_latest is null) return false;
            _latest = null;
            return true;
        }
    }

    /// <summary>Waits up to <paramref name="timeout"/> for a result and takes it (for tests and tools).</summary>
    public bool WaitForResult(TimeSpan timeout, out EditorAnalysisResult result)
    {
        long end = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        lock (_gate)
        {
            while (_latest is null && !_disposed)
            {
                long left = end - Stopwatch.GetTimestamp();
                if (left <= 0) break;
                Monitor.Wait(_gate, (int)Math.Max(1, left * 1000 / Stopwatch.Frequency));
            }
            result = _latest!;
            if (_latest is null) return false;
            _latest = null;
            return true;
        }
    }

    /// <summary>True while a request is waiting for its debounce or being computed.</summary>
    public bool IsBusy
    {
        get { lock (_gate) return _pending is not null || _running is not null; }
    }

    /// <summary>The exception of the last analysis that failed (not cancelled), if any.</summary>
    public Exception? LastError
    {
        get { lock (_gate) return _lastError; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending = null;
            _running?.Cancel();
            Monitor.PulseAll(_gate);
        }
        _thread.Join();
    }

    private void Loop()
    {
        while (true)
        {
            Pending job;
            CancellationTokenSource cts;
            lock (_gate)
            {
                while (true)
                {
                    if (_disposed) return;
                    if (_pending is null) { Monitor.Wait(_gate); continue; }
                    long left = _pending.DueTicks - Stopwatch.GetTimestamp();
                    if (left <= 0) break;
                    Monitor.Wait(_gate, (int)Math.Max(1, left * 1000 / Stopwatch.Frequency));
                }
                job = _pending;
                _pending = null;
                cts = _running = new CancellationTokenSource();
            }

            EditorAnalysisResult? result = null;
            Exception? error = null;
            try { result = _analyze(job.Track, _settings, cts.Token) with { Revision = job.Revision }; }
            catch (OperationCanceledException) { }
            catch (Exception e) { error = e; }

            lock (_gate)
            {
                bool stale = cts.IsCancellationRequested || _pending is not null || _disposed;
                _running = null;
                if (error is not null) _lastError = error;
                else if (result is not null && !stale) { _latest = result; _latestTimestamp = Stopwatch.GetTimestamp(); }
                Monitor.PulseAll(_gate);
            }
            cts.Dispose();
        }
    }
}
