using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TaskScheduler.Engine;
using TaskScheduler.Views;

namespace TaskScheduler.ViewModels
{
    /// <summary>
    /// 「检查更新」窗口。
    ///
    /// 手动检查：构造时传 null，窗口自己异步拉清单；
    /// 启动自动检查：后台已经检查完，把结果传进来直接展示（避免重复请求）。
    ///
    /// 所有网络/文件操作都包了异常，失败时把原因直接写在窗口里 —— 检查更新是低频操作，
    /// 「点了没反应」比「弹出一句网络超时」更让人困惑。
    /// </summary>
    public partial class UpdateViewModel : ObservableObject
    {
        private readonly Action _close;
        private readonly UpdateCheckResult _preloaded;

        /// <summary>
        /// 使用哪份设置去检查。设置窗口里点「立即检查更新」时，用户可能刚改完地址还没点「确定」，
        /// 这时传的是窗口里的副本；其余入口传 null，用全局单例。这样检查不会污染单例、
        /// 点「取消」也不会留下半截设置。
        /// </summary>
        private readonly AppSettings _settings;

        [ObservableProperty] private string _statusText = "正在检查更新…";
        [ObservableProperty] private bool _isBusy = true;
        [ObservableProperty] private bool _hasUpdate;
        [ObservableProperty] private bool _hasError;
        [ObservableProperty] private bool _isUpToDate;
        [ObservableProperty] private bool _isDownloading;
        [ObservableProperty] private int _downloadProgress;
        [ObservableProperty] private string _progressText = "";
        [ObservableProperty] private string _currentVersion = AppVersion.Current;
        [ObservableProperty] private string _newVersion = "";
        [ObservableProperty] private string _releaseTitle = "";
        [ObservableProperty] private string _releaseNotes = "";
        [ObservableProperty] private string _publishedText = "";
        [ObservableProperty] private string _sourceText = "";
        [ObservableProperty] private bool _canInstall;

        private UpdateInfo _info;

        public IRelayCommand CheckCommand { get; }
        public IRelayCommand InstallCommand { get; }
        public IRelayCommand OpenPageCommand { get; }
        public IRelayCommand SkipCommand { get; }
        public ICommand CloseCommand { get; }

        public UpdateViewModel(UpdateCheckResult preloaded, Action close, AppSettings settings = null)
        {
            _preloaded = preloaded;
            _settings = settings;
            _close = close ?? (() => { });

            CheckCommand = new RelayCommand(async () => await CheckAsync(), () => !IsBusy);
            InstallCommand = new RelayCommand(async () => await InstallAsync(),
                () => HasUpdate && !IsBusy);
            OpenPageCommand = new RelayCommand(OpenPage, () => HasUpdate && !IsBusy);
            SkipCommand = new RelayCommand(SkipVersion, () => HasUpdate && !IsBusy);
            CloseCommand = new RelayCommand(() => _close(), () => !IsDownloading);

            if (_preloaded == null) _ = CheckAsync();
            else Apply(_preloaded);
        }

        private async Task CheckAsync()
        {
            IsBusy = true;
            IsUpToDate = false;
            HasUpdate = false;
            HasError = false;
            CanInstall = false;
            StatusText = "正在检查更新…";
            SourceText = "";
            NotifyCommands();

            try
            {
                var result = await UpdateService.CheckAsync(_settings);
                Apply(result);
            }
            catch (Exception ex)
            {
                // async void 命令的兜底：任何漏网异常都必须在窗口里可见，不能冒到 Dispatcher
                Log.Error("检查更新异常：" + ex.Message);
                Apply(UpdateCheckResult.Failed(ex.Message));
            }
        }

