namespace PiloSync.Protocol;

public enum SyncPhase { Install, Backup, Restore }
// Index is 1-based; Total = 0 means unknown.
public sealed record SyncProgress(SyncPhase Phase, int Index, int Total, string Name);
