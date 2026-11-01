using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace DevContainers.VisualStudio.Services;

/// <summary>
/// Runs a Visual Studio command (as typed in the Command Window) in the Visual Studio instance that has a given
/// solution open. VisualStudio.Extensibility has no API to start a debug session from an out-of-process extension,
/// so the extension reaches Visual Studio's automation object (DTE) through the Running Object Table, as external
/// tools do.
/// </summary>
internal static class VisualStudioAutomation
{
    private const int RejectedByCallee = unchecked((int)0x80010001);
    private const int ServerBusy = unchecked((int)0x8001010A);

    /// <summary>Runs the command, retrying while Visual Studio is busy. Throws when no instance has the solution open.</summary>
    public static async Task ExecuteCommandAsync(string solutionPath, string command, string arguments, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var dte = FindDte(solutionPath) ?? throw new InvalidOperationException(
                    $"No Visual Studio instance with {Path.GetFileName(solutionPath)} open was found to start the debugger in.");
                dte.GetType().InvokeMember("ExecuteCommand", BindingFlags.InvokeMethod, null, dte, [command, arguments]);
                return;
            }
            catch (Exception ex) when (attempt < 20 && IsBusy(ex))
            {
                // Visual Studio rejects calls while it is busy (a modal dialog, a build); try again shortly.
                await Task.Delay(500, cancellationToken);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        }
    }

    /// <summary>
    /// Attaches Visual Studio's JavaScript debugger to a Node.js inspector (such as <c>localhost:9229</c>), as
    /// Debug > Attach to Process does with the "JavaScript and TypeScript (Chrome DevTools/V8 Inspector)" connection
    /// type. Returns false when this Visual Studio has no such connection type or nothing answers at the target.
    /// </summary>
    public static async Task<bool> AttachToNodeInspectorAsync(string solutionPath, string target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                var dte = FindDte(solutionPath) ?? throw new InvalidOperationException(
                    $"No Visual Studio instance with {Path.GetFileName(solutionPath)} open was found to attach the debugger in.");
                var debugger = Get(dte, "Debugger")!;
                var transports = Get(debugger, "Transports")!;
                object? transport = null;
                for (var i = 1; i <= (int)Get(transports, "Count")!; i++)
                {
                    var candidate = Invoke(transports, "Item", i)!;
                    var name = Get(candidate, "Name") as string ?? string.Empty;
                    if (name.Contains("V8", StringComparison.OrdinalIgnoreCase) || name.Contains("DevTools", StringComparison.OrdinalIgnoreCase))
                    {
                        transport = candidate;
                        break;
                    }
                }

                if (transport is null)
                {
                    return false;
                }

                var processes = Invoke(debugger, "GetProcesses", transport, target)!;
                if ((int)Get(processes, "Count")! > 0)
                {
                    Invoke(Invoke(processes, "Item", 1)!, "Attach");
                    return true;
                }
            }
            catch (Exception ex) when (IsBusy(ex) || ex is COMException or TargetInvocationException)
            {
                // Busy, or nothing answers at the target yet: try again until the timeout.
            }

            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            await Task.Delay(1000, cancellationToken);
        }
    }

    private static object? Get(object target, string property) =>
        target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, null);

    private static object? Invoke(object target, string method, params object[] arguments) =>
        target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, arguments);

    private static bool IsBusy(Exception ex) =>
        (ex as COMException ?? ex.InnerException as COMException)?.ErrorCode is RejectedByCallee or ServerBusy;

    private static object? FindDte(string solutionPath)
    {
        Marshal.ThrowExceptionForHR(GetRunningObjectTable(0, out var table));
        Marshal.ThrowExceptionForHR(CreateBindCtx(0, out var context));
        table.EnumRunning(out var monikers);
        var found = new IMoniker[1];
        while (monikers.Next(1, found, IntPtr.Zero) == 0)
        {
            found[0].GetDisplayName(context, null, out var name);
            if (!name.StartsWith("!VisualStudio.DTE.", StringComparison.Ordinal))
            {
                continue;
            }

            table.GetObject(found[0], out var dte);
            try
            {
                var solution = dte.GetType().InvokeMember("Solution", BindingFlags.GetProperty, null, dte, null);
                var fullName = solution?.GetType().InvokeMember("FullName", BindingFlags.GetProperty, null, solution, null) as string;
                if (!string.IsNullOrEmpty(fullName) && string.Equals(Path.GetFullPath(fullName).TrimEnd('\\'), Path.GetFullPath(solutionPath).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    return dte;
                }
            }
            catch (Exception ex) when (ex is COMException or TargetInvocationException)
            {
                // That instance is busy or closing; it is not the one with our solution if it cannot say.
            }
        }

        return null;
    }

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable table);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx context);
}
