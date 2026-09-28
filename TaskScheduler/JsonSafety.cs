using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace TaskScheduler
{
    /// <summary>
    /// 序列化安全闸门。
    ///
    /// tasks.json 与命名管道 payload 都是「本地用户可写 + 服务端以 LocalSystem 反序列化」的组合，
    /// 而程序启用了 TypeNameHandling.Auto —— $type 字段会参与类型解析。
    /// 不加约束的话，任何能写这两个入口的本地用户，只要塞一条 $type 指向 .NET 自带的 gadget 类型
    /// （ObjectDataProvider / Process / TypeConverter 这一类），服务下次加载时就会以 SYSTEM 执行代码，
    /// 等于本机任意用户提权到 SYSTEM。
    ///
    /// 这里改用白名单：只放行本程序集 TaskScheduler* 命名空间下的类型，
    /// 外加 Newtonsoft 解析集合 / 数组 / 枚举外壳所必需的少量框架类型，其余一律拒绝。
    /// 老存档不受影响 —— 它落盘的类型全在本程序集里。
    /// </summary>
    internal static class JsonSafety
    {
        /// <summary>本地存档（%ProgramData%\TaskScheduler\tasks.json）</summary>
        public static readonly JsonSerializerSettings Persistent = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.Auto,
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.Indented,
            SerializationBinder = new SafeSerializationBinder()
        };

        /// <summary>命名管道上的 JSON（载荷更小，不缩进）</summary>
        public static readonly JsonSerializerSettings Wire = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.Auto,
            NullValueHandling = NullValueHandling.Ignore,
            SerializationBinder = new SafeSerializationBinder()
        };
    }

    /// <summary>只允许本程序集类型 + 少量框架容器类型的反序列化绑定器</summary>
    internal sealed class SafeSerializationBinder : DefaultSerializationBinder
    {
        /// <summary>
        /// 必须放行的框架类型：$type 一般只出现在多态节点上，但集合 / 数组 / 时间这类
        /// 出现在多态位置时同样会走绑定器，漏一个就会让正常存档加载失败。
        /// 这里刻意逐项列出，而不是放行整个 System 命名空间 ——
        /// 反序列化 gadget 恰恰都藏在 System.* 下面。
        /// </summary>
        private static readonly HashSet<string> FrameworkTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.Collections.Generic.List`1",
            "System.Collections.Generic.IList`1",
            "System.Collections.Generic.Dictionary`2",
            "System.Collections.Generic.IDictionary`2",
            "System.Collections.Generic.HashSet`1",
            "System.Collections.ObjectModel.ObservableCollection`1",
            "System.Collections.ObjectModel.Collection`1",
            "System.DateTime",
            "System.TimeSpan",
            "System.String"
        };

        public override Type BindToType(string assemblyName, string typeName)
        {
            var type = base.BindToType(assemblyName, typeName);
            if (type == null) return null;
            if (IsAllowed(type)) return type;

            throw new JsonSerializationException("拒绝反序列化未授权的类型：" + type.FullName);
        }

        private static bool IsAllowed(Type type)
        {
            // 枚举没有任何可执行成员，不存在 gadget 风险
            if (type.IsEnum) return true;

            // 数组：只要元素类型合法就放行（例如 DayOfWeek[]）
            if (type.IsArray) return IsAllowed(type.GetElementType());

            var ns = type.Namespace ?? string.Empty;
            if (ns == "TaskScheduler" || ns.StartsWith("TaskScheduler.", StringComparison.Ordinal))
                return true;

            return FrameworkTypes.Contains(type.FullName ?? string.Empty);
        }
    }
}
