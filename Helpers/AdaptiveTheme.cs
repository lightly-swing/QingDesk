using System;
using WinColor = System.Windows.Media.Color;
using WinBrush = System.Windows.Media.SolidColorBrush;

namespace QingDesk.Helpers
{
    /// <summary>
    /// 自适应主题画刷（单例）。按“谁在用”分两类，处理方式不同：
    ///
    /// 1. Markdown 富文本画刷（Link / HighlightBg / HighlightFg / CodeBg）：
    ///    解析器把画刷实例直接赋给 Inline 的属性，持有的是同一实例引用，
    ///    因此必须原地修改 Color 才能让已渲染的富文本即时变色。
    ///    这些画刷绝不放进资源字典或样式，WPF 不会冻结运行期属性值。
    ///
    /// 2. 控件画刷（勾选框/正文/标题栏/按钮等 7 个）：
    ///    通过 Resources + DynamicResource 被样式/模板引用。
    ///    WPF 会在封存样式/模板时自动冻结其中解析到的 Freezable
    ///    （样式可能跨线程使用，非冻结 Freezable 不能跨线程），
    ///    被冻结后无法原地改色（InvalidOperationException）。
    ///    因此 Update() 每次产出全新实例，由调用方写入资源字典，
    ///    DynamicResource 会自动重新解析到新画刷。
    /// </summary>
    public static class AdaptiveTheme
    {
        // ── Markdown 富文本（原地改色，仅供解析器直接引用） ──
        public static readonly WinBrush Link        = New(0xFF, 0x18, 0x5F, 0xA5); // 超链接
        public static readonly WinBrush HighlightBg = New(0xFF, 0xF7, 0xFF, 0x77); // ==高亮==底色（荧光黄预混白成实色，见 Update 内说明）
        public static readonly WinBrush HighlightFg = New(0xFF, 0x2B, 0x2B, 0x00); // 高亮文字（深橄榄）
        public static readonly WinBrush CodeBg      = New(0x22, 0x00, 0x00, 0x00); // `代码`底色

        // ── 控件画刷（每次 Update 替换为新实例，写入资源字典） ──
        public static WinBrush CheckBorder { get; private set; } = New(0x66, 0x00, 0x00, 0x00);
        public static WinBrush CheckMark   { get; private set; } = New(0xE6, 0x00, 0x00, 0x00);
        public static WinBrush CheckFill   { get; private set; } = New(0x22, 0x00, 0x00, 0x00);
        public static WinBrush Text        { get; private set; } = New(0xFF, 0x22, 0x22, 0x22); // 正文
        public static WinBrush TextDim     { get; private set; } = New(0x88, 0x00, 0x00, 0x00); // 已完成/占位符
        public static WinBrush HeaderText  { get; private set; } = New(0xFF, 0x33, 0x33, 0x33); // 标题栏文字
        public static WinBrush IconBtn     { get; private set; } = New(0xFF, 0x55, 0x55, 0x55); // 图标按钮字形

        /// <summary>
        /// 按背景色 + 不透明度更新全套配色（退化路径：采样失败时用中性灰近似桌面）。
        /// </summary>
        public static void Update(WinColor bg, double opacity)
        {
            double o = Math.Clamp(opacity, 0, 1);
            double r = bg.R * o + 128 * (1 - o);
            double g = bg.G * o + 128 * (1 - o);
            double b = bg.B * o + 128 * (1 - o);

            ApplyBranch(0.299 * r + 0.587 * g + 0.114 * b > 140);
        }

        /// <summary>
        /// 按真实感知色（窗口底下的桌面采样与背景色混合后的结果）切换主题。
        /// 低透明度时桌面内容占主导，采样比灰度启发式准确得多。
        /// </summary>
        public static void Update(WinColor perceived)
            => ApplyBranch(0.299 * perceived.R + 0.587 * perceived.G + 0.114 * perceived.B > 140);

