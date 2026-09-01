# QingDesk - Windows 桌面待办事项工具

一个极致轻量的 Windows 桌面待办事项工具。

> 基于作者 [li5bo5](https://github.com/li5bo5) 开源项目 [PinToDesk](https://github.com/li5bo5/PinToDesk) 深度二次开发，遵循 AGPL v3 许可证。

## 📥 下载地址

- **GitHub Releases**：[点击下载](https://github.com/你的用户名/QingDesk/releases)

## ✨ 功能特点

- **零干扰界面**：无边框半透明设计，悬浮于桌面，不遮挡工作
- **快速操作**：双击空白处添加，悬停条目显示编辑/删除按钮，拖拽重新排序
- **原地编辑**：双击条目在原位置直接编辑，无需弹窗，所见即所得
- **无字数限制**：支持任意长度内容，自动按窗口宽度换行显示
- **自由缩放**：拖拽右下角可自由调整窗口宽高，无纵横比锁定
- **边界保护**：拖动窗口或缩放时不会超出屏幕工作区范围，支持多显示器
- **细腻滚动条**：7px 极细滚动条，仅在鼠标悬停或滚轮操作时自动淡入显示
- **空列表提示**：列表为空时显示「暂无待办」占位文字
- **鼠标穿透三态**：普通 → 全穿透 → 半穿透（仅超链接可点击）循环切换
- **自适应主题**：自动采样窗口下方桌面颜色，实时切换文字深浅配色
- **Markdown 渲染**：支持 6 级标题、粗体、斜体、删除线、行内代码、荧光高亮、超链接
- **外观设置**：背景颜色、透明度（0%~100%）、无边框模式均可调，实时预览
- **数据持久化**：自动保存至本地 Markdown 文件，便携式存储，可用文本编辑器直接查看
- **开机自启**：可在系统托盘菜单中一键设置，无需管理员权限
- **系统托盘**：最小化到托盘后台运行，支持显示/隐藏、置顶切换、开机启动、退出

## 🖱️ 操作说明

| 操作 | 方法 |
|------|------|
| **添加待办** | 双击列表空白区域 → 输入内容 → 回车确认 |
| **取消添加** | 按 Esc 键 |
| **编辑待办** | 双击某条文字 → 原位置修改 → 回车确认（Shift+Enter 换行） |
| **删除待办** | 鼠标悬停在条目上 → 点击右侧 ✕ 按钮 |
| **拖拽排序** | 鼠标按住条目不放 → 拖拽至目标位置 → 松开 |
| **移动窗口** | 鼠标拖拽标题栏 |
| **调整大小** | 鼠标拖拽右下角手柄（宽高独立调整） |
| **置顶 / 取消** | 标题栏 📌 按钮，或托盘右键菜单 |
| **鼠标穿透** | 托盘右键菜单三态循环切换：普通 → 全穿透 → 半穿透 |
| **显示 / 隐藏** | 单击托盘显示并激活，双击托盘隐藏，或通过托盘右键菜单 |

## 🖥️ 系统要求

- **操作系统**：Windows 10 / 11（64 位）

| 版本 | 运行依赖 |
|------|---------|
| **SelfContained**（自包含版） | 无，下载即用 |
| **FrameworkDependent**（精简版） | 需预装 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |

## 💾 数据存储

待办事项与配置采用**便携式存储**，全部保存在可执行文件所在目录的 `Data\` 子目录下：

```
QingDesk.exe
└─ Data\
   ├─ todos.md        # 待办数据（Markdown 格式）
   └─ settings.json   # 外观与状态配置
```

- 数据随 exe 一起迁移，拷贝整个文件夹即可带着数据搬到其他电脑或 U 盘
- `todos.md` 可直接用任意文本编辑器查看和手动编辑

格式示例：

```markdown
- 完成周报
- [x] 买牛奶
- 回复邮件
```

## 🚀 开机自启动

在系统托盘图标上右键 → 勾选「**开机启动**」即可。

> 注意：开机启动路径基于当前 `.exe` 文件的实际位置。若移动软件文件，需重新勾选以更新路径。

## 🔧 从源码构建

```bash
# 开发调试
dotnet build

# 发布（自包含单文件，无依赖，推荐分发）
dotnet publish QingDesk.csproj -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=None -p:DebugSymbols=false -o publish/self-contained

# 发布（框架依赖，体积极小，需目标机装 .NET 8 运行时）
dotnet publish QingDesk.csproj -c Release -o publish/framework-dependent
```

- 技术栈：C# / WPF / .NET 8（net8.0-windows）
- 零第三方 NuGet 依赖

## 📁 项目结构

```
QingDesk/
├── App.xaml / App.xaml.cs             # 应用入口
├── MainWindow.xaml / MainWindow.xaml.cs  # 主窗口（列表交互、穿透、主题、拖拽）
├── SettingsWindow.xaml / .cs          # 外观设置
├── Models/
│   ├── TodoItem.cs                    # 待办数据模型
│   └── AppSettings.cs                 # 设置持久化模型
├── Services/
│   └── MarkdownStorage.cs             # 待办数据存取
├── Helpers/
│   ├── TrayHelper.cs                  # 系统托盘 + 开机自启
│   ├── AdaptiveTheme.cs               # 自适应主题配色
│   ├── MarkdownTextBlock.cs           # Markdown 绑定附加属性
│   └── MarkdownInlineParser.cs        # 零依赖 Markdown 解析器
├── ico/                               # 图标（多尺寸）
├── .github/workflows/release.yml      # GitHub Actions 自动发版
├── LICENSE                            # AGPL v3
└── QingDesk.csproj
```

## 📄 开源协议

本项目基于 [PinToDesk](https://github.com/li5bo5/PinToDesk) 二次开发，遵循 **AGPL v3** 许可证（详见 [LICENSE](LICENSE)）。
