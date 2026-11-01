using System.Diagnostics;
using Regex = System.Text.RegularExpressions.Regex;
using RegexOptions = System.Text.RegularExpressions.RegexOptions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace DevContainers.VisualStudio.UITests;

/// <summary>
/// Starts Visual Studio and works it through UI Automation: menus, prompts and the first-run dialogs a fresh
/// machine shows. Every wait polls, because Visual Studio loads solutions and extensions in the background.
/// </summary>
internal sealed class VisualStudioDriver : IDisposable
{
    // Links and buttons of the dialogs a never-used Visual Studio shows before the IDE (sign in, theme, what's new).
    private static readonly Regex FirstRunChoice = new(
        @"^(skip|not now|maybe later|continue without|start visual studio)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly Dictionary<IntPtr, DateTime> dialogsSeen = [];

    private readonly UIA3Automation automation = new();
    private readonly Process process;
    private readonly string artifacts;
    private readonly Action<string> log;
    private Window? mainWindow;

    private VisualStudioDriver(Process process, string artifacts, Action<string> log)
    {
        this.process = process;
        this.artifacts = artifacts;
        this.log = log;
    }

    public static VisualStudioDriver Launch(string solution, string artifacts, Action<string> log)
    {
        var devenv = Environment.GetEnvironmentVariable("VS_DEVENV") ?? FindDevenv();
        var startInfo = new ProcessStartInfo(devenv) { UseShellExecute = false };
        startInfo.ArgumentList.Add(solution);
        startInfo.ArgumentList.Add("/log");
        startInfo.ArgumentList.Add(Path.Combine(artifacts, "ActivityLog.xml"));
        log($"Starting {devenv} {solution}");
        return new VisualStudioDriver(Process.Start(startInfo)!, artifacts, log);
    }

    /// <summary>The IDE window: the devenv window that has the main menu bar.</summary>
    public Window Main
    {
        get
        {
            if (this.mainWindow is not null && IsAlive(this.mainWindow))
            {
                return this.mainWindow;
            }

            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
            while (DateTime.UtcNow < deadline)
            {
                this.DismissKnownDialogs();
                foreach (var window in this.Windows())
                {
                    if (TryFind(() => window.FindFirstChild(cf => cf.ByControlType(ControlType.MenuBar))) is not null
                        || TryFind(() => window.FindFirstDescendant(cf => cf.ByControlType(ControlType.MenuBar))) is not null)
                    {
                        this.log($"Visual Studio window: {window.Title}");
                        return this.mainWindow = window;
                    }
                }

                Thread.Sleep(2000);
            }

            throw new TimeoutException("The Visual Studio main window did not appear.");
        }
    }

    /// <summary>
    /// Waits until an element whose name contains this text (a toolbar item, a tree node) exists in the IDE window.
    /// Returns false on timeout, after logging the toolbar items, so the caller can decide whether that is fatal.
    /// </summary>
    public bool WaitForElement(string nameContains, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (TryFind(() => this.Main.FindFirstDescendant(cf => cf.ByName(nameContains, PropertyConditionFlags.MatchSubstring))) is null)
        {
            if (DateTime.UtcNow > deadline)
            {
                var toolbarItems = TryFindAll(() => this.Main.FindAllDescendants(cf => cf.ByControlType(ControlType.ToolBar)))
                    .SelectMany(bar => TryFindAll(() => bar.FindAllChildren()))
                    .Select(item => $"{item.Properties.ControlType.ValueOrDefault} '{item.Properties.Name.ValueOrDefault}'");
                this.log($"Did not find {what} within {timeout}. Toolbar items: {string.Join(" | ", toolbarItems.Take(80))}");
                return false;
            }

            this.DismissKnownDialogs();
            Thread.Sleep(3000);
        }

        this.log($"Found {what}.");
        return true;
    }

    /// <summary>
    /// Makes a project the startup project, through this Visual Studio's automation object (DTE). With several
    /// projects and no saved .vs state, Visual Studio does not always pick the same one, and the Run button and the
    /// launch profile follow the startup project. Retries until the solution has loaded.
    /// </summary>
    /// <param name="uniqueName">The project's path relative to the solution, such as <c>HelloApi\HelloApi.csproj</c>.</param>
    public void SetStartupProject(string uniqueName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var dte = FindDte(this.process.Id);
                if (dte is not null)
                {
                    var solution = Get(dte, "Solution")!;
                    var build = Get(solution, "SolutionBuild")!;
                    if ((int)Get(Get(solution, "Projects")!, "Count")! > 0)
                    {
                        build.GetType().InvokeMember("StartupProjects", System.Reflection.BindingFlags.SetProperty, null, build, [uniqueName]);
                        this.log($"Startup project: {uniqueName}");
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Reflection.TargetInvocationException or InvalidCastException)
            {
                // Busy or still loading.
                last = ex;
            }

            this.DismissKnownDialogs();
            Thread.Sleep(3000);
        }

        throw new TimeoutException($"Could not make {uniqueName} the startup project within {timeout}: {last?.InnerException?.Message ?? last?.Message ?? "no Visual Studio automation object"}");
    }

    private static object? Get(object target, string property) =>
        target.GetType().InvokeMember(property, System.Reflection.BindingFlags.GetProperty, null, target, null);

    // Visual Studio registers its DTE in the Running Object Table as "!VisualStudio.DTE.<version>:<process id>".
    private static object? FindDte(int processId)
    {
        System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(GetRunningObjectTable(0, out var table));
        System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(CreateBindCtx(0, out var context));
        table.EnumRunning(out var monikers);
        var found = new System.Runtime.InteropServices.ComTypes.IMoniker[1];
        while (monikers.Next(1, found, IntPtr.Zero) == 0)
        {
            found[0].GetDisplayName(context, null, out var name);
            if (name.StartsWith("!VisualStudio.DTE.", StringComparison.Ordinal) && name.EndsWith(":" + processId, StringComparison.Ordinal))
            {
                table.GetObject(found[0], out var dte);
                return dte;
            }
        }

        return null;
    }

    [System.Runtime.InteropServices.DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out System.Runtime.InteropServices.ComTypes.IRunningObjectTable table);

    [System.Runtime.InteropServices.DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out System.Runtime.InteropServices.ComTypes.IBindCtx context);

    /// <summary>Opens a menu path from the main menu bar and invokes the last item.</summary>
    public void ClickMenu(params string[] path)
    {
        this.log("Menu: " + string.Join(" > ", path));
        Exception? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                this.Main.Focus();
                var menuBar = this.Main.FindFirstDescendant(cf => cf.ByControlType(ControlType.MenuBar))
                    ?? throw new InvalidOperationException("The main menu bar was not found.");
                AutomationElement parent = menuBar;
                for (var i = 0; i < path.Length; i++)
                {
                    var item = this.FindMenuItem(parent, path[i])
                        ?? throw new InvalidOperationException($"Menu item '{path[i]}' was not found. Items here: {DescribeMenu(parent)}");
                    if (i == path.Length - 1)
                    {
                        item.AsMenuItem().Invoke();
                    }
                    else
                    {
                        item.AsMenuItem().Expand();
                        Thread.Sleep(500);
                        parent = item;
                    }
                }

                return;
            }
            catch (Exception ex) when (ex is not TimeoutException)
            {
                last = ex;
                this.log($"Menu attempt {attempt + 1} failed: {ex.Message}");
                CloseMenus();
                Thread.Sleep(3000);
            }
        }

