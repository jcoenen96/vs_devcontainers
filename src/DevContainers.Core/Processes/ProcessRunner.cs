using System.Diagnostics;
using System.Text;

namespace DevContainers.Core.Processes;

/// <summary>Describes a process to start.</summary>
public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments)
{
    public string? WorkingDirectory { get; init; }

    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    public override string ToString() => string.Join(' ', new[] { FileName }.Concat(Arguments.Select(Quote)));

    private static string Quote(string arg) => arg.Length == 0 || arg.Any(char.IsWhiteSpace) ? $"\"{arg}\"" : arg;
}

/// <summary>Result of a finished process.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Receives output lines while a process runs.</summary>
public interface IProcessOutputSink
{
    void OnStandardOutput(string line);

    void OnStandardError(string line);
}

/// <summary>Starts processes. Abstracted so the orchestration logic can be tested without real processes.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, IProcessOutputSink? sink, CancellationToken cancellationToken);
}

/// <summary>Runs real operating system processes.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessSpec spec, IProcessOutputSink? sink, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in spec.Arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (spec.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = spec.WorkingDirectory;
        }

        if (spec.Environment is not null)
        {
            foreach (var (key, value) in spec.Environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stdoutClosed.TrySetResult();
                return;
            }

            lock (stdout)
            {
                stdout.AppendLine(e.Data);
            }

            sink?.OnStandardOutput(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stderrClosed.TrySetResult();
                return;
            }

            lock (stderr)
            {
                stderr.AppendLine(e.Data);
            }

            sink?.OnStandardError(e.Data);
        };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ProcessStartException(spec, ex);
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            throw;
        }

        await Task.WhenAll(stdoutClosed.Task, stderrClosed.Task).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}

/// <summary>Thrown when an executable cannot be started, typically because it is not installed.</summary>
public sealed class ProcessStartException(ProcessSpec spec, Exception inner)
    : Exception($"Could not start '{spec.FileName}': {inner.Message}", inner)
{
    public ProcessSpec Spec { get; } = spec;
}
