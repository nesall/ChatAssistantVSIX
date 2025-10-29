using ChatAssistantVSIX.ToolWindows;

namespace ChatAssistantVSIX
{
  [Command(PackageIds.ButtonInsertCommand)]
  internal sealed class ButtonInsertCommand : BaseCommand<ButtonInsertCommand>
  {
    protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
    {
      await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
      ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
      {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        ToolWindowMessenger messenger = await Package.GetServiceAsync<ToolWindowMessenger, ToolWindowMessenger>();
        messenger.Send("ButtonInsertCommand");
      }).FireAndForget();
    }
  }
}
