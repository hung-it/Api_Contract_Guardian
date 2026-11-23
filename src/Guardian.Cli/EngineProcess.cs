using System.ComponentModel;
using System.Diagnostics;

namespace Guardian.Cli;

internal sealed class EngineException(string message) : Exception(message);

internal sealed record EngineResult(int ExitCode, byte[] Stdout);

internal static class EngineProcess
{
    internal static async Task<EngineResult> RunAsync(string executable, string directory,
        IReadOnlyList<string> arguments, CancellationToken cancellationToken, TimeSpan? timeout = null,
        int stdoutLimit = 8 * 1024 * 1024, int stderrLimit = 1024 * 1024)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new EngineException("Engine invocation canceled.");
        }

        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        foreach (var key in start.Environment.Keys.Where(key =>
            key.StartsWith("OASDIFF_", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            start.Environment.Remove(key);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            throw new EngineException("Could not start the configured oasdiff executable.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, stdoutLimit, "stdout", deadline.Token);
        var stderr = ReadBoundedAsync(process.StandardError.BaseStream, stderrLimit, "stderr", deadline.Token);
        var exit = process.WaitForExitAsync(deadline.Token);
        Task[] tasks = [stdout, stderr, exit];

        try
        {
            var pending = tasks.ToList();
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending);
                await finished;
                pending.Remove(finished);
            }

            return new EngineResult(process.ExitCode, await stdout);
        }
        catch (Exception error) when (error is EngineException or OperationCanceledException
            or IOException or InvalidOperationException)
        {
            deadline.Cancel();
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception stopError) when (stopError is InvalidOperationException or Win32Exception)
            {
                // The process can exit concurrently with the kill request.
            }

            try
            {
                await Task.WhenAll([.. tasks, process.WaitForExitAsync()])
                    .WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception cleanupError) when (cleanupError is EngineException or OperationCanceledException
                or TimeoutException or IOException or InvalidOperationException)
            {
                // Preserve the failure that initiated cleanup.
            }

            if (error is OperationCanceledException)
            {
                throw new EngineException(cancellationToken.IsCancellationRequested
                    ? "Engine invocation canceled." : "Engine invocation timed out.");
            }
            if (error is not EngineException)
            {
                throw new EngineException("Could not read the engine result.");
            }
            throw;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, string name,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + count > limit)
            {
                throw new EngineException($"Engine {name} exceeded its output limit.");
            }
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
