using PiloSync.Protocol;

namespace PiloSync.Cli;

public sealed class TrayStatus
{
    public const int TextLimit = 127;
    private readonly object gate = new();
    private readonly List<Session> sessions = [];
    public Session Begin(string name) { lock (gate) { var session = new Session(this, name); sessions.Add(session); return session; } }
    public (string Text, int Count) Snapshot(bool paused = false, string? restoreAccount = null)
    {
        lock (gate)
        {
            string text = "PiloSync：";
            if (paused) text += "一時停止中";
            else if (sessions.Count == 0) text += "待ち受け中";
            else if (sessions.Count == 1) text += Describe(sessions[0], false);
            else text += $"{sessions.Count} 台 HotSync 中 / " + string.Join(" / ", sessions.Select(s => Describe(s, true)));
            if (restoreAccount is not null) text += $"（次の HotSync で{restoreAccount}を復元）";
            if (text.Length > TextLimit)
            {
                int end = TextLimit - 1;
                if (char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])) end--;
                text = text[..end] + "…";
            }
            return (text, sessions.Count);
        }
    }
    private static string Describe(Session session, bool multiple)
    {
        if (session.Progress is not { } p) return session.Name + (multiple ? " 開始" : " HotSync 中");
        string phase = p.Phase == SyncPhase.Restore ? (multiple ? "復元" : "復元中") : p.Phase == SyncPhase.Install ? (multiple ? "インストール" : "インストール中") : (multiple ? "保存" : "バックアップ中");
        string count = p.Total == 0 ? $"{p.Index} 件目" : $"{p.Index}/{p.Total}";
        return $"{session.Name} {phase} {count}" + (multiple ? "" : $" {p.Name}");
    }
    public sealed class Session(TrayStatus owner, string name) : IDisposable
    {
        internal string Name = name;
        internal SyncProgress? Progress;
        public void Rename(string name) { lock (owner.gate) Name = name; }
        public void Report(SyncProgress progress) { lock (owner.gate) Progress = progress; }
        public void Dispose() { lock (owner.gate) owner.sessions.Remove(this); }
    }
}
