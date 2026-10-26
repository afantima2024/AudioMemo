namespace AudioMemo.Core.Models;

public sealed class HistoryPeekState
{
    public static readonly TimeSpan HideDelay = TimeSpan.FromSeconds(5);
    private TimeSpan lastActivity;
    public bool IsOpen { get; private set; }
    public bool IsPointerInside { get; private set; }

    public void Reveal(TimeSpan now)
    {
        IsOpen = true;
        lastActivity = now;
    }

    public void UpdatePointer(bool inside, TimeSpan now)
    {
        if (inside != IsPointerInside)
            lastActivity = now;
        IsPointerInside = inside;
    }

    public void Interact(TimeSpan now) => lastActivity = now;

    public bool Expire(TimeSpan now)
    {
        if (!IsOpen || IsPointerInside || now - lastActivity < HideDelay)
            return false;
        Close();
        return true;
    }

    public void Close()
    {
        IsOpen = false;
        IsPointerInside = false;
    }
}
