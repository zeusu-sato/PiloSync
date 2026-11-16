using Microsoft.Win32;

namespace PiloSync.Cli.Update;

public sealed class UpdateSettings(RegistryKey root)
{
    public bool Enabled => root.GetValue("AutoUpdate") is not int value || value != 0;
    public void SetEnabled(bool enabled) => root.SetValue("AutoUpdate", enabled ? 1 : 0, RegistryValueKind.DWord);
    public string? FailedTag => root.GetValue("UpdateFailedTag") as string;
}

public static class UpdateGate
{
    public static bool CanInstall(int count, bool paused, bool listening, int openForms, bool menuOpen) =>
        count == 0 && !paused && listening && openForms == 0 && !menuOpen;
}