        private static void ApplyBranch(bool isLightBg)
        {
            if (isLightBg)
            {
                // 浅背景 → 深色元素

                // Markdown 富文本：原地改色（已渲染的 Inline 即时刷新）
                SetColor(Link,        NewColor(0xFF, 0x18, 0x5F, 0xA5)); // 深蓝链接
                // 高亮用"预混合"：提前把半透明黄和固定底色算成最终色，再以纯实色绘制。
                // 否则半透明黄在渲染时要和底下的像素 alpha 混合，背景一暗黄就被染成褐色。
                // 预混后观感与纯白背景下完全一致，且任何背景/透明度下颜色都不再漂移。
                // 想调浓度：改第一个参数（黄的本色 RGB）或 alpha（0x88）。
                SetColor(HighlightBg, PreBlend(NewColor(0xFF, 0xF0, 0xFF, 0x00), 0x88, NewColor(0xFF, 0xFF, 0xFF, 0xFF))); // 黄@53% 混白
                SetColor(HighlightFg, NewColor(0xFF, 0x2B, 0x2B, 0x00)); // 深橄榄字，压住荧光底
                SetColor(CodeBg,      NewColor(0x22, 0x00, 0x00, 0x00));

                // 控件：全新实例（防止样式冻结后无法改色）
                CheckBorder = New(0x66, 0x00, 0x00, 0x00);
                CheckMark   = New(0xE6, 0x00, 0x00, 0x00);
                CheckFill   = New(0x22, 0x00, 0x00, 0x00);
                Text        = New(0xFF, 0x22, 0x22, 0x22);
                TextDim     = New(0x88, 0x00, 0x00, 0x00);
                HeaderText  = New(0xFF, 0x33, 0x33, 0x33);
                IconBtn     = New(0xFF, 0x55, 0x55, 0x55);
            }
            else
            {
                // 深背景 → 浅色元素

                SetColor(Link,        NewColor(0xFF, 0x8A, 0xB9, 0xE8)); // 浅蓝链接
                // 深底同样预混合，但底色用纯黑：混出来是固定的深金色，白字压得住。
                // 想更黄就把 alpha(0xAA) 调大，想给白字更多余量就调小——预混后颜色是锁死的。
                SetColor(HighlightBg, PreBlend(NewColor(0xFF, 0xF0, 0xFF, 0x00), 0xAA, NewColor(0xFF, 0x00, 0x00, 0x00))); // 黄@67% 混黑
                SetColor(HighlightFg, NewColor(0xFF, 0xFF, 0xFF, 0xFF)); // 白字
                SetColor(CodeBg,      NewColor(0x22, 0xFF, 0xFF, 0xFF));

                CheckBorder = New(0x66, 0xFF, 0xFF, 0xFF);
                CheckMark   = New(0xE6, 0xFF, 0xFF, 0xFF);
                CheckFill   = New(0x22, 0xFF, 0xFF, 0xFF);
                Text        = New(0xFF, 0xF2, 0xF2, 0xF2);
                TextDim     = New(0x88, 0xFF, 0xFF, 0xFF);
                HeaderText  = New(0xFF, 0xE0, 0xE0, 0xE0);
                IconBtn     = New(0xFF, 0xBB, 0xBB, 0xBB);
            }
        }

        /// <summary>原地改色，带防御：画刷万一被冻结则跳过（不崩溃，仅不刷新）</summary>
        private static void SetColor(WinBrush brush, WinColor color)
        {
            if (!brush.IsFrozen) brush.Color = color;
        }

        /// <summary>
        /// 预混合：把"半透明前景色盖在固定底色上"的最终结果提前算出来，输出不透明的实色。
        /// 用实色绘制就不会再和窗口背景/桌面内容做 alpha 混合，颜色在任何背景下都稳定。
        /// </summary>
        private static WinColor PreBlend(WinColor top, byte alpha, WinColor bottom)
        {
            double a = alpha / 255.0;
            byte r = (byte)Math.Round(top.R * a + bottom.R * (1 - a));
            byte g = (byte)Math.Round(top.G * a + bottom.G * (1 - a));
            byte b = (byte)Math.Round(top.B * a + bottom.B * (1 - a));
            return NewColor(0xFF, r, g, b);
        }

        private static WinBrush New(byte a, byte r, byte g, byte b) => new(NewColor(a, r, g, b));

        private static WinColor NewColor(byte a, byte r, byte g, byte b) => WinColor.FromArgb(a, r, g, b);
    }
}
