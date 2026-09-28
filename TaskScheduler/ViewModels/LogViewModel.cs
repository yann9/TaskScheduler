using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TaskScheduler.Engine;
using TaskScheduler.Views;

namespace TaskScheduler.ViewModels
{
    /// <summary>
    /// 独立日志窗口。
    ///
    /// 日志内容直接从日志文件尾部读 —— 内存里只留最近 1000 行，翻更早的内容必须落文件。
    /// 服务模式下经由管道向服务端要，因此两种托管方式看到的是同一份日志。
    /// </summary>
    public partial class LogViewModel : ObservableObject
    {
        private readonly ITaskService _host;
        private readonly DispatcherTimer _timer;

        /// <summary>可选的行数档位</summary>
        public List<int> LineCounts { get; } = new List<int> { 200, 500, 1000, 2000, 5000 };

        [ObservableProperty] private string _text = "";
        [ObservableProperty] private string _keyword = "";
        [ObservableProperty] private bool _autoRefresh = true;
        [ObservableProperty] private int _lineCount = 500;
        [ObservableProperty] private bool _wrapLines;
        [ObservableProperty] private string _statusText = "";

        public ICommand RefreshCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand ClearFileCommand { get; }

        public string LogPath => Paths.LogFilePath;

        public LogViewModel(ITaskService host)
        {
            _host = host;
            RefreshCommand = new RelayCommand(Load);
            OpenFolderCommand = new RelayCommand(OpenFolder);
            ClearFileCommand = new RelayCommand(ClearFile);

            // 关掉自动刷新时后台轮询也停掉，免得用户以为停了其实还在每 2 秒拉一次
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += (s, e) => { if (AutoRefresh) Load(); };
            _timer.Start();

            Load();
        }

        partial void OnAutoRefreshChanged(bool value)
        {
            if (value) Load();
        }

        partial void OnLineCountChanged(int value) => Load();
        partial void OnKeywordChanged(string value) => ApplyFilter(_raw);

        private string _raw = "";

        private void Load()
        {
            _raw = _host.ReadLogFile(LineCount) ?? "";
            ApplyFilter(_raw);
        }

        /// <summary>
        /// 关键字过滤在客户端做：服务端只负责把尾部 N 行给过来，
        /// 这样改关键字不用重新走一次管道往返。
        /// </summary>
        private void ApplyFilter(string raw)
        {
            var k = Keyword?.Trim();
            string result;
            if (string.IsNullOrEmpty(k))
            {
                result = raw;
            }
            else
            {
                var lines = raw.Split('\n').Where(l => l.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                result = string.Join(Environment.NewLine, lines);
            }

            // 内容没变就不重新赋值：整段 Text 重绑会把滚动位置打回顶部
            if (!string.Equals(result, Text, StringComparison.Ordinal)) Text = result;
            StatusText = string.IsNullOrEmpty(k)
                ? $"共 {_raw.Split('\n').Length} 行"
                : $"匹配 {result.Split('\n').Where(l => l.Length > 0).Count()} 行";
        }

        private void OpenFolder()
        {
            try
            {
                var path = Paths.LogFilePath;
                if (File.Exists(path))
                    Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else
                    Process.Start("explorer.exe", "\"" + Paths.DataDir + "\"");
            }
            catch (Exception ex)
            {
                Log.Error("打开日志目录失败：" + ex.Message);
            }
        }

        private void ClearFile()
        {
            var r = TsDialog.Show(
                "确认清空日志文件？\n\n" + Paths.LogFilePath + "\n\n（已打开的这个窗口会跟着清空，之后的日志照常写入。）",
                "确认", TsDialogButtons.YesNo, TsDialogIcon.Question);
            if (r != System.Windows.MessageBoxResult.Yes) return;

            try
            {
                _host.ClearLogFile();
                Load();
            }
            catch (Exception ex)
            {
                TsDialog.Show("清空日志失败：\n\n" + ex.Message, "提示",
                    TsDialogButtons.Ok, TsDialogIcon.Warning);
            }
        }
    }
}
