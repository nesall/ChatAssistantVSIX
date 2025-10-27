using ChatAssistantVSIX.ToolWindows;
using Microsoft.VisualStudio.Imaging;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ChatAssistantVSIX
{
  public class MyToolWindow : BaseToolWindow<MyToolWindow>
  {
    public override string GetTitle(int toolWindowId) => "My Tool Window";

    public override Type PaneType => typeof(Pane);

    public override async Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
    {
      ToolWindowMessenger toolWindowMessenger = await Package.GetServiceAsync<ToolWindowMessenger, ToolWindowMessenger>();
      return new MyToolWindowControl(toolWindowMessenger);
    }

    [Guid("822ff34f-53b6-47c4-a1c6-c84b9464f79c")]
    internal class Pane : ToolkitToolWindowPane
    {
      public Pane()
      {
        BitmapImageMoniker = KnownMonikers.ToolWindow;
        ToolBar = new CommandID(PackageGuids.ChatAssistantVSIX, PackageIds.TWindowToolbar);
      }
    }
  }
}