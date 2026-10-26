namespace AudioMemo.Services;

internal enum ProcessingOutcome { None, Working, Completed, Warning, Cancelled, Failed }

internal sealed class ProcessingActivity
{
    private readonly HashSet<long> active = [];
    private long nextId;
    private bool warned;
    private bool failed;
    private bool cancelled;
    public ProcessingOutcome Outcome { get; private set; }
    public bool IsActive(long id) => active.Contains(id);

    public long Begin()
    {
        if (active.Count == 0)
            warned = failed = cancelled = false;
        var id = ++nextId;
        active.Add(id);
        Outcome = ProcessingOutcome.Working;
        return id;
    }

    public void Warn(long id)
    {
        if (IsActive(id))
            warned = true;
    }

    public void Finish(long id, ProcessingOutcome outcome)
    {
        if (outcome is not (ProcessingOutcome.Completed or ProcessingOutcome.Warning or ProcessingOutcome.Cancelled or ProcessingOutcome.Failed))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        if (!active.Remove(id))
            throw new InvalidOperationException("The processing operation is not active.");
        warned |= outcome == ProcessingOutcome.Warning;
        failed |= outcome == ProcessingOutcome.Failed;
        cancelled |= outcome == ProcessingOutcome.Cancelled;
        Outcome = active.Count > 0 ? ProcessingOutcome.Working :
            failed ? ProcessingOutcome.Failed :
            cancelled ? ProcessingOutcome.Cancelled :
            warned ? ProcessingOutcome.Warning : ProcessingOutcome.Completed;
    }

    public void Acknowledge()
    {
        if (active.Count == 0)
            Outcome = ProcessingOutcome.None;
    }
}
