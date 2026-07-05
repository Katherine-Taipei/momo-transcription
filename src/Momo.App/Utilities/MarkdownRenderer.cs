using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Momo.App.Utilities
{
    public static class MarkdownRenderer
    {
        private static HttpClient _httpClient = new HttpClient();
        public static HttpClient HttpClientInstance { get => _httpClient; set => _httpClient = value; }

        private static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Momo", "cache", "img");

        static MarkdownRenderer()
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
            }
            catch
            {
                // Ignore failure in read-only setups
            }
        }

        public static List<Control> Render(string markdown)
        {
            var controls = new List<Control>();
            if (string.IsNullOrEmpty(markdown)) return controls;

            try
            {
                var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
                var document = Markdown.Parse(markdown, pipeline);

                foreach (var block in document)
                {
                    var control = RenderBlock(block);
                    if (control != null)
                    {
                        controls.Add(control);
                    }
                }
            }
            catch (Exception ex)
            {
                var errorText = new TextBlock
                {
                    Text = $"Error rendering release notes: {ex.Message}",
                    Foreground = Brushes.Red,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 5)
                };
                controls.Add(errorText);
            }

            return controls;
        }

        private static Control? RenderBlock(Block block)
        {
            if (block is HeadingBlock headingBlock)
            {
                var textBlock = new TextBlock
                {
                    FontWeight = FontWeight.Bold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 12, 0, 6)
                };

                // Style according to header level
                textBlock.FontSize = headingBlock.Level switch
                {
                    1 => 18,
                    2 => 15,
                    3 => 13,
                    _ => 12
                };

                // Heading color matching Momo theme
                textBlock.Foreground = new SolidColorBrush(Color.Parse("#C084FC")); // Purple/violet

                if (headingBlock.Inline != null)
                {
                    RenderInlines(headingBlock.Inline, textBlock.Inlines);
                }

                return textBlock;
            }
            else if (block is ParagraphBlock paragraphBlock)
            {
                // Check if paragraph contains only standalone image(s) to render them as block elements
                if (paragraphBlock.Inline != null && IsStandaloneImageBlock(paragraphBlock.Inline, out var imageInline))
                {
                    return RenderImageBlock(imageInline);
                }

                var textBlock = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Foreground = Brushes.White,
                    Margin = new Thickness(0, 4, 0, 8),
                    LineHeight = 18
                };

                if (paragraphBlock.Inline != null)
                {
                    RenderInlines(paragraphBlock.Inline, textBlock.Inlines);
                }

                return textBlock;
            }
            else if (block is ListBlock listBlock)
            {
                var listStack = new StackPanel
                {
                    Margin = new Thickness(10, 4, 0, 8),
                    Spacing = 4
                };

                int index = 1;
                foreach (var item in listBlock)
                {
                    if (item is ListItemBlock listItem)
                    {
                        var rowGrid = new Grid
                        {
                            ColumnDefinitions = new ColumnDefinitions("Auto,*")
                        };

                        var bulletText = listBlock.IsOrdered ? $"{index}. " : "• ";
                        var bulletLabel = new TextBlock
                        {
                            Text = bulletText,
                            FontWeight = FontWeight.Bold,
                            Foreground = new SolidColorBrush(Color.Parse("#8B5CF6")),
                            Margin = new Thickness(0, 0, 6, 0),
                            VerticalAlignment = VerticalAlignment.Top
                        };
                        Grid.SetColumn(bulletLabel, 0);
                        rowGrid.Children.Add(bulletLabel);

                        var contentStack = new StackPanel();
                        foreach (var innerBlock in listItem)
                        {
                            var innerControl = RenderBlock(innerBlock);
                            if (innerControl != null)
                            {
                                contentStack.Children.Add(innerControl);
                            }
                        }
                        Grid.SetColumn(contentStack, 1);
                        rowGrid.Children.Add(contentStack);

                        listStack.Children.Add(rowGrid);
                        index++;
                    }
                }
                return listStack;
            }
            else if (block is CodeBlock codeBlock)
            {
                // Render code blocks as styled read-only cards
                var border = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#12131C")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#25273C")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10),
                    Margin = new Thickness(0, 6, 0, 6)
                };

                var codeText = new TextBlock
                {
                    FontFamily = new FontFamily("Consolas,Monospace"),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.Parse("#A78BFA")), // Purple-accented text
                    TextWrapping = TextWrapping.NoWrap,
                    Margin = new Thickness(0)
                };

                var codeBuilder = new StringBuilder();
                foreach (var line in codeBlock.Lines)
                {
                    codeBuilder.AppendLine(line.ToString());
                }

                codeText.Text = codeBuilder.ToString().TrimEnd();
                
                var scroll = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    Content = codeText
                };

                border.Child = scroll;
                return border;
            }

            return null;
        }

        private static bool IsStandaloneImageBlock(ContainerInline inlines, out LinkInline imageInline)
        {
            imageInline = null!;
            if (inlines.FirstChild == inlines.LastChild && inlines.FirstChild is LinkInline link && link.IsImage)
            {
                imageInline = link;
                return true;
            }
            return false;
        }

        private static void RenderInlines(ContainerInline inlines, InlineCollection targetCollection)
        {
            foreach (var inline in inlines)
            {
                if (inline is LiteralInline literalInline)
                {
                    targetCollection.Add(new Run(literalInline.Content.ToString()));
                }
                else if (inline is LineBreakInline)
                {
                    targetCollection.Add(new LineBreak());
                }
                else if (inline is EmphasisInline emphasisInline)
                {
                    var span = new Span();
                    if (emphasisInline.DelimiterCount == 1)
                    {
                        span.FontStyle = FontStyle.Italic;
                    }
                    else if (emphasisInline.DelimiterCount >= 2)
                    {
                        span.FontWeight = FontWeight.Bold;
                    }

                    if (emphasisInline.FirstChild != null)
                    {
                        RenderInlines(emphasisInline, span.Inlines);
                    }
                    targetCollection.Add(span);
                }
                else if (inline is LinkInline linkInline)
                {
                    if (linkInline.IsImage)
                    {
                        // Render inline images
                        var imageControl = RenderImageBlock(linkInline);
                        var uiContainer = new InlineUIContainer(imageControl);
                        targetCollection.Add(uiContainer);
                    }
                    else
                    {
                        // Render hyperlink buttons styled for contrast
                        var url = linkInline.Url ?? string.Empty;
                        
                        // Sanitize URL: allow only http/https, block javascript:
                        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && 
                            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        {
                            url = string.Empty;
                        }

                        var linkBtn = new Button
                        {
                            Background = Brushes.Transparent,
                            BorderThickness = new Thickness(0),
                            Padding = new Thickness(0),
                            Margin = new Thickness(2, 0, 2, 0),
                            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                            VerticalAlignment = VerticalAlignment.Center
                        };

                        // Add dynamic styling classes or custom content control templates
                        var btnContent = new TextBlock
                        {
                            Text = linkInline.Title ?? GetLinkText(linkInline),
                            Foreground = new SolidColorBrush(Color.Parse("#C084FC")), // Dark theme link contrast
                            TextDecorations = TextDecorations.Underline,
                            FontSize = 12
                        };
                        linkBtn.Content = btnContent;

                        if (!string.IsNullOrEmpty(url))
                        {
                            linkBtn.Click += (s, e) =>
                            {
                                try
                                {
                                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                    {
                                        FileName = url,
                                        UseShellExecute = true
                                    });
                                }
                                catch
                                {
                                    // Ignore failures
                                }
                            };
                        }

                        var uiContainer = new InlineUIContainer(linkBtn);
                        targetCollection.Add(uiContainer);
                    }
                }
            }
        }

        private static string GetLinkText(LinkInline link)
        {
            var sb = new StringBuilder();
            if (link.FirstChild != null)
            {
                foreach (var child in link)
                {
                    if (child is LiteralInline lit)
                    {
                        sb.Append(lit.Content.ToString());
                    }
                }
            }
            return sb.Length > 0 ? sb.ToString() : (link.Url ?? "Link");
        }

        private static Control RenderImageBlock(LinkInline imageInline)
        {
            var url = imageInline.Url ?? string.Empty;
            
            // Build cache file path
            string? cachedPath = null;
            if (!string.IsNullOrEmpty(url))
            {
                try
                {
                    using var sha = SHA256.Create();
                    var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(url))).Replace("-", "").ToLowerInvariant();
                    cachedPath = Path.Combine(CacheDir, hash + ".bin");
                }
                catch
                {
                    // Ignore hash failures
                }
            }

            var imageControl = new Image
            {
                MaxWidth = 480,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 10)
            };

            var placeholderText = new TextBlock
            {
                Text = $"[Image: {imageInline.Title ?? "Loading..."}]",
                FontSize = 10,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 4, 0, 8),
                FontStyle = FontStyle.Italic
            };

            var containerPanel = new Panel();
            containerPanel.Children.Add(placeholderText);

            if (string.IsNullOrEmpty(url) || cachedPath == null)
            {
                placeholderText.Text = "[Image failed to load: Invalid URL]";
                return containerPanel;
            }

            // Perform asynchronous download & cache loading
            Task.Run(async () =>
            {
                try
                {
                    if (File.Exists(cachedPath))
                    {
                        // Load directly from local cache
                        await LoadImageToUIAsync(cachedPath, imageControl, containerPanel, placeholderText);
                        return;
                    }

                    // Download image with a 10 seconds timeout
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var responseBytes = await _httpClient.GetByteArrayAsync(url, cts.Token);

                    // Write to cache
                    await File.WriteAllBytesAsync(cachedPath, responseBytes);

                    // Display on UI
                    await LoadImageToUIAsync(cachedPath, imageControl, containerPanel, placeholderText);
                }
                catch
                {
                    // Fallback to placeholder error
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        placeholderText.Text = $"[Image failed to load: {imageInline.Title ?? "Timeout/Error"}]";
                        placeholderText.Foreground = Brushes.Red;
                    });
                }
            });

            return containerPanel;
        }

        private static async Task LoadImageToUIAsync(string path, Image image, Panel container, TextBlock placeholder)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(path);
                using var ms = new MemoryStream(bytes);
                var bitmap = new Bitmap(ms);

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    image.Source = bitmap;
                    container.Children.Clear();
                    container.Children.Add(image);
                });
            }
            catch
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    placeholder.Text = "[Image load failed: Bad Format]";
                    placeholder.Foreground = Brushes.Red;
                });
            }
        }
    }
}
