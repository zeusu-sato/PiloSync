namespace PiloSync.Cli.Update;

public sealed record StagedUpdate(string Tag, string Version, string Folder);

public interface IUpdateFetcher
{
    Task<StagedUpdate?> FetchAsync(string currentTag, Action<string> log, CancellationToken token);
}
