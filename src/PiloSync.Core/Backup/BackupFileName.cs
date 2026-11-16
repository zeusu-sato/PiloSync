namespace PiloSync.Protocol;

public static class BackupFileName
{
    public static string For(string name, bool isResource, string type, string creator)
    {
        string safe = new(name.Select(c => char.IsControl(c) || "\\/:*?\"<>|".Contains(c) ? '_' : c).ToArray());
        safe = safe.TrimEnd('.', ' '); if (safe.Length == 0) safe = "_";
        string stem = safe.Split('.')[0].TrimEnd(' ');
        if (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9')) safe = "_" + safe;
        return safe + (isResource ? ".prc" : type == "pqa " ? ".pqa" : ".pdb");
    }
}
