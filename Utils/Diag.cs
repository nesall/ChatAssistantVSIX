using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChatAssistantVSIX.Utils
{
  class Diag
  {
    public static void OutputMsg(string s)
    {
      Debug.WriteLine(s);
      ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
      {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
          var pane = await PhenixCodeCoreService.GetPaneAsync();
          if (pane != null)
            await pane.WriteLineAsync($"[Debug] {s}");
        }
        catch (Exception) { }
      }).FireAndForget();
    }
  }
}
