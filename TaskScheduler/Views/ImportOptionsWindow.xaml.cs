using System.IO;
using System.Windows;
using TaskScheduler.Persistence;

namespace TaskScheduler.Views
{
    /// <summary>
    /// 导入前的确认窗口：把文件信息摊开给用户看，再让他选「合并 / 覆盖」。
    /// 覆盖是破坏性操作，所以默认选中合并，并且明确写出"会自动备份"。
    ///
    /// 这个小窗口是一次性选择，没有持续的展示状态，故直接用 code-behind 读写控件，
    /// 不额外套一层 ViewModel（与 AboutWindow 的处理方式一致）。
    /// </summary>
    public partial class ImportOptionsWindow : Window
    {
        /// <summary>true = 覆盖全部；false = 合并追加</summary>
        public bool ReplaceAll { get; private set; }

        public ImportOptionsWindow(TaskImportResult result, string path)
        {
            InitializeComponent();

            FileText.Text = Path.GetFileName(path) + "　（" + path + "）";
            CountText.Text = result.Tasks.Count + " 个";

            var exported = result.ExportedAt.HasValue
                ? result.ExportedAt.Value.ToString("yyyy-MM-dd HH:mm")
                : "未知";
            var sourceVersion = string.IsNullOrWhiteSpace(result.SourceAppVersion)
                ? "未知" : result.SourceAppVersion;
            ExportInfoText.Text = $"导出时间 {exported}　|　导出程序版本 {sourceVersion}　|　格式版本 {result.FormatVersion}";

            WarningText.Text = string.IsNullOrWhiteSpace(result.Warning)
                ? "文件校验通过，没有发现问题。"
                : result.Warning;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            ReplaceAll = RadioReplace.IsChecked == true;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
