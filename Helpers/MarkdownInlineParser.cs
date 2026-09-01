using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows.Documents;
using WpfColor = System.Windows.Media.Color;
using WpfBrush = System.Windows.Media.SolidColorBrush;
using WpfFont  = System.Windows.Media.FontFamily;

namespace QingDesk.Helpers
{
    /// <summary>
    /// 轻量级 Markdown 内联解析器（零依赖）。
    /// 支持的语法：
    ///   # ~ ######     标题（6 级字号 56/48/36/30/24/18 + 粗体）
    ///   **text**         粗体
    ///   *text*           斜体
    ///   ~~text~~         删除线
    ///   `text`           行内代码（等宽字体 + 浅底色）
    ///   [text](url)      超链接（可点击打开浏览器）
    ///                     也支持 mailto:（发邮件）/ file:///（打开本地文件或文件夹）
    ///   https://...      裸链接自动识别（无需 [text](url) 包裹）
    ///                     file:///... 与 mailto: 同样自动识别
    ///   C:\xxx           Windows 路径自动识别为本地链接（反斜杠/正斜杠均可）
    ///   ==text==         黄色荧光高亮
    /// </summary>
    public static class MarkdownInlineParser
    {
        // 基础字号（与 MainWindow.xaml 中 TitleText 的 FontSize 一致）
        public const double BaseSize = 13;

        // 标题字号（6 级）
        public const double H1Size = 56;
        public const double H2Size = 48;
        public const double H3Size = 36;
        public const double H4Size = 30;
        public const double H5Size = 24;
        public const double H6Size = 18;

        // 组合正则：粗体 | 删除线 | 代码 | 链接 | 斜体 | 高亮 | 裸链接
        // 顺序很重要：** 必须在 * 之前尝试匹配；链接必须在裸链接之前
        private static readonly Regex Pattern = new(
            @"\*\*(.+?)\*\*"                  // group 1: bold
          + @"|~~(.+?)~~"                     // group 2: strikethrough
          + @"|`([^`]+)`"                     // group 3: code
          + @"|\[([^\]]+)\]\(([^)]+)\)"       // group 4: link text, group 5: url
          + @"|\*(.+?)\*"                     // group 6: italic
          + @"|==(.+?)=="                     // group 7: highlight
          + @"|((?:https?|ftp|file)://[^\s]+|mailto:[^\s]+)", // group 8: bare url / file path / mail
            RegexOptions.Compiled);

        // 裸链接末尾常见的标点，不应算作 URL 的一部分
        private static readonly char[] TrailingPunctuation = { '.', ',', ';', ':', '!', '?', ')', ']', '}', '，', '。', '；', '：', '！', '？', '）', '】', '」', '》' };

        // Windows 盘符路径（C:\xxx 或 C:/xxx）
        private static readonly Regex DrivePath = new(@"^[A-Za-z]:[\\/]", RegexOptions.Compiled);

        /// <summary>
        /// 将 Markdown 文本解析为 WPF Inline 列表，并返回应使用的字号。
        /// </summary>
        public static (List<Inline> inlines, double fontSize) Parse(string text)
        {
            double fontSize = BaseSize;
            bool isHeader = false;

            // 检测行首标题标记（必须从最长的 ###### 开始匹配，否则 ## 会先命中）
            if (text.StartsWith("###### "))
            {
                text = text[7..];
                fontSize = H6Size;
                isHeader = true;
            }
            else if (text.StartsWith("##### "))
            {
                text = text[6..];
                fontSize = H5Size;
                isHeader = true;
            }
            else if (text.StartsWith("#### "))
            {
                text = text[5..];
                fontSize = H4Size;
                isHeader = true;
            }
            else if (text.StartsWith("### "))
            {
                text = text[4..];
                fontSize = H3Size;
                isHeader = true;
            }
            else if (text.StartsWith("## "))
            {
                text = text[3..];
                fontSize = H2Size;
                isHeader = true;
            }
            else if (text.StartsWith("# "))
            {
                text = text[2..];
                fontSize = H1Size;
                isHeader = true;
            }

            var inlines = ParseInline(text);

            // 标题默认粗体
            if (isHeader && inlines.Count > 0)
            {
                var bold = new Bold();
                foreach (var inline in inlines)
                    bold.Inlines.Add(inline);
                return (new List<Inline> { bold }, fontSize);
            }

            return (inlines, fontSize);
        }

