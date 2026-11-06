using System;
using System.Reflection;

namespace TaskScheduler.Engine
{
    /// <summary>
    /// 程序版本号：读取与比较的唯一入口。
    ///
    /// 版本来自程序集属性（发布时由 publish.ps1 经 -p:Version 注入），
    /// 「关于」窗口与更新检查读的是同一个值 —— 以前版本字符串只在 AboutViewModel 里解析，
    /// 更新检查再抄一份就会出现"关于显示 1.2.0、检查更新拿 1.2.0.0 去比"的错位。
    ///
    /// 比较按数字段逐段进行，而不是字符串：字符串比较下 "1.10.0" &lt; "1.9.0"，
    /// 会让 1.10 永远提示"已是最新"。
    /// </summary>
    public static class AppVersion
    {
        private static string _current;

        /// <summary>当前程序版本（语义版本，形如 1.2.0）</summary>
        public static string Current => _current ?? (_current = Resolve());

        /// <summary>
        /// 优先取 InformationalVersion（发布时由 publish.ps1 经 -p:Version 注入 tag 版本），
        /// 否则退回 AssemblyVersion。
        /// 版本号里若带 "+..." 后缀（.NET SDK 会把提交哈希拼在后面，形如 "1.3.0+a1b2c3"）
        /// 一律截掉 —— 界面与版本比较都只用语义版本部分。
        /// </summary>
        private static string Resolve()
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info)) return Normalize(info);
            var v = asm.GetName().Version;
            return v == null ? "1.0.0" : Normalize(v.ToString());
        }

        /// <summary>去掉 v 前缀、+构建元数据、-预发布后缀，只留数字版本段</summary>
        public static string Normalize(string raw)
        {
            var s = (raw ?? "").Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);

            var plus = s.IndexOf('+');
            if (plus >= 0) s = s.Substring(0, plus);
            var dash = s.IndexOf('-');
            if (dash >= 0) s = s.Substring(0, dash);

            return s.Trim();
        }

        /// <summary>左 &gt; 右返回正数；相等返回 0；左 &lt; 右返回负数。无法解析的段按 0 处理。</summary>
        public static int Compare(string left, string right)
        {
            var a = Parse(left);
            var b = Parse(right);
            for (int i = 0; i < 4; i++)
            {
                if (a[i] != b[i]) return a[i] > b[i] ? 1 : -1;
            }
            return 0;
        }

        /// <summary>候选版本是否比基准版本新</summary>
        public static bool IsNewer(string candidate, string baseline) => Compare(candidate, baseline) > 0;

        private static int[] Parse(string raw)
        {
            var result = new int[4];
            var parts = Normalize(raw).Split('.');
            for (int i = 0; i < parts.Length && i < result.Length; i++)
                int.TryParse(parts[i], out result[i]);
            return result;
        }
    }
}
