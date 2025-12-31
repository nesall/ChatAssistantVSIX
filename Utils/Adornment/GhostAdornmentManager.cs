using ChatAssistantVSIX.ToolWindows;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Diagnostics;
using System.Windows.Input;

namespace ChatAssistantVSIX.Utils.Adornment
{
  internal sealed class GhostAdornmentManager
  {
    private readonly IWpfTextView view_;
    private readonly IAdornmentLayer layer_;
    private GhostAdornment adornment_;
    private ITrackingPoint anchor_;

    internal GhostAdornmentManager(IWpfTextView view, IAdornmentLayer layer)
    {
      view_ = view;
      layer_ = layer;
      view_.LayoutChanged += OnLayoutChanged;
      view_.Closed += (_, _) => Dispose();
      view_.Caret.PositionChanged += OnCaretPositionChanged;
    }

    public void ShowProcessing()
    {
      ThreadHelper.ThrowIfNotOnUIThread();
      Show("Processing...", false);
    }

    public void Show(string text, bool showButtons = true)
    {
      ThreadHelper.ThrowIfNotOnUIThread();
      Clear();

      var line = view_.Caret.Position.BufferPosition.GetContainingLine();
      anchor_ = view_.TextSnapshot.CreateTrackingPoint(line.End, PointTrackingMode.Positive);

      adornment_ = new GhostAdornment(text);
      adornment_.AcceptClicked += Accept;
      adornment_.RejectClicked += Clear;

      if (!showButtons)
      {
        adornment_.AcceptButton.IsEnabled = false;
        adornment_.RejectButton.IsEnabled = false;
        adornment_.AcceptButton.Visibility = System.Windows.Visibility.Hidden;
        adornment_.RejectButton.Visibility = System.Windows.Visibility.Hidden;
        adornment_.AcceptButton.Width = 0;
        adornment_.RejectButton.Width = 0;
      }

      PositionAdornment();
    }

    public void Clear()
    {
      if (adornment_ == null) return;
      layer_.RemoveAdornment(adornment_);
      adornment_ = null;
      anchor_ = null;
    }

    private void PositionAdornment()
    {
      if (adornment_ == null) return;

      var snap = anchor_.GetPoint(view_.TextSnapshot);
      var line = view_.TextViewLines.GetTextViewLineContainingBufferPosition(snap);
      if (line == null) return;

      // horizontal = caret column
      var caretX = line.GetCharacterBounds(snap).Right;

      // vertical  = just under the line
      var y = line.Top + 0 * view_.ZoomLevel / 100.0; // 2 px, zoom-aware

      Canvas.SetLeft(adornment_, caretX);
      Canvas.SetTop(adornment_, y);

      layer_.RemoveAdornment(adornment_);
      layer_.AddAdornment(AdornmentPositioningBehavior.OwnerControlled, null, null, adornment_, null);
    }

    private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        => PositionAdornment();

    private void OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
    {
      if (adornment_ == null) return;
      var oldLine = e.OldPosition.BufferPosition.GetContainingLine();
      var newLine = e.NewPosition.BufferPosition.GetContainingLine();
      if (oldLine.LineNumber != newLine.LineNumber)
        Clear();
    }

    public void Accept()
    {
      ThreadHelper.ThrowIfNotOnUIThread();
      var text = adornment_.Text;
      Clear();

      var caretPos = view_.Caret.Position.BufferPosition;
      var edit = view_.TextBuffer.CreateEdit();
      edit.Insert(caretPos, text);
      var snapshot = edit.Apply();

      FormatInsertedText(snapshot, caretPos, text.Length);
    }

    public void Dispose()
    {
      // Unsubscribe event handlers
      view_.LayoutChanged -= OnLayoutChanged;
      Clear();
    }
    private void FormatInsertedText(ITextSnapshot snap, SnapshotPoint insertionPoint, int insertedLength)
    {
      // Select the inserted text
      //var snapshot = view_.TextSnapshot;
      var insertedSpan = new SnapshotSpan(snap, insertionPoint, insertedLength);
      view_.Selection.Select(insertedSpan, isReversed: false);
      view_.Caret.MoveTo(insertedSpan.End);

      // Execute format selection command
      ThreadHelper.JoinableTaskFactory.Run(async () =>
      {
        await VS.Commands.ExecuteAsync("Edit.FormatSelection");
      });
    }

  }
}
