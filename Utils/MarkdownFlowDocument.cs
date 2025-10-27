namespace ChatAssistantVSIX.Utils
{
  using Markdig;
  using Markdig.Syntax;
  using Markdig.Syntax.Inlines;
  using System.Windows;
  using System.Windows.Controls;
  using System.Windows.Documents;
  using System.Windows.Media;

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

      foreach (var block in markdownDoc)
      {
        switch (block)
        {
          case HeadingBlock heading:
            HandleHeading(doc, heading);
            break;

          case ParagraphBlock paragraph:
            HandleParagraph(doc, paragraph);
            break;

          case FencedCodeBlock codeBlock:
            HandleCodeBlock(doc, codeBlock);
            break;

          case QuoteBlock quote:
            HandleQuoteBlock(doc, quote);
            break;

          case ListBlock list:
            HandleListBlock(doc, list);
            break;

          case ThematicBreakBlock:
            HandleThematicBreak(doc);
            break;
        }
      }

      return doc;
    }

    private static void HandleHeading(FlowDocument doc, HeadingBlock heading)
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

      ProcessInlines(paragraph, heading.Inline, doc);
      doc.Blocks.Add(paragraph);
    }

    private static void HandleParagraph(FlowDocument doc, ParagraphBlock paragraph)
    {
      var para = new Paragraph
      {
        Margin = new Thickness(0, 4, 0, 4)
      };
      ProcessInlines(para, paragraph.Inline, doc);
      doc.Blocks.Add(para);
    }

    private static void HandleCodeBlock(FlowDocument doc, FencedCodeBlock codeBlock)
    {
      var codeText = codeBlock.Lines.ToString();
      var codePara = new Paragraph(new Run(codeText))
      {
        FontFamily = new FontFamily("Consolas"),
        Background = doc.TryFindResource(VsBrushes.ButtonFaceKey) as Brush,
        Foreground = doc.TryFindResource(VsBrushes.ButtonTextKey) as Brush,
        Margin = new Thickness(0, 4, 0, 4),
        Padding = new Thickness(8),        
        BorderBrush = doc.TryFindResource(VsBrushes.ButtonFaceKey) as Brush,
        BorderThickness = new Thickness(1)
      };
      doc.Blocks.Add(codePara);
    }

    private static void HandleQuoteBlock(FlowDocument doc, QuoteBlock quote)
    {
      var quotePara = new Paragraph
      {
        Margin = new Thickness(10, 4, 0, 4),
        BorderBrush = doc.TryFindResource(VsBrushes.ButtonFaceKey) as Brush,
        BorderThickness = new Thickness(2, 0, 0, 0),
        Padding = new Thickness(8, 0, 0, 0),
        Background = doc.TryFindResource(VsBrushes.ButtonFaceKey) as Brush,
        Foreground = doc.TryFindResource(VsBrushes.ButtonTextKey) as Brush
      };

      foreach (var block in quote)
      {
        if (block is ParagraphBlock paragraphBlock)
        {
          ProcessInlines(quotePara, paragraphBlock.Inline, doc);
        }
      }
      doc.Blocks.Add(quotePara);
    }

    private static void HandleListBlock(FlowDocument doc, ListBlock list)
    {
      var listControl = list.IsOrdered ? new List { MarkerStyle = TextMarkerStyle.Decimal }
                                     : new List { MarkerStyle = TextMarkerStyle.Disc };

      foreach (ListItemBlock item in list)
      {
        var listItem = new ListItem();
        foreach (var block in item)
        {
          if (block is ParagraphBlock paragraphBlock)
          {
            var para = new Paragraph();
            ProcessInlines(para, paragraphBlock.Inline, doc);
            listItem.Blocks.Add(para);
          }
        }
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
      doc.Blocks.Add(separator);
    }

    private static void ProcessInlines(Paragraph paragraph, ContainerInline inline, FrameworkContentElement el)
    {
      if (inline == null) return;

      foreach (var current in inline)
      {
        switch (current)
        {
          case LiteralInline lit:
            paragraph.Inlines.Add(new Run(lit.Content.ToString()));
            break;

          case CodeInline code:
            paragraph.Inlines.Add(new Run(code.Content)
            {
              FontFamily = new FontFamily("Consolas"),
              Background = el.TryFindResource(VsBrushes.ButtonFaceKey) as Brush,
              Foreground = el.TryFindResource(VsBrushes.ButtonTextKey) as Brush
            });
            break;

          case EmphasisInline emphasis:
            var run = new Run();
            if (emphasis.DelimiterChar == '*' || emphasis.DelimiterChar == '_')
            {
              run.FontStyle = emphasis.DelimiterCount >= 2 ? FontStyles.Italic : FontStyles.Normal;
              run.FontWeight = emphasis.DelimiterCount >= 2 ? FontWeights.Bold : FontWeights.Normal;
            }
            ProcessInlines(paragraph, emphasis, el);
            break;

          case LinkInline link:
            var hyperlink = new Hyperlink { NavigateUri = new Uri(link.Url) };
            ProcessInlines(paragraph, link, el);
            paragraph.Inlines.Add(hyperlink);
            break;
        }
      }
    }
  }
}
