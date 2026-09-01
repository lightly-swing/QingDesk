using System.Windows;
using System.Windows.Controls;

namespace QingDesk.Helpers
{
    /// <summary>
    /// 附加属性：让 TextBlock 直接绑定 Markdown 字符串并渲染为富文本。
    /// 用法：&lt;TextBlock h:MarkdownTextBlock.Markdown="{Binding Title}"/&gt;
    /// </summary>
    public static class MarkdownTextBlock
    {
        public static readonly DependencyProperty MarkdownProperty =
            DependencyProperty.RegisterAttached(
                "Markdown",
                typeof(string),
                typeof(MarkdownTextBlock),
                new PropertyMetadata(null, OnMarkdownChanged));

        public static string GetMarkdown(TextBlock element)
            => (string)element.GetValue(MarkdownProperty);

        public static void SetMarkdown(TextBlock element, string value)
            => element.SetValue(MarkdownProperty, value);

        private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TextBlock tb) return;

            var text = e.NewValue as string ?? "";
            var (inlines, fontSize) = MarkdownInlineParser.Parse(text);

            tb.Inlines.Clear();
            tb.FontSize = fontSize;          // 标题行使用更大字号，普通行恢复基础字号
            tb.Inlines.AddRange(inlines);
        }
    }
}
