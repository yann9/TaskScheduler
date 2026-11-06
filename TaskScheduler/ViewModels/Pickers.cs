using System.IO;

namespace TaskScheduler.ViewModels
{
    /// <summary>
    /// 文件 / 目录选择对话框。
    /// 统一放在这里，是因为编辑器的四个分区都要用 —— 抄进四个 ViewModel 就等于
    /// 四处各有一份"对话框怎么写"，迟早会有一处漏掉 CheckFileExists 之类的细节。
    /// </summary>
    internal static class Pickers
    {
        /// <summary>选一个文件；mustExist=false 时允许输入不存在的路径（用于"目标路径"这类场景）</summary>
        public static string File(string initial, bool mustExist)
        {
            // 写全限定名：项目同时引用了 WinForms，OpenFileDialog 两个命名空间里都有
            var d = new Microsoft.Win32.OpenFileDialog
            {
                FileName = initial ?? "",
                CheckFileExists = mustExist,
                ValidateNames = mustExist
            };
            return d.ShowDialog() == true ? d.FileName : null;
        }

        /// <summary>选一个目录</summary>
        public static string Folder(string initial)
        {
            // 用"打开文件"对话框的目录选择技巧，外观比旧式 FolderBrowserDialog 更现代
            var d = new Microsoft.Win32.OpenFileDialog
            {
                CheckFileExists = false,
                ValidateNames = false,
                FileName = string.IsNullOrEmpty(initial) ? "选择此文件夹" : initial,
                Title = "选择文件夹"
            };
            return d.ShowDialog() == true ? Path.GetDirectoryName(d.FileName) : null;
        }

        private const string JsonFilter = "任务文件 (*.json)|*.json|所有文件 (*.*)|*.*";

        /// <summary>选择要导入的 .json 文件</summary>
        public static string OpenJson()
        {
            var d = new Microsoft.Win32.OpenFileDialog
            {
                Filter = JsonFilter,
                CheckFileExists = true,
                ValidateNames = true,
                Title = "导入任务"
            };
            return d.ShowDialog() == true ? d.FileName : null;
        }

        /// <summary>选择导出目标路径（自动补 .json 扩展名、覆盖前确认）</summary>
        public static string SaveJson(string defaultName)
        {
            var d = new Microsoft.Win32.SaveFileDialog
            {
                Filter = JsonFilter,
                FileName = defaultName ?? "tasks.json",
                DefaultExt = ".json",
                AddExtension = true,
                OverwritePrompt = true,
                Title = "导出任务"
            };
            return d.ShowDialog() == true ? d.FileName : null;
        }
    }
}
