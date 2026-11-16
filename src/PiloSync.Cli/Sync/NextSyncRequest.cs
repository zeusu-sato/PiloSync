namespace PiloSync.Cli;

public sealed class NextSyncRequest<T> where T : class
{
    private T? current;
    public T? Current => Volatile.Read(ref current);
    public void Reserve(T request) { ArgumentNullException.ThrowIfNull(request); Interlocked.Exchange(ref current, request); }
    public void Cancel() => Interlocked.Exchange(ref current, null);
    public bool TryClaim(out T? request) { request = Interlocked.Exchange(ref current, null); return request is not null; }
}
