using ChatAssistantVSIX.ToolWindows;

namespace ChatAssistantVSIX
{
  [Command(PackageIds.ButtonSettingsCommand)]
  internal sealed class ButtomSettingsCommand : BaseCommand<ButtomSettingsCommand>
  {
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {      
      await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
      ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
      {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        ToolWindowMessenger messenger = await Package.GetServiceAsync<ToolWindowMessenger, ToolWindowMessenger>();
        messenger.Send("ButtonSettingsCommand Message");
      }).FireAndForget();
    }
  }
}
