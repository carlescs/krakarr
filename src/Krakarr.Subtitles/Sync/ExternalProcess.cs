using System.Diagnostics;

namespace Krakarr.Subtitles.Sync;

/// <summary>Exit code of an external tool and the last lines it printed.</summary>
public sealed record ProcessResult(int ExitCode, string Output)
{
    public bool Success => ExitCode == 0;

    /// <summary>The last non-empty line of output, for error messages.</summary>
    public string LastLine => Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? $"exit code {ExitCode}";
}

/// <summary>Runs a command-line tool without a shell, keeping only the tail of its output and killing it on timeout.</summary>
public static class ExternalProcess
{
    private const int MaxOutputLines = 20;

    public static async Task<ProcessResult> RunAsync(
        string fileName, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var (key, value) in environment) info.Environment[key] = value;

        // Tools like ffsubsync print progress bars all the time: keep a bounded tail only.
        var tail = new Queue<string>(MaxOutputLines);
        void Collect(object _, DataReceivedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (tail)
            {
                if (tail.Count == MaxOutputLines) tail.Dequeue();
                tail.Enqueue(e.Data.Length > 500 ? e.Data[..500] : e.Data);
            }
        }

        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += Collect;
        process.ErrorDataReceived += Collect;
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"Could not start {fileName}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"Could not start {Path.GetFileName(fileName)}: {ex.Message}", ex);
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(fileName)} took longer than {timeout.TotalSeconds:0} s and was stopped");
        }

        lock (tail) return new ProcessResult(process.ExitCode, string.Join('\n', tail));
    }
}
