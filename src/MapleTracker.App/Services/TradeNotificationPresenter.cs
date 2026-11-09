namespace MapleTracker.App.Services;

internal sealed class TradeNotificationPresenter(Window anchor) : IDisposable
{
    private readonly Queue<TradeAlert> pending = new();
    internal TradeAlertWindow? Current { get; private set; }
    internal int PendingCount => pending.Count;
    private bool clearing;

    public void Show(TradeAlert alert)
    {
        pending.Enqueue(alert);
        if (Current == null) ShowNext();
        else Current.SetPendingCount(pending.Count);
    }

    private void ShowNext()
    {
        if (clearing || pending.Count == 0) return;
        // No WPF Owner: minimizing or hiding chat must not hide trade alerts.
        var popup = new TradeAlertWindow();
        Current = popup;
        popup.Closed += (_, _) => { Current = null; ShowNext(); };
        try
        {
            popup.Present(pending.Dequeue(), anchor);
            popup.SetPendingCount(pending.Count);
        }
        catch
        {
            clearing = true; popup.Close(); clearing = false;
            throw;
        }
    }

    public void Clear()
    {
        clearing = true; pending.Clear(); Current?.Close(); Current = null; clearing = false;
    }
    public void Dispose() => Clear();
}
