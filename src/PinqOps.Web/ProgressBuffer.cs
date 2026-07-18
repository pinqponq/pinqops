namespace PinqOps.Web;

/// <summary>
/// Thread-safe log buffer for the single-flight runner install so the UI can
/// poll live progress while the install POST is still running. One instance is
/// enough because the install endpoint is gated by a semaphore.
/// </summary>
public sealed class ProgressBuffer
{
    private readonly object _gate = new();
    private readonly List<string> _lines = new();

    public bool Active { get; private set; }

    public bool? Succeeded { get; private set; }

    public void Start()
    {
        lock (_gate)
        {
            _lines.Clear();
            Active = true;
            Succeeded = null;
        }
    }

    public void Add(string line)
    {
        lock (_gate)
        {
            _lines.Add(line);
        }
    }

    public void Finish(bool succeeded)
    {
        lock (_gate)
        {
            Active = false;
            Succeeded = succeeded;
        }
    }

    public object Snapshot()
    {
        lock (_gate)
        {
            return new { active = Active, succeeded = Succeeded, log = string.Join('\n', _lines) };
        }
    }

    public string Text()
    {
        lock (_gate)
        {
            return string.Join('\n', _lines);
        }
    }
}
