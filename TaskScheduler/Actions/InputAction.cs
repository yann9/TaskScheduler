using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TaskScheduler.Native;

namespace TaskScheduler.Actions
{
    /// <summary>
    /// 模拟键鼠输入。
    /// 键盘走 SendKeys（支持文本与快捷键语法，如 ^c 表示 Ctrl+C）；
    /// 鼠标走 SendInput 封装（NativeMethods.MouseClick）。
    /// 必须在 UI 线程(STA)执行，由引擎通过 Dispatcher 调度。
    /// </summary>
    public class InputAction : ObservableModel, IAction
    {
        public ActionType Type => ActionType.InputSimulation;

        private InputActionKind _kind = InputActionKind.KeyboardText;
        public InputActionKind Kind
        {
            get => _kind;
            set => Set(ref _kind, value);
        }

        private string _text = "";
        public string Text
        {
            get => _text;
            set => Set(ref _text, value);
        }

        private int _mouseX;
        public int MouseX
        {
            get => _mouseX;
            set => Set(ref _mouseX, value);
        }

        private int _mouseY;
        public int MouseY
        {
            get => _mouseY;
            set => Set(ref _mouseY, value);
        }

        public async Task ExecuteAsync(ActionContext context, CancellationToken ct)
        {
            Action run = Kind switch
            {
                InputActionKind.KeyboardText => () => SendKeys.SendWait(Text),
                InputActionKind.KeyboardShortcut => () => SendKeys.SendWait(Text),
                InputActionKind.MouseClick => () => NativeMethods.MouseClick(MouseX, MouseY),
                _ => () => { }
            };

            if (context.Dispatcher != null)
                await context.Dispatcher.InvokeAsync(run);
            else
                run();
        }

        public string Describe()
        {
            return Kind switch
            {
                InputActionKind.KeyboardText => $"输入文本：{Text}",
                InputActionKind.KeyboardShortcut => $"快捷键：{Text}",
                InputActionKind.MouseClick => $"鼠标点击：({MouseX},{MouseY})",
                _ => "模拟输入"
            };
        }
    }
}
