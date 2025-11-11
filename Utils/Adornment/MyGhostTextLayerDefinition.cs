using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace ChatAssistantVSIX.Utils.Adornment
{
  internal sealed class MyGhostTextLayerDefinition
  {
    [Export]
    [Name("MyGhostText")]
    [Order(After = PredefinedAdornmentLayers.Text, Before = PredefinedAdornmentLayers.Selection)]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal AdornmentLayerDefinition adornmentLayer = null;
  }
}