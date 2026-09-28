using System;
using TaskScheduler.Native;

namespace TaskScheduler.Conditions
{
    /// <summary>
    /// 前台窗口条件：根据用户当前正在看什么窗口来决定要不要执行。
    ///
    /// 典型用法：
    /// - 「标题包含『腾讯会议』」→ 开会时不要弹通知、不要抢焦点。
    /// - 「处于全屏」→ 演示 / 打游戏 / 看片时不打扰。
    /// - 「标题包含『VS Code』」→ 只有写代码时才自动跑构建。
    ///
    /// 重要限制：这条条件在服务模式（Session 0）下**永远不满足**。
    /// 服务进程跑在自己的会话里，看不到交互桌面的任何窗口，
    /// GetForegroundWindow 返回空。这种条件必须配合"登录时自动启动"使用。
    /// </summary>
    public class ForegroundWindowCondition : ConditionBase
    {
        public override ConditionType Type => ConditionType.ForegroundWindow;

        /// <summary>
        /// 标题需要包含的关键词（忽略大小写）。多个用逗号 / 分号分隔，命中任意一个即算匹配。
        /// 留空表示不限制标题。
        /// </summary>
        private string _titleContains = "";
        public string TitleContains
        {
            get => _titleContains;
            set => SetField(ref _titleContains, value);
        }

        /// <summary>全屏要求</summary>
        private FullScreenMode _fullScreen = FullScreenMode.Ignore;
        public FullScreenMode FullScreen
        {
            get => _fullScreen;
            set => SetField(ref _fullScreen, value);
        }

        protected override bool EvaluateCore()
        {
            var title = NativeMethods.GetForegroundWindowTitle();
            if (title == null) return false;      // 服务模式 / 无前台窗口 → 不满足

            var keys = SplitKeywords(TitleContains);
            if (keys.Length > 0)
            {
                var hit = false;
                foreach (var k in keys)
                {
                    if (title.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) { hit = true; break; }
                }
                if (!hit) return false;
            }

            switch (FullScreen)
            {
                case FullScreenMode.RequireFullScreen:
                    return NativeMethods.IsForegroundWindowFullScreen();
                case FullScreenMode.RequireWindowed:
                    return !NativeMethods.IsForegroundWindowFullScreen();
                default:
                    return true;                   // Ignore：不限制；两者都没设就是"恒通过"
            }
        }

        internal static string[] SplitKeywords(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return new string[0];
            var parts = s.Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new System.Collections.Generic.List<string>();
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list.ToArray();
        }

        protected override string DescribeCore
        {
            get
            {
                var keys = SplitKeywords(TitleContains);
                string s = keys.Length == 0 ? "" : "前台窗口标题含 " + string.Join(" / ", keys);

                switch (FullScreen)
                {
                    case FullScreenMode.RequireFullScreen:
                        s = s.Length == 0 ? "前台窗口处于全屏" : s + " 且全屏";
                        break;
                    case FullScreenMode.RequireWindowed:
                        s = s.Length == 0 ? "前台窗口非全屏" : s + " 且非全屏";
                        break;
                }

                return s.Length == 0 ? "前台窗口（未设置限制）" : s;
            }
        }
    }
}
