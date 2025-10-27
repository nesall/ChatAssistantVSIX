using ChatAssistantVSIX.ToolWindows;
using System.Windows;
using System.Windows.Controls;

namespace ChatAssistantVSIX
{
  public partial class MyToolWindowControl : UserControl
  {
    public ToolWindowMessenger ToolWindowMessenger = null;
    public MyToolWindowControl(ToolWindowMessenger toolWindowMessenger)
    {
      toolWindowMessenger ??= new ToolWindowMessenger();
      ToolWindowMessenger = toolWindowMessenger;
      toolWindowMessenger.MessageReceived += OnMessageReceived;
      InitializeComponent();
    }

    private void OnMessageReceived(object sender, string e)
    {
      VS.MessageBox.ShowWarning("ButtomSettingsCommand", "Button clicked");
    }

  }
}