        throw new InvalidOperationException("Could not use the menu " + string.Join(" > ", path), last);
    }

    /// <summary>
    /// Waits for a Visual Studio prompt (message box) whose text contains one of the expected phrases and returns
    /// its text. A prompt with a failure phrase fails at once with the prompt's text.
    /// </summary>
    public string WaitForPrompt(string[] success, string[] failure, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var nextScreenshot = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        var shot = 0;
        while (true)
        {
            foreach (var text in this.PromptTexts())
            {
                if (failure.Any(f => text.Contains(f, StringComparison.OrdinalIgnoreCase)))
                {
                    this.Screenshot("prompt-failure");
                    throw new InvalidOperationException("Visual Studio reported a failure: " + text);
                }

                if (success.Any(s => text.Contains(s, StringComparison.OrdinalIgnoreCase)))
                {
                    return text;
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out after {timeout} waiting for a prompt containing '{string.Join("' or '", success)}'.");
            }

            if (DateTime.UtcNow > nextScreenshot)
            {
                this.Screenshot($"waiting-{++shot:00}");
                nextScreenshot = DateTime.UtcNow + TimeSpan.FromMinutes(3);
            }

            this.DismissKnownDialogs();
            Thread.Sleep(3000);
        }
    }

    /// <summary>
    /// Picks <paramref name="choice"/> on a prompt that offers choices (a button or list entry with that exact name),
    /// waiting for the prompt to appear, then presses OK if the prompt still has one.
    /// </summary>
    public void Choose(string choice, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            foreach (var window in this.Dialogs())
            {
                var element = TryFind(() => window.FindFirstDescendant(cf => cf.ByName(choice)));
                if (element is null)
                {
                    continue;
                }

                this.Press(element, $"'{choice}' in '{window.Title}'");
                Thread.Sleep(1000);
                var ok = TryFind(() => window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("OK"))));
                if (ok is not null)
                {
                    this.Press(ok, $"OK in '{window.Title}'");
                }

                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                this.Screenshot("choice-missing");
                throw new TimeoutException($"Timed out after {timeout} waiting for a prompt offering '{choice}'.");
            }

            this.DismissKnownDialogs();
            Thread.Sleep(2000);
        }
    }

    /// <summary>The text of an open prompt that contains <paramref name="contains"/>, or null.</summary>
    public string? TryReadPrompt(string contains) =>
        this.PromptTexts().FirstOrDefault(t => t.Contains(contains, StringComparison.OrdinalIgnoreCase));

    /// <summary>Presses OK on the open prompt.</summary>
    public void ClosePrompt()
    {
        foreach (var window in this.Dialogs())
        {
            var ok = TryFind(() => window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("OK"))));
            if (ok is not null)
            {
                ok.AsButton().Invoke();
                return;
            }
        }
    }

    /// <summary>
    /// Gets past the first-run dialogs (sign in, theme): presses their "skip" or "not now" choice, and closes one
    /// that has no such choice after a minute. Only dialogs shown before the IDE window are ever closed, so the
    /// extension's own prompts are left alone.
    /// </summary>
    public void DismissKnownDialogs()
    {
        foreach (var window in this.Windows())
        {
            if (TryFind(() => window.FindFirstDescendant(cf => cf.ByControlType(ControlType.MenuBar))) is not null)
            {
                continue;
            }

            var handle = window.Properties.NativeWindowHandle.ValueOrDefault;
            var elements = TryFindAll(() => window.FindAllDescendants());
            if (!this.dialogsSeen.ContainsKey(handle))
            {
                this.dialogsSeen[handle] = DateTime.UtcNow;
                this.log($"Dialog '{window.Title}': " + string.Join(" | ", elements
                    .Select(e => (Type: e.Properties.ControlType.ValueOrDefault, Name: e.Properties.Name.ValueOrDefault))
                    .Where(e => !string.IsNullOrWhiteSpace(e.Name))
                    .Select(e => $"{e.Type} '{e.Name}'")
                    .Take(60)));
                this.Screenshot($"dialog-{this.dialogsSeen.Count:00}");
            }

            var choice = elements.FirstOrDefault(e => e.Properties.Name.ValueOrDefault is { } name && FirstRunChoice.IsMatch(name.Trim()));
            if (choice is not null)
            {
                this.Press(choice, $"'{window.Title}': '{choice.Properties.Name.ValueOrDefault}'");
                continue;
            }

            if (this.mainWindow is null && DateTime.UtcNow - this.dialogsSeen[handle] > TimeSpan.FromMinutes(1))
            {
                this.log($"Closing '{window.Title}', which has no skip choice.");
                try
                {
                    window.Close();
                }
                catch (Exception ex)
                {
                    this.log($"Could not close '{window.Title}': {ex.Message}");
                }

                Thread.Sleep(2000);
            }
        }
    }

    public void Screenshot(string name)
    {
        try
        {
            var path = Path.Combine(this.artifacts, name + ".png");
            Capture.Screen().ToFile(path);
            this.log($"Screenshot: {path}");
        }
        catch (Exception ex)
        {
            this.log($"Screenshot '{name}' failed: {ex.Message}");
        }
    }

    /// <summary>Writes the open windows and their text to the log, to see what Visual Studio was showing.</summary>
    public void LogWindows()
    {
        foreach (var window in this.Windows())
        {
            this.log($"Window '{window.Title}': {string.Join(" | ", Texts(window).Take(40))}");
        }
    }

    /// <summary>Logs what the Debug Target (launch profile) toolbar button shows.</summary>
    public void LogDebugTarget()
    {
        var target = TryFind(() => this.Main.FindFirstDescendant(cf => cf.ByName("Debug Target")));
        var parts = target is null
            ? []
            : new[] { target }.Concat(TryFindAll(() => target.FindAllDescendants()))
                .Select(e => $"{e.Properties.ControlType.ValueOrDefault} '{e.Properties.Name.ValueOrDefault}' '{e.Properties.HelpText.ValueOrDefault}'");
        this.log("Debug Target: " + string.Join(" | ", parts));
    }

    /// <summary>
    /// Logs the text of every console window (the Dev Container profile's launcher runs in one) and saves it as
    /// console-N.txt, to see what the launcher printed.
    /// </summary>
    public void LogConsoles()
    {
        var index = 0;
        foreach (var window in TryFindAll(() => this.automation.GetDesktop().FindAllChildren(cf => cf.ByControlType(ControlType.Window))))
        {
            var className = window.Properties.ClassName.ValueOrDefault ?? string.Empty;
            if (className is not ("ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS") && !(window.Properties.Name.ValueOrDefault ?? string.Empty).Contains("powershell", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Windows Terminal exposes the buffer through the Text pattern of its terminal control, deeper in the tree.
            var withText = new[] { window }.Concat(TryFindAll(() => window.FindAllDescendants()))
                .FirstOrDefault(e => { try { return e.Patterns.Text.IsSupported; } catch (Exception) { return false; } });
            var text = withText is not null && withText.Patterns.Text.TryGetPattern(out var pattern)
                ? pattern.DocumentRange.GetText(-1).TrimEnd()
                : "(no text available)";
            var path = Path.Combine(this.artifacts, $"console-{++index}.txt");
            File.WriteAllText(path, text);
            this.log($"Console window '{window.Properties.Name.ValueOrDefault}' ({className}), last lines:{Environment.NewLine}{string.Join(Environment.NewLine, text.Split('\n').TakeLast(40))}");
        }

        if (index == 0)
        {
            this.log("No console window is open.");
        }
    }

    /// <summary>Shows the Output window and saves a screenshot and, when UI Automation exposes it, its text.</summary>
    public void SaveOutputPane()
    {
        if (this.mainWindow is null)
        {
            return;
        }

        try
        {
            this.ClickMenu("View", "Output");
            Thread.Sleep(2000);
            this.Screenshot("output-window");
            foreach (var editor in this.Main.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit).Or(cf.ByControlType(ControlType.Document))))
            {
                if (editor.Patterns.Text.TryGetPattern(out var text))
                {
                    var content = text.DocumentRange.GetText(200_000);
                    if (content.Contains("dev container", StringComparison.OrdinalIgnoreCase))
                    {
                        File.WriteAllText(Path.Combine(this.artifacts, "output-dev-containers.txt"), content);
                        this.log("Saved the Dev Containers output pane.");
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            this.log($"Could not save the Output window: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try
        {
            if (!this.process.HasExited)
            {
                this.process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }

        this.process.Dispose();
        this.automation.Dispose();
    }

    private void Press(AutomationElement element, string what)
    {
        this.log($"Pressing {what}.");
        try
        {
            if (element.Patterns.Invoke.TryGetPattern(out var invoke))
            {
                invoke.Invoke();
            }
            else
            {
                element.Click();
            }
        }
        catch (Exception ex)
        {
            this.log($"Could not press {what}: {ex.Message}");
        }

        Thread.Sleep(2000);
    }

    private AutomationElement? FindMenuItem(AutomationElement parent, string name)
    {
        // WPF puts a submenu's items under its menu item; fall back to the whole desktop for popups that are not.
        for (var i = 0; i < 10; i++)
        {
            var item = TryFind(() => parent.FindFirstDescendant(cf => cf.ByControlType(ControlType.MenuItem).And(cf.ByName(name))))
                ?? TryFind(() => this.automation.GetDesktop().FindFirstDescendant(cf => cf.ByControlType(ControlType.MenuItem).And(cf.ByName(name))));
            if (item is not null)
            {
                return item;
            }

            Thread.Sleep(1000);
        }

        return null;
    }

    private IEnumerable<string> PromptTexts()
    {
        foreach (var window in this.Dialogs())
        {
            var texts = Texts(window).ToList();
            if (texts.Count > 0)
            {
                yield return string.Join(" ", texts);
            }
        }
    }

    /// <summary>Modal dialogs: top-level devenv windows other than the IDE, and windows the IDE window owns.</summary>
    private IEnumerable<Window> Dialogs()
    {
        var main = this.mainWindow;
        foreach (var window in this.Windows())
        {
            if (main is not null && SameElement(window, main))
            {
                foreach (var owned in TryFindAll(() => window.FindAllChildren(cf => cf.ByControlType(ControlType.Window))))
                {
                    yield return owned.AsWindow();
                }

                continue;
            }

            yield return window;
        }
    }

    private IReadOnlyList<Window> Windows()
    {
        var ids = Process.GetProcessesByName("devenv").Select(p =>
        {
            using (p)
            {
                return p.Id;
            }
        }).ToHashSet();
        return TryFindAll(() => this.automation.GetDesktop().FindAllChildren(cf => cf.ByControlType(ControlType.Window)))
            .Where(w => ids.Contains(w.Properties.ProcessId.ValueOrDefault))
            .Select(w => w.AsWindow())
            .ToList();
    }

    private static IEnumerable<string> Texts(AutomationElement element) =>
        TryFindAll(() => element.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)))
            .Select(t => t.Properties.Name.ValueOrDefault)
            .OfType<string>()
            .Where(t => t.Trim().Length > 0);

    private static string DescribeMenu(AutomationElement parent) =>
        string.Join(", ", TryFindAll(() => parent.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem)))
            .Select(i => i.Properties.Name.ValueOrDefault)
            .Where(n => !string.IsNullOrEmpty(n))
            .Take(40));

    private static void CloseMenus()
    {
        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
    }

    private static bool IsAlive(AutomationElement element)
    {
        try
        {
            // Reading a property of a closed window throws.
            return element.Properties.ProcessId.Value != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool SameElement(AutomationElement a, AutomationElement b)
    {
        try
        {
            return a.Properties.NativeWindowHandle.ValueOrDefault == b.Properties.NativeWindowHandle.ValueOrDefault;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static AutomationElement? TryFind(Func<AutomationElement?> find)
    {
        try
        {
            return find();
        }
        catch (Exception)
        {
            // Windows come and go while Visual Studio starts; a vanished element just means "not found yet".
            return null;
        }
    }

    private static AutomationElement[] TryFindAll(Func<AutomationElement[]> find)
    {
        try
        {
            return find();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string FindDevenv()
    {
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        var startInfo = new ProcessStartInfo(vswhere) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in new[] { "-latest", "-prerelease", "-version", "[18.0,)", "-property", "productPath" })
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        var path = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return path.Length > 0 ? path : throw new InvalidOperationException("Visual Studio 2026 was not found (vswhere).");
    }
}