        private void Apply(UpdateCheckResult r)
        {
            IsBusy = false;
            SourceText = string.IsNullOrWhiteSpace(r?.Source) ? "" : "检查来源：" + r.Source;

            if (r == null || !r.Success)
            {
                HasError = true;
                StatusText = "检查更新失败：" + (r?.Error ?? "未知错误");
                ReleaseNotes = "国内网络下建议：\n"
                    + "1）确认本机代理可用并勾选「跟随系统代理」；\n"
                    + "2）在「设置 → 更新」里填一份放在国内可达位置的自定义清单地址；\n"
                    + "3）或在「加速节点」里换成自己可用的节点前缀。\n\n"
                    + "程序已自动尝试直连与多个加速节点，原有版本不受影响。";
            }
            else if (!r.HasUpdate)
            {
                IsUpToDate = true;
                StatusText = "当前已是最新版本";
                ReleaseNotes = "没有发现比 " + AppVersion.Current + " 更新的发布。";
            }
            else
            {
                _info = r.Info;
                HasUpdate = true;
                CanInstall = _info.CanInstall;
                NewVersion = AppVersion.Normalize(_info.Version);
                ReleaseTitle = string.IsNullOrWhiteSpace(_info.Title) ? ("版本 " + NewVersion) : _info.Title;
                ReleaseNotes = string.IsNullOrWhiteSpace(_info.Notes) ? "（本次发布没有填写更新说明）" : _info.Notes;
                PublishedText = _info.PublishedAt.HasValue
                    ? _info.PublishedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                    : "";
                StatusText = CanInstall
                    ? "发现新版本，可一键下载安装"
                    : "发现新版本，请到发布页手动下载";
            }

            NotifyCommands();
        }

        /// <summary>下载并启动安装包；没有可用安装包时退化为打开发布页</summary>
        private async Task InstallAsync()
        {
            if (_info == null) return;

            if (!_info.CanInstall)
            {
                OpenPage();
                return;
            }

            IsBusy = true;
            IsDownloading = true;
            DownloadProgress = 0;
            ProgressText = "0%";
            StatusText = "正在下载安装包（会自动切换可用线路）…";
            NotifyCommands();

            try
            {
                var progress = new Progress<int>(p =>
                {
                    DownloadProgress = p;
                    ProgressText = p + "%";
                });

                var path = await UpdateService.DownloadAsync(_info, progress, _settings);
                StatusText = "下载完成，正在启动安装程序…";
                UpdateService.LaunchInstaller(path);

                // 安装包会覆盖正在运行的 exe：主动退出，走 OnExit 的 Stop()+Save() 落盘最新排期。
                // 不直接杀进程，否则存档会停在旧排期上。
                // 这里必须写全名：项目同时引用了 WPF 与 WinForms（隐式 using 会把两个 Application 都带进来）。
                App.ForceClose = true;
                System.Windows.Application.Current?.Shutdown();
            }
            catch (Exception ex)
            {
                Log.Error("下载更新失败：" + ex.Message);
                IsBusy = false;
                IsDownloading = false;
                StatusText = "下载失败：" + ex.Message;
                ProgressText = "";
                ReleaseNotes = "可以点「打开下载页」手动下载安装。\n\n原有版本不受影响，任务配置与运行记录都还在。";
                NotifyCommands();
            }
        }

        private void OpenPage()
        {
            try { UpdateService.OpenDownloadPage(_info, _settings); }
            catch (Exception ex)
            {
                TsDialog.Show("打开发布页失败：" + ex.Message, "提示",
                    TsDialogButtons.Ok, TsDialogIcon.Warning);
            }
        }

        /// <summary>「跳过此版本」：只记住这一个版本号，将来发新版仍会提示</summary>
        private void SkipVersion()
        {
            if (_info == null) return;
            try
            {
                SettingsService.Current.SkippedUpdateVersion = AppVersion.Normalize(_info.Version);
                SettingsService.Save();
            }
            catch (Exception ex) { Log.Error("保存跳过版本失败：" + ex.Message); }
            _close();
        }

        private void NotifyCommands()
        {
            CheckCommand.NotifyCanExecuteChanged();
            InstallCommand.NotifyCanExecuteChanged();
            OpenPageCommand.NotifyCanExecuteChanged();
            SkipCommand.NotifyCanExecuteChanged();
            (CloseCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        }
    }
}
