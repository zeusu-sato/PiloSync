using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Diagnostics;

namespace PiloSync.Cli;

public static class InstallPipe
{
    public const int MaxFiles = 256;
    public const int MaxBytes = 1024 * 1024;
    public static string Name => $"PiloSync.Install.{WindowsIdentity.GetCurrent().User!.Value}.{Process.GetCurrentProcess().SessionId}";
    public static byte[] Encode(IReadOnlyList<string> paths)
    {
        if (paths.Count is < 1 or > MaxFiles || paths.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 32767 || p.IndexOfAny(['\r', '\n', '\0']) >= 0 || !Path.IsPathFullyQualified(p))) throw new InvalidDataException("不正なインストール一覧です。");
        byte[] bytes = new UTF8Encoding(false, true).GetBytes(paths.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" + string.Join("\n", paths) + "\n");
        if (bytes.Length > MaxBytes) throw new InvalidDataException("インストール一覧が大きすぎます。");
        return bytes;
    }
    public static string[] Decode(byte[] bytes)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException("インストール一覧が大きすぎます。");
        string[] lines;
        try { lines = new UTF8Encoding(false, true).GetString(bytes).Split('\n'); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("UTF-8 が不正です。", ex); }
        if (lines.Length < 3 || !int.TryParse(lines[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int count) || count is < 1 or > MaxFiles || lines.Length != count + 2 || lines[^1] != "") throw new InvalidDataException("不正なインストール一覧です。");
        string[] paths = lines[1..^1]; Encode(paths); return paths;
    }
    public static async Task Send(string[] paths)
    {
        byte[] bytes = Encode(paths);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            try
            {
                using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(500, timeout.Token);
                await pipe.WriteAsync(bytes, timeout.Token); await pipe.FlushAsync(timeout.Token);
                byte[] ack = new byte[1];
                if (await pipe.ReadAsync(ack, timeout.Token) != 1 || ack[0] != 1) throw new IOException("常駐版が一覧を受け取れませんでした。");
                return;
            }
            catch (TimeoutException) { await Task.Delay(100, timeout.Token); }
        }
    }
    public static async Task Listen(Action<string[]> received, Action<string> log, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var data = new MemoryStream(); byte[] buffer = new byte[4096];
                // The count and final newline frame the message; no unbounded ReadLine allocation.
                while (true)
                {
                    int n = await pipe.ReadAsync(buffer, timeout.Token);
                    if (n == 0) throw new InvalidDataException("インストール一覧が途中で終わりました。");
                    if (data.Length + n > MaxBytes) throw new InvalidDataException("インストール一覧が大きすぎます。");
                    data.Write(buffer, 0, n);
                    byte[] bytes = data.ToArray();
                    int first = Array.IndexOf(bytes, (byte)'\n');
                    if (first < 0) { if (bytes.Length > 4) throw new InvalidDataException("件数が不正です。"); continue; }
                    if (!int.TryParse(Encoding.ASCII.GetString(bytes, 0, first), out int count) || count is < 1 or > MaxFiles) throw new InvalidDataException("件数が不正です。");
                    if (bytes.Count(b => b == '\n') < count + 1) continue;
                    received(Decode(bytes));
                    await pipe.WriteAsync(new byte[] { 1 }, timeout.Token); await pipe.FlushAsync(timeout.Token); break;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ArgumentException or InvalidOperationException) { log(ex.ToString()); }
        }
    }
}
