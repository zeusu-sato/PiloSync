namespace PiloSync.Core;

public static class AppFolder
{
    public static string Current => Resolve(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    public static string Resolve(string localAppData)
    {
        string next = Path.Combine(localAppData, "PiloSync"), previous = Path.Combine(localAppData, "PalmSync");
        return Directory.Exists(next) || !Directory.Exists(previous) ? next : previous;
    }
}
