using System;
using System.Windows;
using TaskScheduler.ViewModels;

namespace TaskScheduler.Views
{
    public partial class TaskEditorWindow : Window
    {
        public TaskEditorWindow()
        {
            InitializeComponent();
        }

        protected override void OnClosed(EventArgs e)
        {
            // 解绑静态事件（节假日同步状态），否则每个编辑器实例都会被 HolidayProvider 一直持有
            (DataContext as TaskEditorViewModel)?.Detach();
            base.OnClosed(e);
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not TaskEditorViewModel vm) return;

            // 让每个分区把"只存在于界面上"的状态写回模型（例如星期勾选）。
            // 各分区自己负责自己的收尾，窗口不需要知道有哪些字段。
            vm.CommitAll();

            if (string.IsNullOrWhiteSpace(vm.Task.Name))
            {
                TsDialog.Show("请填写任务名称", "提示",
                    TsDialogButtons.Ok, TsDialogIcon.Warning);
                return;
            }

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            // 取消就是丢弃这份草稿：MainViewModel 传给编辑器的本来就是副本，
            // 引擎持有的那个实例一个字段都没被碰过。
            DialogResult = false;
        }
    }
}
