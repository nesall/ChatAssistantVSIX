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

      // Hook per-view key events (only active when this text view has focus)
      //view_.VisualElement.PreviewKeyDown += OnPreviewKeyDown;
      view_.Caret.PositionChanged += OnCaretPositionChanged;
    }

    public void Show(string text)
    {
      ThreadHelper.ThrowIfNotOnUIThread();
      Clear();

      var line = view_.Caret.Position.BufferPosition.GetContainingLine();
      anchor_ = view_.TextSnapshot.CreateTrackingPoint(line.End, PointTrackingMode.Positive);

      adornment_ = new GhostAdornment(text);
      adornment_.AcceptClicked += Accept;
      adornment_.RejectClicked += Clear;

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

    // WPF key handler wired to the view's VisualElement. Uses System.Windows.Input.KeyEventArgs.
    //private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    //{
    //  Debug.WriteLine($"OnPreviewKeyDown {e.Key}");
    //  if (adornment_ == null) return;
    //  ThreadHelper.ThrowIfNotOnUIThread();
    //  if (e.Key == Key.NumPad1 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
    //  {
    //    Accept();
    //    e.Handled = true;
    //  }
    //  else if (e.Key == Key.Escape)
    //  {
    //    Clear();
    //    e.Handled = true;
    //  }
    //}

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
      //view_.VisualElement.PreviewKeyDown -= OnPreviewKeyDown;
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
