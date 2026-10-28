using DevContainers.Core.Processes;

namespace DevContainers.Core.Tests;

/// <summary>Answers process calls from a handler and records what was run.</summary>
internal sealed class FakeProcessRunner(Func<ProcessSpec, ProcessResult> handler) : IProcessRunner
{
    public List<ProcessSpec> Calls { get; } = [];

    public Task<ProcessResult> RunAsync(ProcessSpec spec, IProcessOutputSink? sink, CancellationToken cancellationToken)
    {
        this.Calls.Add(spec);
        var result = handler(spec);
        foreach (var line in result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            sink?.OnStandardError(line);
        }

        return Task.FromResult(result);
    }

    public static ProcessResult Ok(string stdout = "") => new(0, stdout, string.Empty);

    public static ProcessResult Fail(string stderr) => new(1, string.Empty, stderr);
}

/// <summary>A temporary folder deleted on dispose.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        this.Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "devcontainers-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.Path);
    }

    public string Path { get; }

    public string Write(string relativePath, string content = "{}")
    {
        var full = System.IO.Path.Combine(this.Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose() => Directory.Delete(this.Path, recursive: true);
}
