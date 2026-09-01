using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using QingDesk.Models;

namespace QingDesk.Services
{
    public class MarkdownStorage
    {
        private readonly string _filePath;

        public MarkdownStorage()
        {
            // 数据保存在 exe 所在目录的 Data\ 子目录（便携式，随 exe 一起迁移）
            var folder = Path.Combine(AppContext.BaseDirectory, "Data");
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "todos.md");
            // Ensure file exists
            if (!File.Exists(_filePath))
                File.WriteAllText(_filePath, string.Empty, Encoding.UTF8);
        }

        public List<TodoItem> LoadTodos()
        {
            var todos = new List<TodoItem>();
            var lines = File.ReadAllLines(_filePath, Encoding.UTF8);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                // 剥掉行首的 "- "（兼容无勾旧格式）
                var title = trimmed.StartsWith("- ") ? trimmed.Substring(2) : trimmed;

                // 再判断是否有 "[x] " 完成标记
                bool isDone = false;
                if (title.StartsWith("[x] "))
                {
                    isDone = true;
                    title = title.Substring(4);
                }
                // 顺手兼容 "[ ] "（未勾选的显式写法）
                else if (title.StartsWith("[ ] "))
                {
                    title = title.Substring(4);
                }

                // 解码被转义的换行（多行任务还原），"&#10;" 还原为真实换行
                title = title.Replace("&#10;", "\n");

                todos.Add(new TodoItem { Title = title, IsDone = isDone });
            }
            return todos;
        }


        public void SaveTodos(IEnumerable<TodoItem> items)
        {
            var sb = new StringBuilder();
            foreach (var item in items)
            {
                var prefix = item.IsDone ? "- [x] " : "- ";
                // 多行任务：把换行统一为 \n 并转义成 &#10;，确保整条任务写在一行内，
                // 避免重开程序时被按行拆分。加载端再做反向解码。
                var title = item.Title
                    .Replace("\r\n", "\n")
                    .Replace("\r", "\n")
                    .Replace("\n", "&#10;");
                sb.AppendLine(prefix + title);
            }
            File.WriteAllText(_filePath, sb.ToString(), Encoding.UTF8);
        }

    }
}
