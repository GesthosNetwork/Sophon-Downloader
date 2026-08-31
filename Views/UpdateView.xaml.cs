using SophonDownloader.Services;
using System.Text.RegularExpressions;
using System.Windows.Documents;


namespace SophonDownloader;

public partial class UpdateView : UserControl
{
    private readonly ApplicationUpdateService _updateService = new();
    private ApplicationUpdateInfo? _update;
    private bool _checking;

    public UpdateView()
    {
        InitializeComponent();
        CurrentVersionText.Text = App.Version;
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_checking)
            return;

        if (_update?.HasUpdate == true && !string.IsNullOrWhiteSpace(_update.DownloadUrl))
        {
            await InstallUpdateAsync();
            return;
        }

        _checking = true;
        ActionButton.IsEnabled = false;
        UpdateProgressBar.Visibility = Visibility.Collapsed;
        RenderReleaseNotes(ReleaseNotesRichTextBox, null);
        StatusText.Text = "Checking for the latest version…";

        try
        {
            _update = await _updateService.CheckForUpdateAsync();
            if (_update is null)
            {
                StatusText.Text = "Unable to read the latest release information.";
                ActionButton.Content = "Check Again";
                return;
            }

            if (!_update.HasUpdate)
            {
                StatusText.Text = $"You are using the latest available version ({App.Version}).";
                ActionButton.Content = "Check Again";
                return;
            }

            StatusText.Text = $"Version {_update.Version} is available.";
            RenderReleaseNotes(ReleaseNotesRichTextBox, _update.ReleaseNotes);
            ActionButton.Content = string.IsNullOrWhiteSpace(_update.DownloadUrl) ? "Update Unavailable" : $"Update to {_update.Version}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Update check failed: {ex.Message}";
            ActionButton.Content = "Try Again";
        }
        finally
        {
            _checking = false;
            ActionButton.IsEnabled = true;
        }
    }


        private static readonly Regex LinkRegex = new(@"\[([^\]]+)\]\((https?://[^\)]+)\)|(?<![\w])((?:https?://|www\.)[^\s<>\]\)]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex BoldRegex = new(@"(\*\*|__)(.+?)\1", RegexOptions.Compiled);
        private static readonly Regex InlineCodeRegex = new(@"`([^`]+)`", RegexOptions.Compiled);
        private static readonly Regex ItalicRegex = new(@"(\*|_)([^*_]+)\1", RegexOptions.Compiled);

        private void RenderReleaseNotes(RichTextBox box, string? markdown)
        {
            var document = new FlowDocument
            {
                PagePadding = new Thickness(14, 12, 14, 12),
                LineHeight = 1.4,
                FontFamily = box.FontFamily,
                FontSize = box.FontSize,
                Foreground = box.Foreground
            };

            var text = string.IsNullOrWhiteSpace(markdown)
                ? "No release notes were provided."
                : markdown.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

            var lines = text.Split('\n');
            Paragraph? currentList = null;
            string? lastListKind = null;

            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd();

                if (string.IsNullOrWhiteSpace(line))
                {
                    currentList = null;
                    lastListKind = null;
                    continue;
                }

                var heading = Regex.Match(line, @"^\s{0,3}(#{1,6})\s+(.+?)\s*#*\s*$");
                if (heading.Success)
                {
                    currentList = null;
                    lastListKind = null;
                    var level = heading.Groups[1].Value.Length;
                    var paragraph = CreateParagraph(0, 0, level <= 2 ? 8 : 5);
                    paragraph.FontSize = level switch
                    {
                        1 => 18,
                        2 => 15,
                        3 => 13,
                        4 => 12,
                        _ => 11
                    };
                    paragraph.FontWeight = FontWeights.SemiBold;
                    paragraph.Margin = new Thickness(0, level == 1 ? 5 : 3, 0, level <= 2 ? 7 : 4);
                    paragraph.Inlines.AddRange(ParseInlines(heading.Groups[2].Value));
                    document.Blocks.Add(paragraph);
                    continue;
                }

                var quote = Regex.Match(line, @"^\s*>\s?(.*)$");
                if (quote.Success)
                {
                    currentList = null;
                    lastListKind = null;
                    var quotedText = quote.Groups[1].Value;
                    var quotedHeading = Regex.Match(quotedText, @"^\s*(#{1,6})\s+(.+?)\s*#*\s*$");
                    var paragraph = CreateParagraph(2, 18, 5);
                    paragraph.BorderBrush = ResolveBrush(box, "AccentBrush");
                    paragraph.BorderThickness = new Thickness(3, 0, 0, 0);
                    paragraph.Padding = new Thickness(10, 3, 0, 3);
                    if (quotedHeading.Success)
                    {
                        paragraph.FontSize = quotedHeading.Groups[1].Value.Length switch
                        {
                            1 => 16,
                            2 => 14,
                            3 => 13,
                            _ => 12
                        };
                        paragraph.FontWeight = FontWeights.SemiBold;
                        paragraph.Inlines.AddRange(ParseInlines(quotedHeading.Groups[2].Value));
                    }
                    else
                    {
                        paragraph.Inlines.AddRange(ParseInlines(quotedText));
                    }
                    document.Blocks.Add(paragraph);
                    continue;
                }

                var bullet = Regex.Match(line, @"^\s*([-*+])\s+(.+)$");
                if (bullet.Success)
                {
                    if (currentList is null || lastListKind != "bullet")
                    {
                        currentList = CreateParagraph(0, 18, 0);
                        currentList.Margin = new Thickness(0, 0, 0, 2);
                        currentList.Inlines.Add(new Run("• ") { FontWeight = FontWeights.SemiBold });
                        document.Blocks.Add(currentList);
                        lastListKind = "bullet";
                    }
                    else
                    {
                        currentList.Inlines.Add(new Run("\n    ")); 
                        currentList.Inlines.Add(new Run("• ") { FontWeight = FontWeights.SemiBold });
                    }
                    currentList.Inlines.AddRange(ParseInlines(bullet.Groups[2].Value));
                    continue;
                }

                var ordered = Regex.Match(line, @"^\s*(\d+)[.)]\s+(.+)$");
                if (ordered.Success)
                {
                    if (currentList is null || lastListKind != "ordered")
                    {
                        currentList = CreateParagraph(0, 18, 0);
                        currentList.Margin = new Thickness(0, 0, 0, 2);
                        currentList.Inlines.Add(new Run($"{ordered.Groups[1].Value}. ") { FontWeight = FontWeights.SemiBold });
                        document.Blocks.Add(currentList);
                        lastListKind = "ordered";
                    }
                    else
                    {
                        currentList.Inlines.Add(new Run("\n    "));
                        currentList.Inlines.Add(new Run($"{ordered.Groups[1].Value}. ") { FontWeight = FontWeights.SemiBold });
                    }
                    currentList.Inlines.AddRange(ParseInlines(ordered.Groups[2].Value));
                    continue;
                }

                currentList = null;
                lastListKind = null;
                var body = CreateParagraph(0, 0, 4);
                body.Margin = new Thickness(0, 0, 0, 5);
                body.Inlines.AddRange(ParseInlines(line));
                document.Blocks.Add(body);
            }

            box.Document = document;
        }

        private Paragraph CreateParagraph(double topMargin, double leftIndent, double bottomMargin)
        {
            return new Paragraph
            {
                Margin = new Thickness(leftIndent, topMargin, 0, bottomMargin),
                Padding = new Thickness(0),
                TextIndent = 0,
                LineHeight = 1.35
            };
        }

        private List<Inline> ParseInlines(string text)
        {
            var result = new List<Inline>();
            ParseInlineSegment(text, result);
            return result;
        }

        private void ParseInlineSegment(string text, List<Inline> output)
        {
            var match = FindNextInlineToken(text);
            if (match is null)
            {
                if (!string.IsNullOrEmpty(text))
                    output.Add(new Run(text));
                return;
            }

            if (match.Value.Start > 0)
                output.Add(new Run(text[..match.Value.Start]));

            switch (match.Value.Type)
            {
                case TokenType.Bold:
                    var bold = new Bold();
                    bold.Inlines.AddRange(ParseInlines(match.Value.Content));
                    output.Add(bold);
                    break;
                case TokenType.Italic:
                    var italic = new Italic();
                    italic.Inlines.AddRange(ParseInlines(match.Value.Content));
                    output.Add(italic);
                    break;
                case TokenType.Code:
                    output.Add(new Run(match.Value.Content)
                    {
                        FontFamily = new FontFamily("Consolas"),
                        Background = new SolidColorBrush(Color.FromArgb(32, 128, 128, 128))
                    });
                    break;
                case TokenType.Link:
                    var hyperlink = new Hyperlink(new Run(match.Value.Content))
                    {
                        NavigateUri = new Uri(match.Value.Url!),
                        Foreground = ResolveStaticAccentBrush()
                    };
                    hyperlink.RequestNavigate += Hyperlink_RequestNavigate;
                    output.Add(hyperlink);
                    break;
            }

            ParseInlineSegment(text[(match.Value.End + 1)..], output);
        }

        private (TokenType Type, int Start, int End, string Content, string? Url)? FindNextInlineToken(string text)
        {
            var candidates = new List<(TokenType Type, Match Match, string Content, string? Url)>();

            var link = LinkRegex.Match(text);
            if (link.Success)
            {
                var displayText = link.Groups[1].Success ? link.Groups[1].Value : link.Groups[3].Value;
                var url = link.Groups[2].Success ? link.Groups[2].Value : link.Groups[3].Value;
                if (url.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                    url = "https://" + url;
                candidates.Add((TokenType.Link, link, displayText, url));
            }

            var bold = BoldRegex.Match(text);
            if (bold.Success)
                candidates.Add((TokenType.Bold, bold, bold.Groups[2].Value, null));

            var code = InlineCodeRegex.Match(text);
            if (code.Success)
                candidates.Add((TokenType.Code, code, code.Groups[1].Value, null));

            var italic = ItalicRegex.Match(text);
            if (italic.Success)
                candidates.Add((TokenType.Italic, italic, italic.Groups[2].Value, null));

            if (candidates.Count == 0)
                return null;

            var first = candidates.OrderBy(x => x.Match.Index).First();
            return (first.Type, first.Match.Index, first.Match.Index + first.Match.Length - 1, first.Content, first.Url);
        }

        private static void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            if (e.Uri is null)
                return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = e.Uri.AbsoluteUri,
                    UseShellExecute = true
                });
                e.Handled = true;
            }
            catch {}
        }

        private Brush ResolveBrush(FrameworkElement element, string resourceKey)
        {
            return element.TryFindResource(resourceKey) as Brush ?? Brushes.Gray;
        }

        private Brush ResolveStaticAccentBrush() => Brushes.DodgerBlue;

        private enum TokenType
        {
            Bold, Italic, Code, Link
        }

    private async Task InstallUpdateAsync()
    {
        ActionButton.IsEnabled = false;
        UpdateProgressBar.Visibility = Visibility.Visible;
        UpdateProgressBar.Value = 0;
        StatusText.Text = $"Downloading and installing {_update!.Version}…";

        try
        {
            await _updateService.DownloadAndInstallAsync(
                _update, new Progress<double>(value => UpdateProgressBar.Value = value));
        }
        catch (Exception ex)
        {
            UpdateProgressBar.Visibility = Visibility.Collapsed;
            StatusText.Text = $"Update failed: {ex.Message}";
            ActionButton.Content = "Try Again";
            ActionButton.IsEnabled = true;
        }
    }
}