        /// <summary>
        /// 递归解析行内格式。处理粗体/斜体/删除线/代码/链接，支持嵌套。
        /// </summary>
        private static List<Inline> ParseInline(string text)
        {
            var result = new List<Inline>();
            int lastIndex = 0;

            foreach (Match match in Pattern.Matches(text))
            {
                // 添加匹配前的普通文本
                if (match.Index > lastIndex)
                    AddRunsWithBreaks(result, text[lastIndex..match.Index]);

                if (match.Groups[1].Success) // **bold**
                {
                    var bold = new Bold();
                    bold.Inlines.AddRange(ParseInline(match.Groups[1].Value));
                    result.Add(bold);
                }
                else if (match.Groups[2].Success) // ~~strikethrough~~
                {
                    var span = new Span();
                    span.TextDecorations = System.Windows.TextDecorations.Strikethrough;
                    span.Inlines.AddRange(ParseInline(match.Groups[2].Value));
                    result.Add(span);
                }
                else if (match.Groups[3].Success) // `code`
                {
                    var span = new Span(new Run(match.Groups[3].Value));
                    span.FontFamily = new WpfFont("Consolas, Courier New, monospace");
                    span.Background = AdaptiveTheme.CodeBg; // 底色随背景自适应
                    result.Add(span);
                }
                else if (match.Groups[4].Success && match.Groups[5].Success) // [link](url)
                {
                    var linkText = match.Groups[4].Value;
                    var rawUrl = NormalizeUrl(match.Groups[5].Value);

                    // 尝试构造合法 URI；失败则退化为纯文本
                    if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
                        result.Add(CreateLink(linkText, uri));
                    else if (Uri.TryCreate("https://" + rawUrl, UriKind.Absolute, out var httpsUri))
                        result.Add(CreateLink(linkText, httpsUri)); // URL 缺少协议头时补 https://
                    else
                        result.Add(new Run($"[{match.Groups[4].Value}]({match.Groups[5].Value})")); // URL 不合法，整段当普通文本
                }
                else if (match.Groups[6].Success) // *italic*
                {
                    var italic = new Italic();
                    italic.Inlines.AddRange(ParseInline(match.Groups[6].Value));
                    result.Add(italic);
                }
                else if (match.Groups[7].Success) // ==highlight==
                {
                    var span = new Span();
                    span.Background = AdaptiveTheme.HighlightBg; // 底色/字色随背景自适应
                    span.Foreground = AdaptiveTheme.HighlightFg;
                    span.Inlines.AddRange(ParseInline(match.Groups[7].Value));
                    result.Add(span);
                }
                else if (match.Groups[8].Success) // 裸链接 https://... / file:///... / mailto:...
                {
                    var url = NormalizeUrl(TrimTrailingPunctuation(match.Groups[8].Value, out var trailing));
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                        result.Add(CreateLink(url, uri));
                    else
                        result.Add(new Run(url));
                    if (trailing.Length > 0)
                        result.Add(new Run(trailing));
                }

                lastIndex = match.Index + match.Length;
            }

            // 添加剩余普通文本
            if (lastIndex < text.Length)
                AddRunsWithBreaks(result, text[lastIndex..]);

            // 纯文本兜底
            if (result.Count == 0 && !string.IsNullOrEmpty(text))
                AddRunsWithBreaks(result, text);

            return result;
        }

        /// <summary>
        /// 将文本添加到 Inline 集合，遇换行符插入 LineBreak，实现多行任务正常换行显示。
        /// </summary>
        private static void AddRunsWithBreaks(List<Inline> result, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            // 统一换行符为 \n（\r\n / \r 都视为换行）
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");

            var parts = text.Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                    result.Add(new LineBreak());
                if (parts[i].Length > 0)
                    result.Add(new Run(parts[i]));
            }
        }

        /// <summary>
        /// 构造可点击的超链接（浏览器 / 邮件客户端 / 资源管理器，由系统按 URI 协议分发）。
        /// file:/// 链接改用解码后的本地路径启动，规避带空格/中文路径经 ShellExecute 的编码兼容问题。
        /// </summary>
        private static Hyperlink CreateLink(string text, Uri uri)
        {
            var link = new Hyperlink(new Run(text))
            {
                NavigateUri = uri,
                Foreground = AdaptiveTheme.Link // 链接色随背景自适应
            };
            link.RequestNavigate += (s, e) =>
            {
                try
                {
                    var target = e.Uri.IsFile ? e.Uri.LocalPath : e.Uri.AbsoluteUri;
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(target)
                        { UseShellExecute = true });
                }
                catch { /* 忽略打开失败 */ }
            };
            return link;
        }

        /// <summary>
        /// 规范化 URL：Windows 路径统一为 file:/// URI（反斜杠 → 正斜杠，补全协议头）。
        /// 支持：C:\xxx、C:/xxx、file:///C:\xxx、file://C:\xxx、标准 URI 原样返回。
        /// </summary>
        private static string NormalizeUrl(string raw)
        {
            raw = raw.Trim();

            // 标准前缀：file:///C:\x → file:///C:/x（仅替换反斜杠）
            if (raw.StartsWith("file:///", System.StringComparison.OrdinalIgnoreCase))
                return raw.Replace('\\', '/');

            // file://C:\x（少一个斜杠）→ file:///C:/x
            if (raw.StartsWith("file://", System.StringComparison.OrdinalIgnoreCase))
                return "file:///" + raw.Substring(7).Replace('\\', '/');

            // 盘符路径 C:\x 或 C:/x → file:///C:/x
            if (DrivePath.IsMatch(raw))
                return "file:///" + raw.Replace('\\', '/');

            return raw;
        }

        /// <summary>
        /// 去除裸链接末尾不属于 URL 的标点（如句号、逗号），返回被截掉的部分。
        /// </summary>
        private static string TrimTrailingPunctuation(string url, out string trimmed)
        {
            trimmed = string.Empty;
            int end = url.Length;
            while (end > 0 && Array.IndexOf(TrailingPunctuation, url[end - 1]) >= 0)
                end--;
            if (end < url.Length)
            {
                trimmed = url[end..];
                url = url[..end];
            }
            return url;
        }
    }
}
