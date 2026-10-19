using System.Diagnostics;

namespace Jellyfin.Plugin.S3Storage;

internal static class ManagedProcess
{
    public static ProcessStartInfo StartInfo(string executable, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    public static async Task RunAsync(ProcessStartInfo start, string? input, CancellationToken ct)
    {
        start.RedirectStandardInput = input is not null;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动外部程序。");
        using var registration = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var errors = process.StandardError.ReadToEndAsync(ct);
        if (input is not null) { await process.StandardInput.WriteAsync(input.AsMemory(), ct); process.StandardInput.Close(); }
        await process.WaitForExitAsync(ct);
        await output; await errors;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(start.FileName)} 退出码 {process.ExitCode}。请检查程序配置与运行权限。");
    }
}
