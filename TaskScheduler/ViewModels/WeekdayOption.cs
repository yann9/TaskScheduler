using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using TaskScheduler.Conditions;
using TaskScheduler.Triggers;

namespace TaskScheduler.ViewModels
{
    /// <summary>星期多选项的视图模型（“每周”触发器与“允许时段”条件各自持有一份）</summary>
    public partial class WeekdayOption : ObservableObject
    {
        public DayOfWeek Day { get; }
        public string Label { get; }
        [ObservableProperty] private bool _isChecked;

        /// <summary>
        /// 勾选变化时的回调，用来立刻把结果写回模型。
        /// 不能只在"确定"时统一写回：切换选中的条件 / 触发器会重建这份勾选列表，
        /// 没写回的话用户刚勾的星期会直接消失。
        /// </summary>
        public Action Changed { get; set; }

        public WeekdayOption(DayOfWeek day, string label) { Day = day; Label = label; }

        partial void OnIsCheckedChanged(bool value) => Changed?.Invoke();
    }

    /// <summary>
    /// 星期勾选列表的构造与回写（触发器与条件两个分区共用同一套星期定义）。
    /// 名字刻意不叫 "WeekdayOptions"：分区里有同名属性，会遮蔽类型名导致 CS1061。
    /// </summary>
    internal static class WeekdayOptionBuilder
    {
        public static readonly (DayOfWeek day, string label)[] Defs =
        {
            (DayOfWeek.Monday, "周一"), (DayOfWeek.Tuesday, "周二"), (DayOfWeek.Wednesday, "周三"),
            (DayOfWeek.Thursday, "周四"), (DayOfWeek.Friday, "周五"), (DayOfWeek.Saturday, "周六"),
            (DayOfWeek.Sunday, "周日"),
        };

        /// <summary>
        /// 按 model 里已选的星期重建勾选列表。
        /// Changed 必须最后赋值：放前面的话 IsChecked 设初值时会立刻回调，
        /// 把还没填完的半截列表写回模型。
        /// </summary>
        public static void Rebuild(IList<WeekdayOption> target, DayOfWeek[] selected, Action onChanged)
        {
            target.Clear();
            foreach (var def in Defs)
                target.Add(new WeekdayOption(def.day, def.label)
                {
                    IsChecked = selected != null && Array.Exists(selected, d => d == def.day),
                    Changed = onChanged
                });
        }

        public static DayOfWeek[] Collect(IEnumerable<WeekdayOption> options)
            => options.Where(o => o.IsChecked).Select(o => o.Day).ToArray();
    }
}
