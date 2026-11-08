namespace ChatAssistantVSIX.Utils
{
  using Markdig;
  using Markdig.Syntax;
  using Markdig.Syntax.Inlines;
  using Markdig.Extensions.Tables;
  using System.Windows;
  using System.Windows.Controls;
  using System.Windows.Documents;
  using System.Windows.Media;
  using System.Diagnostics;

  public static class MarkdownFlowDocument
  {

    private static readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static FlowDocument Convert(string markdown, string font, double size)
    {
      var doc = new FlowDocument();
      var markdownDoc = Markdig.Markdown.Parse(markdown, _pipeline);
      doc.FontFamily = new FontFamily(font);
      doc.FontSize = size;
      doc.SetResourceReference(FlowDocument.ForegroundProperty, VsBrushes.WindowTextKey);

      RenderBlocks(doc.Blocks, markdownDoc, markdown);
      return doc;
    }

    private static void RenderBlocks(BlockCollection target, ContainerBlock container, string markdown)
    {
      foreach (var block in container)
      {
        // Reuse the leaf renderers, then attach their blocks to the containing
        // list item, quote, table cell or document.
        var doc = new FlowDocument();
        switch (block)
        {
          case HeadingBlock heading:
            HandleHeading(doc, heading, markdown);
            break;

          case ParagraphBlock paragraph:
            HandleParagraph(doc, paragraph, markdown);
            break;

          case CodeBlock codeBlock:
            HandleCodeBlock(doc, codeBlock);
            break;

          case QuoteBlock quote:
            var section = new Section { Margin = new Thickness(10, 4, 0, 4), BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(8, 0, 0, 0) };
            section.SetResourceReference(System.Windows.Documents.Block.BorderBrushProperty, VsBrushes.ButtonShadowKey);
            RenderBlocks(section.Blocks, quote, markdown);
            doc.Blocks.Add(section);
            break;

          case ListBlock list:
            HandleListBlock(doc, list, markdown);
            break;

          case Markdig.Extensions.Tables.Table table:
            var rendered = new System.Windows.Documents.Table { CellSpacing = 0, Margin = new Thickness(0, 6, 0, 6) };
            var group = new TableRowGroup(); rendered.RowGroups.Add(group);
            foreach (Markdig.Extensions.Tables.TableRow row in table)
            {
              var outputRow = new System.Windows.Documents.TableRow(); group.Rows.Add(outputRow);
              foreach (Markdig.Extensions.Tables.TableCell cell in row)
              {
                var outputCell = new System.Windows.Documents.TableCell { Padding = new Thickness(6), BorderThickness = new Thickness(0, 0, 0, 1), ColumnSpan = System.Math.Max(1, cell.ColumnSpan) };
                outputCell.SetResourceReference(System.Windows.Documents.TableCell.BorderBrushProperty, VsBrushes.ButtonShadowKey);
                if (row.IsHeader) outputCell.FontWeight = FontWeights.Bold;
                RenderBlocks(outputCell.Blocks, cell, markdown);
                outputRow.Cells.Add(outputCell);
              }
            }
            doc.Blocks.Add(rendered);
            break;

          case ThematicBreakBlock:
            HandleThematicBreak(doc);
            break;

          default:
            // Keep unsupported Markdown visible as source rather than silently
            // removing it. HTML is displayed as text, never executed.
            if (block.Span.Start >= 0 && block.Span.End >= block.Span.Start && block.Span.End < markdown.Length)
              doc.Blocks.Add(new Paragraph(new Run(markdown.Substring(block.Span.Start, block.Span.Length))));
            break;
        }
        while (doc.Blocks.FirstBlock != null)
        {
          var renderedBlock = doc.Blocks.FirstBlock;
          doc.Blocks.Remove(renderedBlock); target.Add(renderedBlock);
        }
      }
    }

    private static void HandleHeading(FlowDocument doc, HeadingBlock heading, string markdown)
    {
      var paragraph = new Paragraph
      {
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, heading.Level == 1 ? 16 : 8, 0, 4)
      };

      // Set font size based on heading level
      paragraph.FontSize = heading.Level switch
      {
        1 => 24,
        2 => 20,
        3 => 16,
        _ => 14
      };

      ProcessInlines(paragraph, heading.Inline, markdown);
      doc.Blocks.Add(paragraph);
    }

    private static void HandleParagraph(FlowDocument doc, ParagraphBlock paragraph, string markdown)
    {
      var para = new Paragraph
      {
        Margin = new Thickness(0, 4, 0, 4)
      };
      ProcessInlines(para, paragraph.Inline, markdown);
      doc.Blocks.Add(para);
    }

    private static void HandleCodeBlock(FlowDocument doc, CodeBlock codeBlock)
    {
      var codeText = codeBlock.Lines.ToString();
      var panel = new DockPanel { Margin = new Thickness(0, 5, 0, 5) };
      var copy = new Button { Content = "Copy code", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 0, 4) };
      copy.Click += (_, _) =>
      {
        try { Clipboard.SetText(codeText); }
        catch (System.Exception ex) { Diag.OutputMsg("Copy code: " + ex.Message); }
      };
      DockPanel.SetDock(copy, Dock.Top); panel.Children.Add(copy);
      var box = new TextBox
      {
        Text = codeText, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
        FontFamily = new FontFamily("Consolas"), Padding = new Thickness(8), BorderThickness = new Thickness(1),
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
      };
      box.SetResourceReference(Control.BackgroundProperty, VsBrushes.ButtonFaceKey);
      box.SetResourceReference(Control.ForegroundProperty, VsBrushes.ButtonTextKey);
      box.SetResourceReference(Control.BorderBrushProperty, VsBrushes.ButtonShadowKey);
      panel.Children.Add(box);
      panel.SetBinding(FrameworkElement.MaxWidthProperty, new System.Windows.Data.Binding("ActualWidth") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(RichTextBox), 1) });
      doc.Blocks.Add(new BlockUIContainer(panel));
    }

    private static void HandleListBlock(FlowDocument doc, ListBlock list, string markdown)
    {
      var listControl = list.IsOrdered ? new List { MarkerStyle = TextMarkerStyle.Decimal }
                                     : new List { MarkerStyle = TextMarkerStyle.Disc };
      if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start) && start > 0) listControl.StartIndex = start;

      foreach (ListItemBlock item in list)
      {
        var listItem = new ListItem();
        RenderBlocks(listItem.Blocks, item, markdown);
        listControl.ListItems.Add(listItem);
      }
      doc.Blocks.Add(listControl);
    }

    private static void HandleThematicBreak(FlowDocument doc)
    {
      var separator = new Paragraph
      {
        BorderBrush = doc.TryFindResource(VsBrushes.AccentMediumKey) as Brush,
        BorderThickness = new Thickness(0, 0, 0, 1),
        Margin = new Thickness(0, 8, 0, 8)
      };
      separator.SetResourceReference(System.Windows.Documents.Block.BorderBrushProperty, VsBrushes.AccentMediumKey);
      doc.Blocks.Add(separator);
    }

    private static void ProcessInlines(Paragraph paragraph, ContainerInline inline, string markdown)
      => ProcessInlines(paragraph.Inlines, inline, markdown);

    private static void ProcessInlines(InlineCollection target, ContainerInline inline, string markdown)
    {
      if (inline == null) return;

      foreach (var current in inline)
      {
        switch (current)
        {
          case LiteralInline lit:
            target.Add(new Run(lit.Content.ToString()));
            break;

          case CodeInline code:
            var codeRun = new Run(code.Content) { FontFamily = new FontFamily("Consolas") };
            codeRun.SetResourceReference(TextElement.BackgroundProperty, VsBrushes.ButtonFaceKey);
            codeRun.SetResourceReference(TextElement.ForegroundProperty, VsBrushes.ButtonTextKey);
            target.Add(codeRun);
            break;
          case EmphasisInline emphasis:
            var span = new Span();
            if (emphasis.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
            else { if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeights.Bold; if (emphasis.DelimiterCount % 2 == 1) span.FontStyle = FontStyles.Italic; }
            ProcessInlines(span.Inlines, emphasis, markdown);
            target.Add(span);
            break;
          case LinkInline link:
            if (link.IsImage) { ProcessInlines(target, link, markdown); break; }
            if (!System.Uri.TryCreate(link.Url, System.UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http" && uri.Scheme != "mailto"))
            { ProcessInlines(target, link, markdown); break; }
            var hyperlink = new Hyperlink { NavigateUri = uri };
            hyperlink.SetResourceReference(TextElement.ForegroundProperty, VsBrushes.HighlightKey);
            ProcessInlines(hyperlink.Inlines, link, markdown);
            hyperlink.RequestNavigate += (_, args) =>
            {
              try { Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true }); args.Handled = true; }
              catch (System.Exception ex) { Diag.OutputMsg("Open link: " + ex.Message); }
            };
            target.Add(hyperlink);
            break;
          case LineBreakInline line:
            target.Add(line.IsHard ? (System.Windows.Documents.Inline)new LineBreak() : new Run(" "));
            break;
          case ContainerInline nested:
            ProcessInlines(target, nested, markdown);
            break;
          default:
            if (current.Span.Start >= 0 && current.Span.End >= current.Span.Start && current.Span.End < markdown.Length)
              target.Add(new Run(markdown.Substring(current.Span.Start, current.Span.Length)));
            break;
        }
      }
    }
  }
}
