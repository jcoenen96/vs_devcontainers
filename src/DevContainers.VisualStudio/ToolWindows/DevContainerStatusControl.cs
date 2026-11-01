using Microsoft.VisualStudio.Extensibility.UI;

namespace DevContainers.VisualStudio.ToolWindows;

/// <summary>The content of the status window; its XAML is DevContainerStatusControl.xaml (an embedded resource).</summary>
internal sealed class DevContainerStatusControl(DevContainerStatusViewModel model) : RemoteUserControl(model)
{
}
