using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using WinColor     = System.Windows.Media.Color;
using WinBrush     = System.Windows.Media.SolidColorBrush;
using WinColorConv = System.Windows.Media.ColorConverter;

namespace QingDesk
{
    /// <summary>
    /// 外观设置窗口：背景颜色（WinForms 调色板）+ 不透明度滑条。
    /// 所有改动实时回调预览；「保存」落盘，「取消/X」还原打开前的外观。
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly Action<string, double, bool> _preview;   // 实时应用到主窗口（颜色/不透明度/边框）
        private readonly Action<string, double, bool> _save;      // 保存：应用 + 写 settings.json
        private readonly string _origColor;
        private readonly double _origOpacity;
        private readonly bool   _origBorder;

        private string _color;
        private bool   _saved;   // 是否已点保存（决定关闭时是否还原）
        private bool   _ready;   // 防止 InitializeComponent 期间的初始事件回调

        public SettingsWindow(string color, double opacity, bool showBorder,
                              Action<string, double, bool> preview,
                              Action<string, double, bool> save)
        {
            InitializeComponent();

            _preview     = preview;
            _save        = save;
            _origColor   = color;
            _origOpacity = opacity;
            _origBorder  = showBorder;
            _color       = color;

            // 初始 UI（赋值会触发 ValueChanged，_ready 置真后才执行实时预览）
            _ready = true;
            OpacitySlider.Value  = Math.Clamp(Math.Round(opacity * 100), 0, 100);
            BorderCheckBox.IsChecked = showBorder;
            UpdatePreviews();
        }

        // ── 颜色选择：调用系统调色板 ─────────────────────
        private void PickColor_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true,   // 直接展开完整调色板（含自定义颜色）
                Color    = System.Drawing.ColorTranslator.FromHtml(_color)
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _color = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
                UpdatePreviews();
            }
        }

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            UpdatePreviews();
        }

        private void BorderCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            UpdatePreviews();
        }

        // ── 刷新预览并实时应用到主窗口 ───────────────────
        private void UpdatePreviews()
        {
            var c     = (WinColor)WinColorConv.ConvertFromString(_color);
            var alpha = (byte)Math.Round(OpacitySlider.Value * 255 / 100);
            bool showBorder = BorderCheckBox.IsChecked == true;

            ColorPreview.Background   = new WinBrush(c);
            EffectPreview.Background  = new WinBrush(WinColor.FromArgb(alpha, c.R, c.G, c.B));
            EffectPreview.BorderThickness = showBorder ? new Thickness(1) : new Thickness(0);
            OpacityText.Text          = $"{Math.Round(OpacitySlider.Value)}%";

            _preview(_color, OpacitySlider.Value / 100.0, showBorder);
        }

        // ── 保存 / 取消 ─────────────────────────────────
        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _saved = true;
            _save(_color, Math.Round(OpacitySlider.Value) / 100.0, BorderCheckBox.IsChecked == true);
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        // X / Alt+F4 关闭视为取消：还原为打开前的外观
        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_saved)
                _preview(_origColor, _origOpacity, _origBorder);
            base.OnClosing(e);
        }
    }
}
