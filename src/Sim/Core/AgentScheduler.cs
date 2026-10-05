using System.Runtime.InteropServices;

namespace Nitrogenesis.Sim.Core;

/// <summary>
/// Runs a mode's agents on W worker threads (PLAN §2.1, "Threading"). Agents never interact, so a worker runs
/// its chunk of agents K ticks in a row with no barrier; workers only meet at the end of a phase.
/// </summary>
/// <remarks>
/// <para>A phase is at most <see cref="RebalanceTicks"/> ticks. Before each phase the alive agents are split
/// into chunks of <see cref="ChunkSize"/> alive agents (a chunk is the index range from its first to its last
/// alive agent, so finished agents cost nothing), and workers take chunks through an atomic counter until none
/// are left. The thread that calls <see cref="Run"/> is worker 0; W − 1 background threads are the others.</para>
/// <para>Chunks are small (<see cref="ChunkSize"/> = 4, not the plan's 32) so the load stays balanced across
/// workers even when only a few dozen agents are alive near the end of a generation; measured, chunks of 4 lose
/// nothing to false sharing at the chunk edges, while chunks of 1 do (NOTES.md, M1 deviations).</para>
/// <para>Results are bit-identical for any W: an agent's ticks depend only on its own state, and every agent
/// is stepped by exactly one worker per phase. <see cref="Run"/> does not allocate.</para>
/// </remarks>
public sealed class AgentScheduler : IDisposable
{
    /// <summary>Alive agents per chunk.</summary>
    public const int ChunkSize = 4;
    /// <summary>Longest run of ticks between two rebalances (one second of game time).</summary>
    public const int RebalanceTicks = 60;
    public const int MaxWorkers = 64;

    private readonly IAgentMode _mode;
    private readonly AgentRange[] _chunks;
    private readonly Thread[] _threads;
    private readonly SemaphoreSlim[] _go;
    private readonly ManualResetEventSlim _phaseDone = new(false);

    // Phase state, written by the coordinator before the workers are released (the release is a full fence).
    private IAgentPolicy? _policy;
    private Action? _sideJob;
    private int _phaseTicks, _chunkCount, _pending;
    private PaddedCounter _next;
    private Exception? _failure;
    private volatile bool _disposed;

    /// <summary>Chunk counter on its own cache lines, so workers taking chunks do not slow down neighbouring fields.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct PaddedCounter
    {
        [FieldOffset(64)] public int Value;
    }

    /// <param name="mode">The agents to run.</param>
    /// <param name="workers">Worker count W, 1…<see cref="MaxWorkers"/> (1 = everything on the calling thread).</param>
    public AgentScheduler(IAgentMode mode, int workers)
    {
        ArgumentNullException.ThrowIfNull(mode);
        if (workers < 1 || workers > MaxWorkers) throw new ArgumentOutOfRangeException(nameof(workers));
        _mode = mode;
        Workers = workers;
        _chunks = new AgentRange[(mode.AgentCount + ChunkSize - 1) / ChunkSize];
        _threads = new Thread[workers - 1];
        _go = new SemaphoreSlim[workers - 1];
        for (int w = 0; w < _threads.Length; w++)
        {
            _go[w] = new SemaphoreSlim(0);
            int index = w;
            _threads[w] = new Thread(() => WorkerLoop(index)) { IsBackground = true, Name = $"Sim worker {w + 1}" };
            _threads[w].Start();
        }
    }

    public int Workers { get; }

