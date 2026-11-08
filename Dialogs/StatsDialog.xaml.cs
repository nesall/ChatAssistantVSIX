using ChatAssistantVSIX.Utils;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Threading;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ChatAssistantVSIX.Dialogs
{
  public partial class StatsDialog : DialogWindow
  {
    private CancellationTokenSource request;
    private bool closed;
    private string displayedScope;

    public StatsDialog()
    {
      InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
      => ThreadHelper.JoinableTaskFactory.RunAsync(RefreshAsync).FireAndForget();

    private void OnRefresh(object sender, RoutedEventArgs e)
      => ThreadHelper.JoinableTaskFactory.RunAsync(RefreshAsync).FireAndForget();

    private async Task RefreshAsync()
    {
      if (closed || request != null) return;
      var scope = PhenixCodeCoreService.SolutionSettingsPath;
      if (!string.Equals(scope, displayedScope, StringComparison.OrdinalIgnoreCase)) StatisticsContent.DataContext = null;
      var cancellation = new CancellationTokenSource();
      request = cancellation;
      Refresh.IsEnabled = false;
      Status.Text = "Loading statistics…";
      try
      {
        var text = await PhenixCodeCoreService.GetStatsAsync(cancellation.Token);
        if (closed) return;
        if (!string.Equals(scope, PhenixCodeCoreService.SolutionSettingsPath, StringComparison.OrdinalIgnoreCase))
        {
          StatisticsContent.DataContext = null;
          Status.Text = "The solution changed. Refresh to load its statistics.";
          return;
        }
        var statistics = IndexStatistics.Parse(text, PhenixCodeCoreService.SolutionDir);
        StatisticsContent.DataContext = statistics;
        displayedScope = scope;
        Status.Text = "Statistics loaded.";
      }
      catch (Exception ex)
      {
        if (closed) return;
        if (!string.Equals(scope, PhenixCodeCoreService.SolutionSettingsPath, StringComparison.OrdinalIgnoreCase))
          StatisticsContent.DataContext = null;
        var message = ex is OperationCanceledException ? "The request timed out. Try Refresh." : ex.Message;
        Status.Text = "Could not load statistics. " + message;
        if (StatisticsContent.DataContext != null) Status.Text += " Displaying previously loaded statistics.";
        Diag.OutputMsg("[ERR] Statistics: " + message);
      }
      finally
      {
        request = null;
        cancellation.Dispose();
        if (!closed) Refresh.IsEnabled = true;
      }
    }

    private void OnClosed(object sender, EventArgs e)
    {
      closed = true;
      request?.Cancel();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
  }
}
