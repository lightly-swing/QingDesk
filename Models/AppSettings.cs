namespace QingDesk.Models
{
    public class AppSettings
    {
        public bool IsPinned { get; set; } = false;
        public bool IsPassThrough { get; set; } = false;  // 旧字段，仅为兼容旧 settings.json 保留

        /// <summary>鼠标模式：0=普通，1=全穿透，2=半穿透（仅超链接可点击）</summary>
        public int MouseMode { get; set; } = 0;

        // 外观：背景颜色（#RRGGBB）与不透明度（0~1）
        public string BackgroundColor { get; set; } = "#FFFFFF";
        public double BackgroundOpacity { get; set; } = 0.40;

        /// <summary>是否显示窗口边框（关闭即无边框模式）</summary>
        public bool ShowBorder { get; set; } = true;
    }
}
