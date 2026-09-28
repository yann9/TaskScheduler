using System.Windows.Controls;

// WPF 与 WinForms 同时引用时 UserControl 有歧义，显式消歧（CS0104）
using UserControl = System.Windows.Controls.UserControl;

namespace TaskScheduler.Views.Editors
{
    /// <summary>「执行条件」分区视图</summary>
    public partial class ConditionSectionView : UserControl
    {
        public ConditionSectionView() => InitializeComponent();
    }
}
