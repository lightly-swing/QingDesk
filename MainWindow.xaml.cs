using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using QingDesk.Helpers;
using QingDesk.Models;
using QingDesk.Services;
using WinPoint       = System.Windows.Point;
using WinKey         = System.Windows.Input.KeyEventArgs;
using WinMouse       = System.Windows.Input.MouseEventArgs;
using WinDrag        = System.Windows.DragEventArgs;
using WinButton      = System.Windows.Controls.Button;
using WinDropEffects = System.Windows.DragDropEffects;
using WinColor       = System.Windows.Media.Color;
using WinBrush       = System.Windows.Media.SolidColorBrush;
using WinColorConv   = System.Windows.Media.ColorConverter;

namespace QingDesk
{
    public partial class MainWindow : Window
    {
        // ══════════════════════════════════════════════
        // Win32 P/Invoke — 鼠标穿透（仅内容区）
        // 注意：不使用 WS_EX_TRANSPARENT，避免整窗口穿透
        // 只通过 WM_NCHITTEST 返回 HTTRANSPARENT 实现内容区穿透
        // TitleBar（前36px）始终保留交互
        // ══════════════════════════════════════════════
        private const int WM_NCHITTEST  = 0x0084;
        private const int HTTRANSPARENT = -1;

        // ══════════════════════════════════════════════
        // 字段
        // ══════════════════════════════════════════════
        private readonly ObservableCollection<TodoItem> _items = new();
        private readonly MarkdownStorage _storage;

        // 拖拽排序
        private WinPoint  _dragStart;
        private TodoItem? _dragItem;

        // 原地编辑：当前正在编辑的条目（非空说明编辑框已展开）
        private TodoItem? _editingItem;

        // 窗口调整大小
        private bool _isResizing;
        private WinPoint _resizeStart;
        private double   _resizeStartW, _resizeStartH;

        // 置顶 / 鼠标模式（独立状态）
        private bool _isPinned   = false;  // 默认不置顶
        // 0=普通，1=全穿透，2=半穿透（仅超链接可点击）
        private int  _mouseMode  = 0;

        // 外观：背景颜色（#RRGGBB）与不透明度（0~1）、边框开关
        private string _bgColor   = "#FFFFFF";
        private double _bgOpacity = 0.40;
        private bool   _showBorder = true;
        private System.Windows.Threading.DispatcherTimer? _themeTimer;   // 移动/缩放后防抖重新采样
        private System.Windows.Threading.DispatcherTimer? _passThroughTimer;
        private System.Windows.Threading.DispatcherTimer? _wallpaperPollTimer; // 低频轮询正下方桌面颜色变化
        private System.Drawing.Color? _lastWallColor;   // 上次轮询感知到的正下方纯桌面色（反混合还原后，与透明度无关）

        // 托盘引用（用于同步状态）
        private TrayHelper? _tray;
        public bool IsPinned   => _isPinned;
        public int  MouseMode  => _mouseMode;   // 0=普通 1=全穿透 2=半穿透
        public bool IsPassThrough => _mouseMode != 0;   // 兼容旧判断
        public void SetTray(TrayHelper tray) => _tray = tray;

        // Win32 结构与接口定义
        private const int WM_MOVING = 0x0216;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        // ══════════════════════════════════════════════
        // 构造函数
        // ══════════════════════════════════════════════
        public MainWindow()
        {
            InitializeComponent();

            // 注册自适应主题画刷（单例，配色原地变化时所有引用处即时刷新，
            // 含已解析的 Markdown 富文本 Inline）
            Resources["CheckBorderBrush"] = AdaptiveTheme.CheckBorder;
            Resources["CheckMarkBrush"]   = AdaptiveTheme.CheckMark;
            Resources["CheckFillBrush"]   = AdaptiveTheme.CheckFill;
            Resources["TodoTextBrush"]    = AdaptiveTheme.Text;
            Resources["TodoTextDimBrush"] = AdaptiveTheme.TextDim;
            Resources["HeaderTextBrush"]  = AdaptiveTheme.HeaderText;
            Resources["IconBtnBrush"]     = AdaptiveTheme.IconBtn;

            // 确保窗口句柄创建，以便后续初始化设置和 Win32 钩子正常运行
            new WindowInteropHelper(this).EnsureHandle();

            _storage = new MarkdownStorage();
            foreach (var item in _storage.LoadTodos())
                _items.Add(item);
            TodoList.ItemsSource = _items;

            // 监听集合变化，同步空列表占位符
            _items.CollectionChanged += (s, e) => UpdateEmptyPlaceholder();

            // 初始位置：右上角
            var area = SystemParameters.WorkArea;
            Left   = area.Right - Width - 20;
            Height = Width * 1.3;
            Top    = area.Top + 20;

            // 载入并应用保存的设置
            LoadSettings();

            // 窗口移动/缩放后，底下的桌面内容变了 → 防抖后重新采样刷新自适应配色
            _themeTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            _themeTimer.Tick += (s, e) => { _themeTimer.Stop(); ApplyBackground(); };
            LocationChanged += (s, e) => RestartThemeRefresh();
            SizeChanged     += (s, e) => RestartThemeRefresh();

            // 低频轮询正下方桌面颜色：壁纸/桌面背景变化时，即使窗口没拖动也能自动刷新主题。
            // 颜色无实质变化时不触发重绘，避免无谓的性能开销。
            _wallpaperPollTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1500)
            };
            _wallpaperPollTimer.Tick += WallpaperPoll_Tick;
            _wallpaperPollTimer.Start();

            // 初始化占位符状态
            UpdateEmptyPlaceholder();

