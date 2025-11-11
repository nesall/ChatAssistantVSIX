using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using ChatAssistantVSIX.ToolWindows;

namespace ChatAssistantVSIX
{
  /// <summary>
  /// Helper base for commands that need to send a single string message to the tool window
  /// using the existing FireAndForget pattern.
  /// Derive your commands from this and call SendToolWindowMessageFireAndForget("...").
  /// </summary>
  internal abstract class MessengerCommand<T> : BaseCommand<T>
    where T : class, new()
  {
    /// <summary>
    /// Sends a message to the ToolWindowMessenger using the same RunAsync(...).FireAndForget() pattern.
    /// </summary>
    protected void SendToolWindowMessageFireAndForget(string message)
    {
      ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
      {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        ToolWindowMessenger messenger = await Package.GetServiceAsync<ToolWindowMessenger, ToolWindowMessenger>();
        messenger.Send(message);
      }).FireAndForget();
    }

    /// <summary>
    /// Async variant if the caller prefers awaiting the operation.
    /// </summary>
    protected Task SendToolWindowMessageAsync(string message)
    {
      return ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
      {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        ToolWindowMessenger messenger = await Package.GetServiceAsync<ToolWindowMessenger, ToolWindowMessenger>();
        messenger.Send(message);
      }).Task;
    }
  }
}