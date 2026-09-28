using System;
using System.Runtime.InteropServices;

namespace TaskScheduler.Conditions
{
    /// <summary>
    /// 电源条件：要求当前为"接入电源(AC)"或"使用电池"。
    /// 通过 kernel32.GetSystemPowerStatus 读取 ACLineStatus。
    ///
    /// 读不到有效值（ACLineStatus = 255 未知，部分系统的服务模式下会出现）时按"满足"处理 ——
    /// 宁可执行，也不要因为读不到状态就静默不执行。界面上有对应说明。
    /// </summary>
    public class PowerCondition : ConditionBase
    {
        public override ConditionType Type => ConditionType.Power;

        private PowerRequiredState _required = PowerRequiredState.PluggedIn;
        public PowerRequiredState Required
        {
            get => _required;
            set => SetField(ref _required, value);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;   // 0=离线, 1=在线, 255=未知
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte Reserved1;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        protected override bool EvaluateCore()
        {
            if (!GetSystemPowerStatus(out var s)) return true;  // 调用失败：放行
            if (s.ACLineStatus == 255) return true;             // 状态未知：放行

            bool online = s.ACLineStatus == 1;
            return Required == PowerRequiredState.PluggedIn ? online : !online;
        }

        protected override string DescribeCore
            => Required == PowerRequiredState.PluggedIn ? "已接入电源(AC)" : "使用电池供电";
    }
}
