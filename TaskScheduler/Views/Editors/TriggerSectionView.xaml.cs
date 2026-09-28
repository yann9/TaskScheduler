using System.Windows.Controls;

// 本项目同时引用了 WPF 与 WinForms（UseWindowsForms），UserControl / Binding / TextBox
// 这类名字两个命名空间里都有，必须显式消歧（CS0104）。
using UserControl = System.Windows.Controls.UserControl;

namespace TaskScheduler.Views.Editors
{
    /// <summary>
    /// 「触发器」分区视图。
    /// 每个分区一个 UserControl，是为了让"改触发器的界面"这件事物理上碰不到其他分区的 XAML。
    /// </summary>
    public partial class TriggerSectionView : UserControl
    {
        public TriggerSectionView() => InitializeComponent();
    }
}