    /// <summary>
    /// Runs every alive agent for up to <paramref name="maxTicks"/> ticks, in phases of at most
    /// <see cref="RebalanceTicks"/>. Returns the ticks actually run, i.e. how far the last agent got: less than
    /// asked only when all agents are done. Assumes the alive agents are all at the same tick (they start together).
    /// </summary>
    public int Run(IAgentPolicy policy, int maxTicks)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ObjectDisposedException.ThrowIf(_disposed, this);
        int run = 0;
        while (run < maxTicks)
        {
            int chunks = BuildChunks();
            if (chunks == 0) break;
            int ticks = Math.Min(RebalanceTicks, maxTicks - run);
            int startTick = _mode.Tick(_chunks[0].Start); // the first agent of a chunk is alive
            RunPhase(policy, ticks, chunks);
            // An agent stops early only when it is done; the furthest one says how far the phase really went.
            int furthest = startTick;
            for (int c = 0; c < chunks; c++)
                for (int i = _chunks[c].Start; i < _chunks[c].End; i++)
                    furthest = Math.Max(furthest, _mode.Tick(i));
            run += furthest - startTick;
            if (furthest - startTick < ticks) break; // every agent is done
        }
        return run;
    }

    /// <summary>
    /// Runs <paramref name="side"/> on a background worker while the calling thread runs <paramref name="main"/>,
    /// and returns when both are done (both run on the calling thread, side first, when W = 1). For independent
    /// single-threaded jobs between phases, e.g. the end-of-generation work. Both always run to the end; an
    /// exception from either is rethrown afterwards.
    /// </summary>
    public void RunAlongside(Action side, Action main)
    {
        ArgumentNullException.ThrowIfNull(side);
        ArgumentNullException.ThrowIfNull(main);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _failure = null;
        if (_threads.Length == 0)
        {
            try
            {
                side();
            }
            catch (Exception e)
            {
                _failure = e;
            }
            main();
        }
        else
        {
            RunSideOnWorker(side, main);
        }
        if (_failure is { } failure) throw new AggregateException("The side job failed.", failure);
    }

    private void RunSideOnWorker(Action side, Action main)
    {
        _sideJob = side;
        _pending = 1;
        _phaseDone.Reset();
        _go[0].Release();
        try
        {
            main();
        }
        finally
        {
            _phaseDone.Wait();
            _sideJob = null;
        }
    }

    /// <summary>Agents still running.</summary>
    public int CountAlive()
    {
        int alive = 0;
        for (int i = 0; i < _mode.AgentCount; i++)
            if (!_mode.IsDone(i)) alive++;
        return alive;
    }

    /// <summary>Splits the alive agents into chunks of <see cref="ChunkSize"/>; returns the chunk count.</summary>
    private int BuildChunks()
    {
        int count = 0, inChunk = 0, start = 0;
        for (int i = 0; i < _mode.AgentCount; i++)
        {
            if (_mode.IsDone(i)) continue;
            if (inChunk == 0) start = i;
            if (++inChunk == ChunkSize)
            {
                _chunks[count++] = new AgentRange(start, i + 1 - start);
                inChunk = 0;
            }
        }
        if (inChunk > 0)
        {
            int end = _mode.AgentCount;
            while (_mode.IsDone(end - 1)) end--;
            _chunks[count++] = new AgentRange(start, end - start);
        }
        return count;
    }

    private void RunPhase(IAgentPolicy policy, int ticks, int chunks)
    {
        _policy = policy;
        _phaseTicks = ticks;
        _chunkCount = chunks;
        _next.Value = 0;
        _failure = null;
        // Only wake as many helpers as there are chunks beyond the first.
        int helpers = Math.Min(_threads.Length, chunks - 1);
        if (helpers > 0)
        {
            _pending = helpers;
            _phaseDone.Reset();
            for (int w = 0; w < helpers; w++) _go[w].Release();
        }
        try
        {
            TakeChunks();
        }
        finally
        {
            if (helpers > 0) _phaseDone.Wait();
        }
        if (_failure is { } failure) throw new AggregateException("A simulation worker failed.", failure);
    }

    private void TakeChunks()
    {
        IAgentPolicy policy = _policy!;
        int ticks = _phaseTicks, count = _chunkCount;
        int c;
        while ((c = Interlocked.Increment(ref _next.Value) - 1) < count) _mode.Step(_chunks[c], ticks, policy);
    }

    private void WorkerLoop(int index)
    {
        while (true)
        {
            _go[index].Wait();
            if (_disposed) return;
            try
            {
                if (_sideJob is { } job) job();
                else TakeChunks();
            }
            catch (Exception e)
            {
                Interlocked.CompareExchange(ref _failure, e, null);
                // Stop the others taking more chunks.
                Interlocked.Exchange(ref _next.Value, int.MaxValue / 2);
            }
            finally
            {
                if (Interlocked.Decrement(ref _pending) == 0) _phaseDone.Set();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var go in _go) go.Release();
        foreach (var t in _threads) t.Join();
        foreach (var go in _go) go.Dispose();
        _phaseDone.Dispose();
    }
}
