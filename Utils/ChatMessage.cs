using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ChatAssistantVSIX.Utils
{
  public class ChatMessage : INotifyPropertyChanged
  {
    public string Role { get; set; }
    private string content = "", status = "", provider = "";
    public string Content { get => content; set { content = value; Changed(); } }
    public string Status { get => status; set { status = value; Changed(); } }
    public string Provider { get => provider; set { provider = value; Changed(); } }
    private IReadOnlyList<string> sources = Array.Empty<string>();
    public IReadOnlyList<string> Sources
    {
      get => sources;
      internal set
      {
        var paths = (value ?? Array.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Replace('\\', '/')).Distinct(StringComparer.Ordinal).ToArray();
        if (sources.SequenceEqual(paths)) return;
        sources = Array.AsReadOnly(paths); Changed();
      }
    }
    public string[] SourceLabels
    {
      get
      {
        if (Sources.Count == 0) return Array.Empty<string>();
        var parts = Sources.Select(p => p.Split('/')).ToArray();
        int common = 0, limit = parts.Min(p => p.Length) - 1;
        while (common < limit && parts.All(p => p[common] == parts[0][common])) common++;
        return parts.Select(p => string.Join("/", p.Skip(common))).ToArray();
      }
    }
    public string CopyContent => Content + (Sources.Count == 0 ? "" : "\n\nSources:\n" + string.Join("\n", SourceLabels));
    public bool IsComplete { get; set; }
    public event PropertyChangedEventHandler PropertyChanged;
    private void Changed([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
  }
}
