using Nitrogenesis.Sim.Io;

namespace Nitrogenesis.Sim.History;

/// <summary>
/// Writes saves on its own background thread (PLAN §5.1: "Autosave … on a background thread from a copied
/// snapshot"), one at a time. The training thread plans a save (<see cref="Session.PrepareSave"/>, a quick copy)
/// and hands it over; training goes on while the files are written. A failed save is reported and the next one
/// writes everything again (nothing is marked saved until a save finishes).
/// </summary>
public sealed class SessionSaver : IDisposable
{
    private readonly object _lock = new();
    private readonly SessionFolder _folder;
    private readonly Session _session;
    private readonly Thread _thread;
    private SavePlan? _pending;
    private bool _running, _stop;

    public SessionSaver(SessionFolder folder, Session session)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(session);
        _folder = folder;
        _session = session;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Session saver" };
        _thread.Start();
    }

    public SessionFolder Folder => _folder;

    /// <summary>A save is waiting or being written.</summary>
    public bool Busy
    {
        get { lock (_lock) return _pending is not null || _running; }
    }

    /// <summary>When the last successful save was planned (UTC), or default.</summary>
    public DateTime LastSavedUtc { get { lock (_lock) return _lastSaved; } }
    private DateTime _lastSaved;

    /// <summary>Why the last save failed, or null when it worked.</summary>
    public string? LastError { get { lock (_lock) return _lastError; } }
    private string? _lastError;

    /// <summary>Successful saves so far.</summary>
    public int Saves { get { lock (_lock) return _saves; } }
    private int _saves;

    /// <summary>Raised on the saver thread after each save attempt (null error = success).</summary>
    public event Action<string?>? Saved;

    /// <summary>Queues a save. A save still waiting is replaced: the newer plan covers everything the older one had.</summary>
    public void Start(SavePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_stop, this);
            _pending = plan;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Waits until no save is waiting or running. Returns false on timeout.</summary>
    public bool WaitIdle(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        lock (_lock)
        {
            while (_pending is not null || _running)
            {
                var left = deadline - DateTime.UtcNow;
                if (timeout != Timeout.InfiniteTimeSpan && left <= TimeSpan.Zero) return false;
                Monitor.Wait(_lock, timeout == Timeout.InfiniteTimeSpan ? Timeout.InfiniteTimeSpan : left);
            }
            return true;
        }
    }

    private void Loop()
    {
        while (true)
        {
            SavePlan plan;
            lock (_lock)
            {
                while (_pending is null && !_stop) Monitor.Wait(_lock);
                if (_pending is null) return;
                plan = _pending;
                _pending = null;
                _running = true;
            }
            string? error = null;
            try
            {
                _folder.Save(plan);
                _session.MarkSaved(plan);
            }
            catch (Exception e)
            {
                // Any failure (also an unexpected one) is reported, never allowed to end the app: the previous save
                // stays loadable and the next save writes everything this one should have.
                error = e.Message;
            }
            lock (_lock)
            {
                _running = false;
                _lastError = error;
                if (error is null)
                {
                    _lastSaved = plan.Header.SavedAtUtc;
                    _saves++;
                }
                Monitor.PulseAll(_lock);
            }
            Saved?.Invoke(error);
        }
    }

    /// <summary>Finishes the save in hand (and one waiting), then stops the thread.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_stop) return;
            _stop = true;
            Monitor.PulseAll(_lock);
        }
        _thread.Join();
    }
}
