global using System;
global using Task = System.Threading.Tasks.Task;
global using Community.VisualStudio.Toolkit;
global using Microsoft.VisualStudio.Shell;
using System.Threading.Tasks;

namespace Community.VisualStudio.Toolkit
{
  public sealed class Solution { public string Name { get; set; } public string FullPath { get; set; } }
  public sealed class OutputWindowPane
  {
    public Guid Guid { get; } = Guid.NewGuid();
    public void WriteLine(string text) => Console.WriteLine(text);
  }
  public static class VS
  {
    public static class Windows
    {
      private static readonly OutputWindowPane Pane = new OutputWindowPane();
      public static Task<OutputWindowPane> CreateOutputWindowPaneAsync(string name, bool visible) => Task.FromResult(Pane);
      public static Task<OutputWindowPane> GetOutputWindowPaneAsync(Guid guid) => Task.FromResult(Pane);
    }
  }
}
namespace Microsoft.VisualStudio.Shell { }
namespace Markdig.Helpers { }
namespace ChatAssistantVSIX.Utils
{
  public static class Diag { public static void OutputMsg(string text) => Console.WriteLine(text); }
}
