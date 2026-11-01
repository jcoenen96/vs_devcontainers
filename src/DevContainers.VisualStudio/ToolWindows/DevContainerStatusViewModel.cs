using System.Runtime.Serialization;
using DevContainers.VisualStudio.Services;
using Microsoft.VisualStudio.Extensibility.UI;

namespace DevContainers.VisualStudio.ToolWindows;

/// <summary>What the status window shows. Updated from <see cref="PortForwardingService.StatusChanged"/>.</summary>
[DataContract]
internal sealed class DevContainerStatusViewModel : NotifyPropertyChangedObject, IDisposable
{
    private readonly PortForwardingService forwarding;
    private string state = "No dev container";
    private string details = "Open a solution whose folder has .devcontainer/devcontainer.json.";
    private string engine = string.Empty;
    private string container = string.Empty;
    private string configuration = string.Empty;
    private bool isRunning;
    private bool hasConfiguration;

    public DevContainerStatusViewModel(DevContainerService service, PortForwardingService forwarding)
    {
        this.forwarding = forwarding;
        this.OpenCommand = new AsyncCommand((_, ct) => service.OpenAsync(rebuild: false, noCache: false, ct));
        this.RebuildCommand = new AsyncCommand((_, ct) => service.OpenAsync(rebuild: true, noCache: false, ct));
        this.StopCommand = new AsyncCommand((_, ct) => service.StopAsync(ct));
        this.TerminalCommand = new AsyncCommand((_, ct) => service.OpenTerminalAsync(ct));
        this.RefreshCommand = new AsyncCommand(async (_, ct) =>
        {
            await forwarding.RefreshAsync(ct);
            this.Update(forwarding.Status);
        });
        forwarding.StatusChanged += this.OnStatusChanged;
        this.Update(forwarding.Status);
    }

    public event EventHandler<string>? TitleChanged;

    /// <summary>The window title: the state in a few words.</summary>
    public string Title => this.isRunning ? "Dev Container: running" : this.hasConfiguration ? "Dev Container: not running" : "Dev Container";

    [DataMember]
    public string State { get => this.state; private set => this.SetProperty(ref this.state, value); }

    [DataMember]
    public string Details { get => this.details; private set => this.SetProperty(ref this.details, value); }

    [DataMember]
    public string Engine { get => this.engine; private set => this.SetProperty(ref this.engine, value); }

    [DataMember]
    public string Container { get => this.container; private set => this.SetProperty(ref this.container, value); }

    [DataMember]
    public string Configuration { get => this.configuration; private set => this.SetProperty(ref this.configuration, value); }

    [DataMember]
    public bool IsRunning { get => this.isRunning; private set => this.SetProperty(ref this.isRunning, value); }

    [DataMember]
    public bool HasConfiguration { get => this.hasConfiguration; private set => this.SetProperty(ref this.hasConfiguration, value); }

    [DataMember]
    public ObservableList<PortRow> Ports { get; } = [];

    [DataMember]
    public AsyncCommand OpenCommand { get; }

    [DataMember]
    public AsyncCommand RebuildCommand { get; }

    [DataMember]
    public AsyncCommand StopCommand { get; }

    [DataMember]
    public AsyncCommand TerminalCommand { get; }

    [DataMember]
    public AsyncCommand RefreshCommand { get; }

    public void Update(ContainerStatus status)
    {
        var title = this.Title;
        this.HasConfiguration = status.ConfigFile is not null;
        this.IsRunning = status.IsRunning;
        this.Configuration = status.ConfigFile?.ConfigPath ?? string.Empty;
        this.Engine = status.Runtime is { } runtime ? $"{runtime.Kind} {runtime.ServerVersion}" : string.Empty;
        this.Container = status.ContainerId is { } id ? id[..Math.Min(12, id.Length)] : string.Empty;
        (this.State, this.Details) = status switch
        {
            { Problem: { } problem } => ("Cannot check the dev container", problem),
            { ConfigFile: null } => ("No dev container", "Open a solution whose folder has .devcontainer/devcontainer.json."),
            { IsRunning: true, Ports.Count: 0 } => ("Running", "No ports are forwarded. Ports the app opens are forwarded when it opens them."),
            { IsRunning: true } => ("Running", "Forwarded to localhost on this computer:"),
            _ => ("Not running", "Use Open in Container, the Dev Container launch profile, or a Run/Debug command to start it."),
        };

        this.Ports.Clear();
        foreach (var port in status.Ports)
        {
            this.Ports.Add(new PortRow(
                port.Spec.ToString(),
                port.IsForwarded ? $"http://localhost:{port.Spec.Port}" : $"not forwarded: {port.Problem}"));
        }

        if (this.Title != title)
        {
            this.TitleChanged?.Invoke(this, this.Title);
        }
    }

    public void Dispose() => this.forwarding.StatusChanged -= this.OnStatusChanged;

    private void OnStatusChanged(object? sender, ContainerStatus status) => this.Update(status);
}

/// <summary>One forwarded port in the status window.</summary>
[DataContract]
internal sealed class PortRow(string port, string address)
{
    [DataMember]
    public string Port { get; } = port;

    [DataMember]
    public string Address { get; } = address;
}
