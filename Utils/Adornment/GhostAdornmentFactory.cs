using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using System.ComponentModel.Composition;
using System.Diagnostics;

namespace ChatAssistantVSIX.Utils.Adornment
{
  [Export(typeof(IVsTextViewCreationListener))]
  [ContentType("text")]
  [TextViewRole(PredefinedTextViewRoles.Document)]
  internal sealed class GhostAdornmentFactory : IVsTextViewCreationListener
  {
    public void VsTextViewCreated(IVsTextView textViewAdapter)
    {
      // Ensure UI thread if you touch WPF/editor UI
      ThreadHelper.ThrowIfNotOnUIThread();

      var wpfView = textViewAdapter.ToIWpfTextView();   // Toolkit helper
      if (wpfView == null) return;

      IAdornmentLayer layer;
      try
      {
        layer = wpfView.GetAdornmentLayer("MyGhostText");
        _ = new GhostAdornmentManager(wpfView, layer);
      }
      catch (ArgumentOutOfRangeException ex)
      {
        Debug.WriteLine($"Adornment layer 'MyGhostText' not found: {ex.Message}");
      }
    }
  }
}