namespace ParkMesh.Application.Allocation;

public interface IStateHydrationTracker
{
    bool IsHydrated { get; }
    int RestoredStateCount { get; }
    string? LastError { get; }
    void MarkHydrated(int restoredStateCount);
    void MarkFailed(string error);
}

public sealed class StateHydrationTracker : IStateHydrationTracker
{
    private int _isHydrated;
    private int _restoredStateCount;
    private string? _lastError;

    public bool IsHydrated => Volatile.Read(ref _isHydrated) == 1;

    public int RestoredStateCount => Volatile.Read(ref _restoredStateCount);

    public string? LastError => Volatile.Read(ref _lastError);

    public void MarkHydrated(int restoredStateCount)
    {
        Volatile.Write(ref _restoredStateCount, restoredStateCount);
        Volatile.Write(ref _lastError, null);
        Volatile.Write(ref _isHydrated, 1);
    }

    public void MarkFailed(string error)
    {
        Volatile.Write(ref _lastError, error);
    }
}