            // 根据置顶或穿透状态决定标题栏按钮初始透明度
            Loaded += (s, e) => {
                UpdateEmptyPlaceholder();
                SetTitleButtonsOpacity((_isPinned || _mouseMode != 0) ? 1 : 0);
            };
        }

        // ══════════════════════════════════════════════
        // 窗口初始化：挂钩 WndProc（用于穿透 hit-test）
        // ══════════════════════════════════════════════
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            hwndSource?.AddHook(WndProc);

            // 如果初始加载的设置中启用了鼠标穿透，立即应用穿透样式
            if (_mouseMode != 0)
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT);
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_MOVING)
            {
                // 获取当前鼠标所在的屏幕工作区（物理像素）
                POINT mousePos;
                GetCursorPos(out mousePos);
                var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(mousePos.X, mousePos.Y));
                var area = screen.WorkingArea;

                var rect = (RECT)Marshal.PtrToStructure(lParam, typeof(RECT))!;
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;

                // 限制窗口范围在当前屏幕工作区内
                if (rect.Left < area.Left)
                {
                    rect.Left = area.Left;
                    rect.Right = rect.Left + width;
                }
                else if (rect.Right > area.Right)
                {
                    rect.Right = area.Right;
                    rect.Left = rect.Right - width;
                }

                if (rect.Top < area.Top)
                {
                    rect.Top = area.Top;
                    rect.Bottom = rect.Top + height;
                }
                else if (rect.Bottom > area.Bottom)
                {
                    rect.Bottom = area.Bottom;
                    rect.Top = rect.Bottom - height;
                }

                Marshal.StructureToPtr(rect, lParam, true);
                handled = true;
                return new IntPtr(1);
            }
            return IntPtr.Zero;
        }

        // ══════════════════════════════════════════════
        // TitleBar 区域悬停：控制 TitleBar 按钮显示
        // ══════════════════════════════════════════════
        private void TitleBar_MouseEnter(object sender, WinMouse e) => SetTitleButtonsOpacity(1);
        private void TitleBar_MouseLeave(object sender, WinMouse e)
        {
            // 置顶或穿透状态下，标题栏按钮保持可见
            if (!_isPinned && _mouseMode == 0) SetTitleButtonsOpacity(0);
        }

        private void SetTitleButtonsOpacity(double opacity)
        {
            PinBtn.Opacity         = opacity;
            PassThroughBtn.Opacity = opacity;
            CloseBtn.Opacity       = opacity;
        }

        // ResizeGrip 区域悬停：控制 Grip 显示
        private void ResizeGrip_MouseEnter(object sender, WinMouse e)
        {
            if (!_isPinned) ResizeGripArea.Opacity = 1;
        }
        private void ResizeGrip_MouseLeave(object sender, WinMouse e) => ResizeGripArea.Opacity = 0;

        // ══════════════════════════════════════════════
        // 标题栏拖动
        // ══════════════════════════════════════════════
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1) DragMove();
        }

        // ══════════════════════════════════════════════
        // 标题栏按钮
        // ══════════════════════════════════════════════
        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Hide();

        private void PinBtn_Click(object sender, RoutedEventArgs e)
        {
            TogglePinState();
            _tray?.SyncPinMenuItem();
        }

        // 由托盘菜单触发（避免循环调用 SyncPinMenuItem）
        internal void TogglePinFromTray()
        {
            TogglePinState();
        }

        private void TogglePinState()
        {
            _isPinned    = !_isPinned;
            this.Topmost = _isPinned; // 真正控制置顶状态

            if (_isPinned)
            {
                PinBtn.Content = "📍";
                PinBtn.ToolTip = "取消置顶";
                SetTitleButtonsOpacity(1);
            }
            else
            {
                PinBtn.Content = "📌";
                PinBtn.ToolTip = "置顶";
                if (_mouseMode == 0) SetTitleButtonsOpacity(0);
            }
            SaveSettings();
        }

        // ══════════════════════════════════════════════
        // 鼠标穿透控制逻辑（三态循环：普通 → 全穿透 → 半穿透 → 普通）
        // 半穿透：仅鼠标悬停在超链接上时窗口可交互，其余位置照常穿透
        // ══════════════════════════════════════════════
        private void PassThroughBtn_Click(object sender, RoutedEventArgs e)
        {
            TogglePassThroughState();
            _tray?.SyncPassThroughMenuItem();
        }

        internal void TogglePassThroughFromTray()
        {
            TogglePassThroughState();
        }

        private void TogglePassThroughState()
        {
            // 三态循环：0=普通 → 1=全穿透 → 2=半穿透 → 0=普通
            _mouseMode = (_mouseMode + 1) % 3;

            switch (_mouseMode)
            {
                case 1: // 全穿透
                    PassThroughBtn.Content  = "◉";
                    PassThroughBtn.ToolTip  = "全穿透中 · 点击切换半穿透";
                    SetTitleButtonsOpacity(1);
                    ResizeGripArea.Opacity  = 0;
                    StartPassThroughTimer();
                    break;

                case 2: // 半穿透：仅超链接可点击
                    PassThroughBtn.Content  = "◐";
                    PassThroughBtn.ToolTip  = "半穿透中（仅链接可点）· 点击关闭穿透";
                    SetTitleButtonsOpacity(1);
                    ResizeGripArea.Opacity  = 0;
                    StartPassThroughTimer();
                    break;

                default: // 普通
                    _mouseMode = 0;
                    PassThroughBtn.Content  = "⊙";
                    PassThroughBtn.ToolTip  = "开启鼠标穿透";
                    if (!_isPinned) SetTitleButtonsOpacity(0);

                    StopPassThroughTimer();

                    // 确保退出穿透状态时移除穿透样式
                    var hwnd = new WindowInteropHelper(this).Handle;
                    int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                    SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle & ~WS_EX_TRANSPARENT);
                    break;
            }
            SaveSettings();
        }

        private void StartPassThroughTimer()
        {
            if (_passThroughTimer == null)
            {
                _passThroughTimer = new System.Windows.Threading.DispatcherTimer();
                _passThroughTimer.Interval = TimeSpan.FromMilliseconds(50);
                _passThroughTimer.Tick += PassThroughTimer_Tick;
            }
            _passThroughTimer.Start();
        }

        private void StopPassThroughTimer()
        {
            _passThroughTimer?.Stop();
        }

        private void PassThroughTimer_Tick(object? sender, EventArgs e)
        {
            if (_mouseMode == 0) return;

            POINT mousePos;
            GetCursorPos(out mousePos);

            // 交互热点：全穿透 = 标题栏三按钮；半穿透 = 三按钮 + 超链接
            bool isInteractive = IsOverInteractiveButton((short)mousePos.X, (short)mousePos.Y)
                              || (_mouseMode == 2 && IsOverHyperlink(mousePos.X, mousePos.Y));

            var hwnd = new WindowInteropHelper(this).Handle;
            int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);

            if (isInteractive)
            {
                // 鼠标在交互热点上，移去穿透样式以允许点击
                if ((extendedStyle & WS_EX_TRANSPARENT) != 0)
                {
                    SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle & ~WS_EX_TRANSPARENT);
                }
            }
            else
            {
                // 鼠标不在热点上，加入穿透样式以穿透至桌面
                if ((extendedStyle & WS_EX_TRANSPARENT) == 0)
                {
                    SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT);
                }
            }
        }

        // ══════════════════════════════════════════════
        // 半穿透：判断屏幕坐标处是否悬停在超链接文字上
        // ══════════════════════════════════════════════
        private bool IsOverHyperlink(int screenX, int screenY)
        {
            try
            {
                // 屏幕物理像素 → 窗口逻辑坐标
                var pt = PointFromScreen(new WinPoint(screenX, screenY));

                // 可视化树命中测试（窗口外返回 null）
                var result = System.Windows.Media.VisualTreeHelper.HitTest(this, pt);
                if (result?.VisualHit == null) return false;

                // 向上找最近的 TextBlock（Markdown 链接渲染在 TextBlock 的 Inlines 里）
                DependencyObject v = result.VisualHit;
                System.Windows.Controls.TextBlock? tb = null;
                while (v != null)
                {
                    if (v is System.Windows.Controls.TextBlock t) { tb = t; break; }
                    if (v is System.Windows.Media.Visual vis)
                        v = System.Windows.Media.VisualTreeHelper.GetParent(vis);
                    else
                        break;
                }
                if (tb == null) return false;

                // 窗口坐标 → TextBlock 局部坐标
                var local = TranslatePoint(pt, tb);

                // 只判定链接文字的实际渲染区域（而非整个 TextBlock 宽度）：
                // 遍历 Inlines（含嵌套 Span）中的 Hyperlink，逐字符取矩形并集后做命中测试
                return IsPointOnHyperlink(tb.Inlines, local);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>递归遍历 Inlines（含嵌套 Span/Bold/Italic），判断点是否落在任一 Hyperlink 的实际文字矩形内</summary>
        private static bool IsPointOnHyperlink(System.Windows.Documents.InlineCollection inlines, WinPoint p)
        {
            foreach (System.Windows.Documents.Inline inline in inlines)
            {
                if (inline is System.Windows.Documents.Hyperlink link)
                {
                    var rect = GetHyperlinkRect(link);
                    if (!rect.IsEmpty)
                    {
                        // 少量容差，避免热区过于苛刻（细线边缘）
                        rect.Inflate(1, 1);
                        if (rect.Contains(p)) return true;
                    }
                }
                // 粗体/斜体/高亮等 Span 内可能嵌套链接，递归查找
                if (inline is System.Windows.Documents.Span span)
                {
                    if (IsPointOnHyperlink(span.Inlines, p)) return true;
                }
            }
            return false;
        }

        /// <summary>取 Hyperlink 文字的实际渲染矩形（逐字符矩形并集，支持换行链接跨行）</summary>
        private static System.Windows.Rect GetHyperlinkRect(System.Windows.Documents.Hyperlink link)
        {
            var rect = System.Windows.Rect.Empty;
            var p    = link.ContentStart;
            var end  = link.ContentEnd;

            while (p != null && p.CompareTo(end) <= 0)
            {
                UnionCharacterRect(ref rect, p);
                var next = p.GetNextInsertionPosition(System.Windows.Documents.LogicalDirection.Forward);
                if (next == null || next == p) break;
                p = next;
            }

            // 兜底：显式并入结束位置的边缘矩形。
            // 若循环中途跳出（末尾插入点可能不可达），最后一个字符的右边缘会缺失，
            // 表现为命中区域"正好少一个字的宽度"
            UnionCharacterRect(ref rect, end);

            return rect;
        }

        /// <summary>把插入点 Forward/Backward 两个方向的边缘矩形并入 rect（边缘矩形宽度为 0，至少算 1px）</summary>
        private static void UnionCharacterRect(ref System.Windows.Rect rect, System.Windows.Documents.TextPointer p)
        {
            foreach (var dir in new[]
                     {
                         System.Windows.Documents.LogicalDirection.Forward,
                         System.Windows.Documents.LogicalDirection.Backward
                     })
            {
                var r = p.GetCharacterRect(dir);
                if (!r.IsEmpty)
                {
                    r.Width = Math.Max(r.Width, 1);
                    if (rect.IsEmpty) rect = r;
                    else rect.Union(r);
                }
            }
        }

        // ══════════════════════════════════════════════
        // 右下角自定义 ResizeGrip
        // ══════════════════════════════════════════════
        private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isResizing   = true;
            _resizeStart  = e.GetPosition(null);
            _resizeStartW = Width;
            _resizeStartH = Height;
            ((UIElement)sender).CaptureMouse();
            ((UIElement)sender).MouseMove        += ResizeGrip_MouseMove;
            ((UIElement)sender).MouseLeftButtonUp += ResizeGrip_MouseLeftButtonUp;
            e.Handled = true;
        }

        private void ResizeGrip_MouseMove(object sender, WinMouse e)
        {
            if (!_isResizing) return;
            var pos   = e.GetPosition(null);
            var delta = pos - _resizeStart;
            
            double newW = Math.Max(MinWidth, _resizeStartW + delta.X);
            double newH = Math.Max(MinHeight, _resizeStartH + delta.Y);

            // 限制最大缩放范围，防止超出当前屏幕的工作区
            var area = GetCurrentScreenWorkArea();
            if (Left + newW > area.Right)
            {
                newW = area.Right - Left;
            }
            if (Top + newH > area.Bottom)
            {
                newH = area.Bottom - Top;
            }

            Width  = newW;
            Height = newH;
        }

        // 获取当前窗口所在的屏幕工作区，并转换为 WPF 逻辑像素 (DIPs)
        private Rect GetCurrentScreenWorkArea()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
            var area = screen.WorkingArea;

            // 获取当前 DPI 比例以实现高分屏自适应
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            double dpiScaleX = dpi.DpiScaleX;
            double dpiScaleY = dpi.DpiScaleY;

            return new Rect(
                area.Left / dpiScaleX,
                area.Top / dpiScaleY,
                area.Width / dpiScaleX,
                area.Height / dpiScaleY
            );
        }

        private void ResizeGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isResizing = false;
            ((UIElement)sender).ReleaseMouseCapture();
            ((UIElement)sender).MouseMove        -= ResizeGrip_MouseMove;
            ((UIElement)sender).MouseLeftButtonUp -= ResizeGrip_MouseLeftButtonUp;
        }

        // ══════════════════════════════════════════════
        // 双击空白处：显示内联输入框
        // ══════════════════════════════════════════════
        // 富文本树遍历辅助（Markdown 渲染后，点击源可能是 Run/Hyperlink 等
        // TextElement，它们不在可视化树中，VisualTreeHelper.GetParent 会抛异常）
        // ══════════════════════════════════════════════
        private static DependencyObject GetParentSafe(DependencyObject obj)
        {
            if (obj is System.Windows.Media.Visual)
                return System.Windows.Media.VisualTreeHelper.GetParent(obj);
            if (obj is System.Windows.Documents.TextElement te)
                return te.Parent;   // Run/Span/Hyperlink → 所在 TextBlock 或上级 Span
            return System.Windows.LogicalTreeHelper.GetParent(obj);
        }

        /// <summary>沿混合树（可视化 + 富文本）向上找最近绑定 TodoItem 的元素</summary>
        private static TodoItem FindTodoItem(DependencyObject src)
        {
            while (src != null)
            {
                if (src is FrameworkElement fe && fe.DataContext is TodoItem feItem)
                    return feItem;
                if (src is FrameworkContentElement fce && fce.DataContext is TodoItem fceItem)
                    return fceItem;
                src = GetParentSafe(src);
            }
            return null;
        }

        // ══════════════════════════════════════════════
        private void Window_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // 如果不在新增（内联输入）过程中，忽略
            if (InlineInputArea.Visibility != Visibility.Visible) return;

            // 检查点击的原点，如果是三个功能按钮，不进行完成操作
            var src = e.OriginalSource as DependencyObject;
            while (src != null)
            {
                if (src == PinBtn || src == PassThroughBtn || src == CloseBtn)
                {
                    return;
                }
                // 如果双击的是输入框本身，保留默认双击选词功能，不进行提交
                if (src == InlineEditBox)
                {
                    return;
                }
                src = GetParentSafe(src);
            }

            // 在其他任意区域双击，完成并提交输入，收起输入框
            CommitInlineInput();
            e.Handled = true;
        }

        private void TodoList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // 半穿透模式下窗口仅在链接/按钮上“变实”，双击只服务于点击链接，
            // 不应触发编辑或新增（编辑请切回普通模式）
            if (_mouseMode == 2) return;

            // 如果点击源是按钮/勾选框或其子元素（防止连续删除、快速勾选被识别为双击进入编辑）
            var src = e.OriginalSource as DependencyObject;
            while (src != null)
            {
                if (src is System.Windows.Controls.Button) return;
                if (src is System.Windows.Controls.CheckBox) return;
                src = GetParentSafe(src);
            }

            // 双击在条目文本上 → 编辑该条目（Run 等富文本元素不在可视化树，需混合树查找）
            if (FindTodoItem(e.OriginalSource as DependencyObject) is TodoItem item)
            {
                StartInlineEdit(item, e.OriginalSource as DependencyObject);
                return;
            }

            // 双击空白处 → 显示内联输入框
            ShowInlineInput();
        }

        // ══════════════════════════════════════════════
        // 右键上下文菜单：对条目右键 = 编辑/删除 + 通用项；对空白处右键 = 通用项
        // ══════════════════════════════════════════════
        private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            // 输入框内右键 → 保留系统自带的复制/粘贴菜单
            var src = e.OriginalSource as DependencyObject;
            var cur = src;
            while (cur != null)
            {
                if (cur == InlineEditBox) return;
                cur = GetParentSafe(cur);
            }

            var menu = new ContextMenu();

            // ── 条目专属项（右键落在待办条目上时，含悬停的编辑/删除小按钮）──
            var todo = FindTodoItem(src);
            if (todo != null)
            {
                var mEdit = new MenuItem { Header = "✎  编辑" };
                mEdit.Click += (s2, _) => StartInlineEdit(todo, src);
                menu.Items.Add(mEdit);

                var mDelete = new MenuItem { Header = "✕  删除" };
                mDelete.Click += (s2, _) =>
                {
                    _items.Remove(todo);
                    _storage.SaveTodos(_items);
                    UpdateEmptyPlaceholder();
                };
                menu.Items.Add(mDelete);

                menu.Items.Add(new Separator());
            }

            // ── 通用项（与托盘菜单对齐）──
            var mAdd = new MenuItem { Header = "＋  新增待办" };
            mAdd.Click += (s2, _) => ShowInlineInput();
            menu.Items.Add(mAdd);

            var mPin = new MenuItem { Header = _isPinned ? "取消置顶" : "置顶" };
            mPin.Click += (s2, _) => { TogglePinState(); _tray?.SyncPinMenuItem(); };
            menu.Items.Add(mPin);

            var mPass = new MenuItem { Header = NextMouseModeLabel() };
            mPass.Click += (s2, _) => { TogglePassThroughState(); _tray?.SyncPassThroughMenuItem(); };
            menu.Items.Add(mPass);

            menu.Items.Add(new Separator());

            var mAppearance = new MenuItem { Header = "🎨  外观设置…" };
            mAppearance.Click += (s2, _) => OpenAppearanceSettings();
            menu.Items.Add(mAppearance);

            var mHide = new MenuItem { Header = "隐藏到托盘" };
            mHide.Click += (s2, _) => Hide();
            menu.Items.Add(mHide);

            var mExit = new MenuItem { Header = "退出" };
            mExit.Click += (s2, _) => System.Windows.Application.Current.Shutdown();
            menu.Items.Add(mExit);

            menu.PlacementTarget = RootBorder;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
            e.Handled = true;
        }

        /// <summary>穿透菜单项文字：显示"点击后将进入的下一个模式"</summary>
        private string NextMouseModeLabel() => _mouseMode switch
        {
            0 => "鼠标穿透（全穿透）",
            1 => "半穿透（仅链接可点）",
            _ => "关闭穿透",
        };

        private void ShowInlineInput()
        {
            InlineInputArea.Visibility = Visibility.Visible;
            InlineEditBox.Text         = string.Empty;
            InlineEditBox.Focus();
        }

        private void HideInlineInput()
        {
            InlineInputArea.Visibility = Visibility.Collapsed;
            InlineEditBox.Text         = string.Empty;
        }

        private void InlineEditBox_KeyDown(object sender, WinKey e)
        {
            if (e.Key == Key.Enter)
            {
                CommitInlineInput();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                HideInlineInput();
                e.Handled = true;
            }
        }

        private void InlineEditBox_LostFocus(object sender, RoutedEventArgs e)
        {
            // 失焦时自动取消（若未回车确认）
            HideInlineInput();
        }

        private void CommitInlineInput()
        {
            var text = InlineEditBox.Text.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                // 已移除字数限制，支持任意长度内容
                _items.Add(new TodoItem { Title = text });
                _storage.SaveTodos(_items);
                if (_items.Count > 0)
                    TodoList.ScrollIntoView(_items[^1]);
            }
            HideInlineInput();
        }

        private void UpdateEmptyPlaceholder()
        {
            EmptyPlaceholder.Visibility =
                _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ══════════════════════════════════════════════
        // 编辑 / 删除
        // ══════════════════════════════════════════════
        private void EditBtn_Click(object sender, RoutedEventArgs e)
        {
            var id   = (Guid)((WinButton)sender).Tag;
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item != null) StartInlineEdit(item, sender as DependencyObject);
        }

        // ══════════════════════════════════════════════
        // 原地编辑（取代原 EditDialog 二级小窗口）
        // ══════════════════════════════════════════════
        /// <summary>
        /// 在目标条目原位置展开一个 TextBox 就地编辑。
        /// </summary>
        private void StartInlineEdit(TodoItem item, DependencyObject? clickSource)
        {
            // 若正在编辑别处，先提交
            if (_editingItem != null) CommitInlineEdit();

            // 找到该条目对应的 ListViewItem 容器，用于定位覆盖框位置
            var container = clickSource != null
                ? FindAncestor<System.Windows.Controls.ListViewItem>(clickSource)
                : null;
            container ??= FindContainer(item);

            if (container == null) return;   // 罕见：条目容器尚未生成，放弃本次编辑

            _editingItem = item;

            // 把 ListViewItem 左上角坐标转换到覆盖层(EditOverlay)坐标系
            var origin = container.TranslatePoint(new WinPoint(0, 0), EditOverlay);
            double w = container.ActualWidth;
            double h = container.ActualHeight;

            // 覆盖框置于条目所在行，四周留小边距，尺寸要能完全盖住原条目：
            // 宽度 = 条目整行宽度；高度用 Auto（随内容增长），但 MinHeight 至少 = 原条目高度，
            // 保证初始就能盖住原条目（即使原条目是多行），输入更多行时继续自动长高。
            // 上限 MaxHeight=120（XAML）超长自动滚动。
            Canvas.SetLeft(EditOverlayBox, origin.X + 2);
            Canvas.SetTop(EditOverlayBox, origin.Y + 2);
            EditOverlayBox.Width  = Math.Max(50, w - 4);
            EditOverlayBox.MinHeight = Math.Max(26, h - 4);
            EditOverlayBox.Height = double.NaN;   // Auto：随文本高度增长

            EditOverlayBox.Text = item.Title;
            EditOverlay.Visibility = Visibility.Visible;

            // 关键：不能在 Collapsed→Visible 的同一帧立即 Focus()——此时元素尚未完成
            // Measure/Arrange/命中布局，Focus() 会失败，导致文本框拿不到键盘焦点：
            // 后果是「按回车不触发 KeyDown、点击外部不触发 LostFocus」，即用户反馈的
            // "回车确认/点击外部提交都没有"。必须在布局完成后再聚焦（Dispatcher 下个优先级）。
            // 用 BeginInvoke 推到渲染后，焦点一定能落在编辑框上。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                EditOverlayBox.Focus();
                EditOverlayBox.SelectAll();
                EditOverlayBox.ScrollToHome();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        /// <summary>沿可视化树向上找指定类型的祖先。</summary>
        private static T? FindAncestor<T>(DependencyObject src) where T : DependencyObject
        {
            while (src != null)
            {
                if (src is T t) return t;
                src = GetParentSafe(src);
            }
            return null;
        }

        /// <summary>按条目 Id 在 ItemContainerGenerator 中找对应的 ListViewItem。</summary>
        private System.Windows.Controls.ListViewItem? FindContainer(TodoItem item)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                var c = TodoList.ItemContainerGenerator.ContainerFromIndex(i) as System.Windows.Controls.ListViewItem;
                if (c?.DataContext == item) return c;
            }
            return null;
        }

        // 用 PreviewKeyDown（隧道事件）而非 KeyDown（冒泡）：
        // 当 AcceptsReturn=True 时，TextBox 的默认处理会在冒泡阶段前就把 Enter 吃掉
        // （插入换行并置 e.Handled=true），外层 KeyDown 根本收不到未处理的 Enter，
        // 导致"回车=换行而不是提交"。PreviewKeyDown 在默认处理前触发，可可靠拦截。
        private void EditOverlayBox_PreviewKeyDown(object sender, WinKey e)
        {
            // 未开启多行时不处理；已开多行但未按 Shift 的 Enter 视为确认
            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                CommitInlineEdit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelInlineEdit();
                e.Handled = true;
            }
        }

        private void EditOverlayBox_LostFocus(object sender, RoutedEventArgs e)
        {
            // 失焦时提交（若内容为空则取消）
            if (_editingItem != null) CommitInlineEdit();
        }

        /// <summary>
        /// 点击覆盖层上、但编辑框之外的区域 → 提交当前编辑。
        /// 这是"点击其他位置自动提交"的兜底：即便焦点因布局时序未能建立，
        /// 只要点击落在覆盖层上也能可靠提交（不依赖焦点）。
        /// </summary>
        private void EditOverlay_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // 只处理编辑框之外的区域；编辑框自身内部的点击交由 TextBox 正常处理
            if (e.OriginalSource is DependencyObject src
                && FindAncestor<System.Windows.Controls.TextBox>(src) != EditOverlayBox
                && _editingItem != null)
            {
                CommitInlineEdit();
                e.Handled = true;
            }
        }

        /// <summary>提交原地编辑：用输入内容覆盖原条目文本并保存。</summary>
        private void CommitInlineEdit()
        {
            var item = _editingItem;
            _editingItem = null;
            if (EditOverlay.Visibility == Visibility.Visible)
            {
                EditOverlay.Visibility = Visibility.Collapsed;
            }
            if (item == null) return;

            var text = EditOverlayBox.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;   // 空内容视为放弃本次修改，保留原文

            item.Title = text;
            var idx = _items.IndexOf(item);
            if (idx >= 0)
            {
                _items.RemoveAt(idx);
                _items.Insert(idx, item);
            }
            _storage.SaveTodos(_items);
        }

        /// <summary>取消原地编辑：放弃修改，仅收起覆盖框。</summary>
        private void CancelInlineEdit()
        {
            _editingItem = null;
            EditOverlay.Visibility = Visibility.Collapsed;
        }

        private void DeleteBtn_Click(object sender, RoutedEventArgs e)
        {
            var id   = (Guid)((WinButton)sender).Tag;
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item != null) { _items.Remove(item); _storage.SaveTodos(_items); }
        }
        
        private void TodoCheck_Changed(object sender, RoutedEventArgs e)
        {
            _storage.SaveTodos(_items);   // 勾选状态变了，立即落盘
        }


        // ══════════════════════════════════════════════
        // 拖拽排序
        // ══════════════════════════════════════════════
        private void TodoList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStart = e.GetPosition(null);
            _dragItem  = FindTodoItem(e.OriginalSource as DependencyObject);
        }

        private void TodoList_PreviewMouseMove(object sender, WinMouse e)
        {
            if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
            var pos   = e.GetPosition(null);
            var delta = pos - _dragStart;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            DragDrop.DoDragDrop(TodoList, _dragItem, WinDropEffects.Move);
            _dragItem = null;
        }

        private void TodoList_Drop(object sender, WinDrag e)
        {
            if (_dragItem == null) return;
            var target = FindTodoItem(e.OriginalSource as DependencyObject);
            if (target == null || target == _dragItem) return;
            var oldIdx = _items.IndexOf(_dragItem);
            var newIdx = _items.IndexOf(target);
            if (oldIdx >= 0 && newIdx >= 0) { _items.Move(oldIdx, newIdx); _storage.SaveTodos(_items); }
        }

        // ══════════════════════════════════════════════
        // 按钮物理像素命中测试
        // ══════════════════════════════════════════════
        private bool IsOverInteractiveButton(short screenX, short screenY)
        {
            try
            {
                return IsScreenPointInElement(PinBtn, screenX, screenY) ||
                       IsScreenPointInElement(PassThroughBtn, screenX, screenY) ||
                       IsScreenPointInElement(CloseBtn, screenX, screenY);
            }
            catch
            {
                return false;
            }
        }

        private bool IsScreenPointInElement(System.Windows.UIElement element, short screenX, short screenY)
        {
            if (element == null || !element.IsVisible || element.Opacity == 0)
                return false;

            try
            {
                // PointToScreen 返回的已经是屏幕物理像素 (Physical Pixels)
                var ptScreen = element.PointToScreen(new System.Windows.Point(0, 0));
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(element);

                double left = ptScreen.X; // 已经是物理像素，不再乘以 DpiScaleX
                double top = ptScreen.Y;  // 已经是物理像素，不再乘以 DpiScaleY
                double right = left + element.RenderSize.Width * dpi.DpiScaleX;   // RenderSize 是逻辑像素，需乘 DPI
                double bottom = top + element.RenderSize.Height * dpi.DpiScaleY; // RenderSize 是逻辑像素，需乘 DPI

                return screenX >= left && screenX <= right &&
                       screenY >= top && screenY <= bottom;
            }
            catch
            {
                return false;
            }
        }

        private void LoadSettings()
        {
            try
            {
                var folder = Path.Combine(AppContext.BaseDirectory, "Data");
                var settingsPath = Path.Combine(folder, "settings.json");
                if (File.Exists(settingsPath))
                {
                    var json = File.ReadAllText(settingsPath, Encoding.UTF8);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        _isPinned = settings.IsPinned;
                        // 鼠标模式：新字段优先；旧配置只有 IsPassThrough（bool），映射为全穿透
                        _mouseMode = settings.MouseMode != 0
                                   ? settings.MouseMode
                                   : (settings.IsPassThrough ? 1 : 0);
                        // 外观（旧 settings.json 缺字段时保持默认值，与原 #66FFFFFF 观感一致）
                        if (!string.IsNullOrWhiteSpace(settings.BackgroundColor))
                            _bgColor = settings.BackgroundColor;
                        if (settings.BackgroundOpacity >= 0 && settings.BackgroundOpacity <= 1)
                            _bgOpacity = settings.BackgroundOpacity;
                        _showBorder = settings.ShowBorder;   // 旧配置缺字段时用类默认值 true
                    }
                }
                else
                {
                    _isPinned  = false;
                    _mouseMode = 0;
                }
            }
            catch
            {
                _isPinned  = false;
                _mouseMode = 0;
            }

            // 应用外观（背景颜色 + 不透明度）
            ApplyBackground();

            // 应用置顶状态
            this.Topmost = _isPinned;
            if (_isPinned)
            {
                PinBtn.Content = "📍";
                PinBtn.ToolTip = "取消置顶";
            }
            else
            {
                PinBtn.Content = "📌";
                PinBtn.ToolTip = "置顶";
            }

            // 应用鼠标模式（穿透按钮图标 / 轮询定时器）
            switch (_mouseMode)
            {
                case 1: // 全穿透
                    PassThroughBtn.Content = "◉";
                    PassThroughBtn.ToolTip = "全穿透中 · 点击切换半穿透";
                    StartPassThroughTimer();
                    break;
                case 2: // 半穿透
                    PassThroughBtn.Content = "◐";
                    PassThroughBtn.ToolTip = "半穿透中（仅链接可点）· 点击关闭穿透";
                    StartPassThroughTimer();
                    break;
                default: // 普通
                    PassThroughBtn.Content = "⊙";
                    PassThroughBtn.ToolTip = "开启鼠标穿透";
                    StopPassThroughTimer();
                    break;
            }
        }

        private void SaveSettings()
        {
            try
            {
                var folder = Path.Combine(AppContext.BaseDirectory, "Data");
                if (!Directory.Exists(folder))
                    Directory.CreateDirectory(folder);
                var settingsPath = Path.Combine(folder, "settings.json");

                var settings = new AppSettings
                {
                    IsPinned      = _isPinned,
                    IsPassThrough = _mouseMode != 0,   // 兼容旧版本读取
                    MouseMode     = _mouseMode,
                    BackgroundColor = _bgColor,
                    BackgroundOpacity = _bgOpacity,
                    ShowBorder    = _showBorder
                };
                var json = JsonSerializer.Serialize(settings);
                File.WriteAllText(settingsPath, json, Encoding.UTF8);
            }
            catch
            {
                // 忽略保存错误
            }
        }

        // ══════════════════════════════════════════════
        // 外观：背景颜色 + 不透明度
        // ══════════════════════════════════════════════
        private void ApplyBackground(string? color = null, double? opacity = null, bool? showBorder = null)
        {
            if (color != null)      _bgColor   = color;
            if (opacity != null)    _bgOpacity = opacity.Value;
            if (showBorder != null) _showBorder = showBorder.Value;

            var c     = (WinColor)WinColorConv.ConvertFromString(_bgColor);

            // 边框开关（关闭即无边框模式）
            RootBorder.BorderThickness = _showBorder ? new Thickness(1) : new Thickness(0);

            // ── 先采样窗口正下方桌面（透过半透明窗口），经反混合还原纯桌面色 ──
            // 这套采样不只用于文字明暗自适应，还用于"完全透明"时给背景配色，
            // 因此必须先于背景赋值执行。正下方采样不受窗口边界黑白分界线干扰。
            var wall = SampleBelowWindow();
            var desk = wall != null
                ? UnpremultiplyColor(wall.Value, c, _bgOpacity) ?? wall.Value   // 反混合失败用混合色近似
                : (System.Drawing.Color?)null;
            if (desk != null)
            {
                // 记录纯桌面色（而非混合色）供轮询比较：
                // 纯桌面色与透明度无关，调整透明度不会误触发轮询刷新（避免黑白字闪烁）
                _lastWallColor = desk;
            }

            // ── 设置背景 ──
            // 背景 alpha 下界钳到 1（而非 0）：本窗口 AllowsTransparency=True（WPF 分层窗口），
            // 操作系统 Raw Input Thread 会对窗口"像素 100% 透明"的位置跳过该窗口、把鼠标事件
            // 交给下层窗口——这是 OS 级硬限制，任何 WPF 层 HitTestCore 重写都无法挽回（事件
            // 根本没进窗口）。所以透明度拉满时必须保留 alpha≥1，让窗口"看得见鼠标"、可交互。
            // 为消除 alpha=1 那约 0.4% 的视觉色差：当透明度≈0 时，背景色改用上面采样还原的
            // 纯桌面色（同色叠加在壁纸上几乎不可见），而不是用背景色 _bgColor（白色叠加在
            // 深色壁纸上会暴露 0.4% 的白偏）。逻辑透明度 _bgOpacity 保持原值，主题/轮询不受影响。
            var alpha = (byte)Math.Clamp(Math.Round(_bgOpacity * 255), 1, 255);
            if (alpha <= 1 && desk != null)
            {
                // 完全透明且采样成功：背景=壁纸色 + alpha=1，可交互且无可见色差
                RootBorder.Background = new WinBrush(WinColor.FromArgb(1, desk.Value.R, desk.Value.G, desk.Value.B));
            }
            else
            {
                RootBorder.Background = new WinBrush(WinColor.FromArgb(alpha, c.R, c.G, c.B));
            }

            // ── 全套配色（文字/链接/高亮/代码底色/勾选框）按背景亮度自适应 ──
            if (desk != null)
            {
                // 正下方采样成功：用纯桌面色与背景色按透明度混合出感知色判断明暗
                double o = Math.Clamp(_bgOpacity, 0, 1);
                var w = desk.Value;
                var eff = WinColor.FromArgb(0xFF,
                    (byte)Math.Round(c.R * o + w.R * (1 - o)),
                    (byte)Math.Round(c.G * o + w.G * (1 - o)),
                    (byte)Math.Round(c.B * o + w.B * (1 - o)));
                AdaptiveTheme.Update(eff);
            }
            else if ((wall = SampleWallpaperAround()) != null)
            {
                // 正下方失败，退回四周环带采样
                double o = Math.Clamp(_bgOpacity, 0, 1);
                var w = wall.Value;
                var eff = WinColor.FromArgb(0xFF,
                    (byte)Math.Round(c.R * o + w.R * (1 - o)),
                    (byte)Math.Round(c.G * o + w.G * (1 - o)),
                    (byte)Math.Round(c.B * o + w.B * (1 - o)));
                AdaptiveTheme.Update(eff);
            }
            else
            {
                AdaptiveTheme.Update(c, _bgOpacity);   // 采样全部失败退回灰度启发式
            }

            // 控件画刷每次是全新实例，重新写入资源字典触发 DynamicResource 重解析。
            // 不能原地改色：WPF 封存样式/模板时会自动冻结其中解析到的 Freezable，
            // 冻结后再改 Color 会抛 InvalidOperationException。
            Resources["CheckBorderBrush"] = AdaptiveTheme.CheckBorder;
            Resources["CheckMarkBrush"]   = AdaptiveTheme.CheckMark;
            Resources["CheckFillBrush"]   = AdaptiveTheme.CheckFill;
            Resources["TodoTextBrush"]    = AdaptiveTheme.Text;
            Resources["TodoTextDimBrush"] = AdaptiveTheme.TextDim;
            Resources["HeaderTextBrush"]  = AdaptiveTheme.HeaderText;
            Resources["IconBtnBrush"]     = AdaptiveTheme.IconBtn;

            // ── 原地编辑框背景：固定纯黑/纯白（不再跟随壁纸） ──
            // 之前编辑框背景复用窗口当前背景色（跟随壁纸采样），壁纸一变背景就跟着变，
            // 编辑时会一直闪动。改为只取两种极端：浅色主题→白底深字，深色主题→黑底浅字。
            // 用 AdaptiveTheme.Text 的亮度判断当前明暗分支（浅主题文字是深色、深主题文字是浅色）：
            // 文字偏亮(≥128)→深色主题→黑底；文字偏暗(<128)→浅色主题→白底。
            var fg = ((WinBrush)AdaptiveTheme.Text).Color;
            bool isDark = (fg.R + fg.G + fg.B) / 3 >= 128;  // 文字偏亮→深色主题
            var editBg = isDark ? WinColor.FromArgb(0xFF, 0x1E, 0x1E, 0x1E)   // 深主题：暖深灰（比纯黑柔和、有层次，白字对比仍≥15:1）
                                : WinColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);  // 浅主题：纯白底
            var editFg = isDark ? WinColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)   // 黑底白字
                                : WinColor.FromArgb(0xFF, 0x22, 0x22, 0x22);   // 白底深字
            Resources["EditBoxBgBrush"]    = new WinBrush(editBg);
            Resources["EditBoxFgBrush"]    = new WinBrush(editFg);
            Resources["EditBoxCaretBrush"] = new WinBrush(editFg);
            // 边框：黑底用半透明白、白底用半透明白，都能在编辑区边缘可见
            Resources["EditBoxBorderBrush"] = isDark
                ? new WinBrush(WinColor.FromArgb(0x55, 0xFF, 0xFF, 0xFF))
                : new WinBrush(WinColor.FromArgb(0x55, 0x00, 0x00, 0x00));
        }

        /// <summary>防抖重启配色刷新（窗口拖动/缩放期间不连续采样）</summary>
        private void RestartThemeRefresh()
        {
            if (_themeTimer == null) return;
            if (_themeTimer.IsEnabled) _themeTimer.Stop();
            _themeTimer.Start();
        }

        /// <summary>
        /// 低频轮询：定期采样窗口正下方的桌面颜色，若与上次感知颜色差异超过阈值，
        /// 说明壁纸/桌面背景发生了变化（即使窗口未拖动），触发一次主题刷新。
        /// 颜色几乎无变化时直接返回，不触发重绘以节省资源。
        /// 注意：比较的是"反混合还原后的纯桌面色"，而非窗口底下的混合色——
        /// 纯桌面色与透明度无关，用户调整透明度时不会误触发刷新（避免黑白字闪烁）。
        /// </summary>
        private void WallpaperPoll_Tick(object? sender, EventArgs e)
        {
            var sampled = SampleBelowWindow();
            if (sampled == null) return;

            // 还原纯桌面色：透明度不影响桌面真实颜色，比较它才不会因调透明度误触发
            var c = (WinColor)WinColorConv.ConvertFromString(_bgColor);
            var desk = UnpremultiplyColor(sampled.Value, c, _bgOpacity) ?? sampled.Value;

            if (_lastWallColor is System.Drawing.Color last)
            {
                int dR = Math.Abs(desk.R - last.R);
                int dG = Math.Abs(desk.G - last.G);
                int dB = Math.Abs(desk.B - last.B);
                // 三通道平均变化量小于阈值 → 桌面没变，跳过
                if ((dR + dG + dB) / 3 < 10) return;
            }
            _lastWallColor = desk;
            ApplyBackground();   // 颜色有实质变化 → 重采样并应用主题
        }

        /// <summary>
        /// 采样窗口四周一圈（约 24px 环带，避开窗口本体）的屏幕像素平均色，
        /// 作为"窗口底下桌面颜色"的估计。失败（截图被拒/区域无效）返回 null。
        /// </summary>
        private System.Drawing.Color? SampleWallpaperAround()
        {
            try
            {
                // DIP → 物理像素换算
                double scale = 1.0;
                var src = PresentationSource.FromVisual(this);
                if (src?.CompositionTarget != null)
                    scale = src.CompositionTarget.TransformToDevice.M11;

                double wDip = ActualWidth > 0 ? ActualWidth : Width;
                double hDip = ActualHeight > 0 ? ActualHeight : Height;
                int x = (int)Math.Round(Left * scale);
                int y = (int)Math.Round(Top * scale);
                int w = (int)Math.Round(wDip * scale);
                int h = (int)Math.Round(hDip * scale);
                int m = (int)Math.Max(8, Math.Round(24 * scale));

                // 外扩矩形，并裁剪到虚拟屏幕内（避免越界采到黑边）
                int ox = x - m, oy = y - m;
                int ow = w + 2 * m, oh = h + 2 * m;
                var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
                int cx = Math.Max(ox, vs.Left), cy = Math.Max(oy, vs.Top);
                int cr = Math.Min(ox + ow, vs.Right), cb = Math.Min(oy + oh, vs.Bottom);
                if (cr - cx < 16 || cb - cy < 16) return null;

                int bw = cr - cx, bh = cb - cy;
                using var bmp = new System.Drawing.Bitmap(bw, bh);
                using (var gfx = System.Drawing.Graphics.FromImage(bmp))
                    gfx.CopyFromScreen(cx, cy, 0, 0, new System.Drawing.Size(bw, bh));

                // 窗口本体在位图中的坐标（跳过，只统计环带像素）
                int ix0 = Math.Max(0, x - cx), iy0 = Math.Max(0, y - cy);
                int ix1 = Math.Min(bw, x + w - cx), iy1 = Math.Min(bh, y + h - cy);

                var rect = new System.Drawing.Rectangle(0, 0, bw, bh);
                var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                                        System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                var buf = new byte[data.Stride * bh];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
                bmp.UnlockBits(data);

                long r = 0, g = 0, b = 0; long n = 0;
                int step = Math.Max(1, Math.Max(bw, bh) / 128);
                for (int yy = 0; yy < bh; yy += step)
                {
                    bool innerRow = yy >= iy0 && yy < iy1;
                    for (int xx = 0; xx < bw; xx += step)
                    {
                        if (innerRow && xx >= ix0 && xx < ix1) continue;
                        int i = yy * data.Stride + xx * 4;
                        b += buf[i];
                        g += buf[i + 1];
                        r += buf[i + 2];
                        n++;
                    }
                }
                if (n < 64) return null;   // 环带有效像素太少（窗口贴满屏），不可靠

                return System.Drawing.Color.FromArgb((int)(r / n), (int)(g / n), (int)(b / n));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 采样窗口正下方（窗口本体中心一块矩形）的屏幕像素平均色。
        /// 由于窗口本身带不透明度（背景 o + 桌面透出 (1-o)），这里采到的是
        /// "窗口背景色与下方桌面颜色的混合色"，需配合反混合公式还原纯桌面色。
        /// 相比四周环带采样，正下方采样不受窗口边界处黑白分界线干扰。
        /// 失败（截图被拒/区域无效）返回 null。
        /// </summary>
        private System.Drawing.Color? SampleBelowWindow()
        {
            try
            {
                // DIP → 物理像素换算
                double scale = 1.0;
                var src = PresentationSource.FromVisual(this);
                if (src?.CompositionTarget != null)
                    scale = src.CompositionTarget.TransformToDevice.M11;

                double wDip = ActualWidth > 0 ? ActualWidth : Width;
                double hDip = ActualHeight > 0 ? ActualHeight : Height;
                int x = (int)Math.Round(Left * scale);
                int y = (int)Math.Round(Top * scale);
                int w = (int)Math.Round(wDip * scale);
                int h = (int)Math.Round(hDip * scale);

                // 只取窗口中心一块（45%×45%），避开边缘圆角、边框阴影与可能的文字行
                int cx0 = x + (int)(w * 0.275), cy0 = y + (int)(h * 0.275);
                int cw = (int)(w * 0.45), ch = (int)(h * 0.45);
                if (cw < 8 || ch < 8) return null;   // 窗口过小，采样区域无意义

                var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
                int sx = Math.Max(cx0, vs.Left), sy = Math.Max(cy0, vs.Top);
                int ex = Math.Min(cx0 + cw, vs.Right), ey = Math.Min(cy0 + ch, vs.Bottom);
                if (ex - sx < 8 || ey - sy < 8) return null;   // 裁剪后过小

                int bw = ex - sx, bh = ey - sy;
                using var bmp = new System.Drawing.Bitmap(bw, bh);
                using (var gfx = System.Drawing.Graphics.FromImage(bmp))
                    gfx.CopyFromScreen(sx, sy, 0, 0, new System.Drawing.Size(bw, bh));

                var rect = new System.Drawing.Rectangle(0, 0, bw, bh);
                var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                                        System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                var buf = new byte[data.Stride * bh];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
                bmp.UnlockBits(data);

                long r = 0, g = 0, b = 0; long n = 0;
                int step = Math.Max(1, Math.Max(bw, bh) / 64);
                for (int yy = 0; yy < bh; yy += step)
                {
                    for (int xx = 0; xx < bw; xx += step)
                    {
                        int i = yy * data.Stride + xx * 4;
                        b += buf[i];
                        g += buf[i + 1];
                        r += buf[i + 2];
                        n++;
                    }
                }
                if (n < 16) return null;

                return System.Drawing.Color.FromArgb((int)(r / n), (int)(g / n), (int)(b / n));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 反混合：已知混合色 M、窗口背景色 C 与不透明度 o（窗口在上的遮盖比例），
        /// 反推窗口正下方的纯桌面色 D，满足 M = C*o + D*(1-o)。
        /// 数值误差会导致结果越界（超出 0~255）或负数，需钳制。
        /// </summary>
        private System.Drawing.Color? UnpremultiplyColor(System.Drawing.Color mixed, WinColor bg, double opacity)
        {
            double o = Math.Clamp(opacity, 0, 1);
            double d = 1 - o;
            if (d < 0.05) return null;   // 窗口近乎完全不透明时无法可靠反推，放弃

            byte Clamp(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

            double r = (mixed.R - bg.R * o) / d;
            double g = (mixed.G - bg.G * o) / d;
            double b = (mixed.B - bg.B * o) / d;

            return System.Drawing.Color.FromArgb(Clamp(r), Clamp(g), Clamp(b));
        }

        /// <summary>托盘「外观设置」入口：打开设置窗口，实时预览，保存后落盘</summary>
        public void OpenAppearanceSettings()
        {
            var win = new SettingsWindow(
                _bgColor, _bgOpacity, _showBorder,
                preview: (c, o, b) => ApplyBackground(c, o, b),
                save:     (c, o, b) => { ApplyBackground(c, o, b); SaveSettings(); })
            {
                Owner = this
            };
            win.Show();
        }
    }
}
