using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Newtonsoft.Json;
using TaskScheduler.Actions;
using TaskScheduler.Engine;
using TaskScheduler.Triggers;

namespace TaskScheduler.Persistence
{
    /// <summary>
    /// 任务持久化：用 Newtonsoft 的多态序列化把触发器 / 动作 / 条件存到
    /// %ProgramData%\TaskScheduler\tasks.json（服务与界面共用，避免权限错配）。
    ///
    /// 两条硬约束：
    /// 1) 序列化设置一律走 <see cref="JsonSafety"/> 的白名单绑定器 —— 这个文件是本地可写的，
    ///    而反序列化方是 LocalSystem，不设防等于把提权入口摆在 ProgramData 下。
    /// 2) 写盘必须原子（临时文件 + Replace + 备份），读盘失败绝不能退化成"没有任务" ——
    ///    否则一次失败读取之后，任意一次 Save() 就把空列表固化了。
    /// </summary>
    public class TaskRepository
    {
        private const string TmpSuffix = ".tmp";
        private const string BackupSuffix = ".bak";

        public string FilePath { get; set; }

        /// <summary>最近一次加载的告警（文件损坏 / 从备份恢复），供上层提示用户</summary>
        public string LastLoadWarning { get; private set; }

        public TaskRepository()
        {
            var dir = Engine.Paths.DataDir;
            EnsureDirectory(dir);
            FilePath = Engine.Paths.TasksFilePath;
        }

        private static void EnsureDirectory(string dir)
        {
            try
            {
                if (!Directory.Exists(dir))
                {
                    var d = Directory.CreateDirectory(dir);
                    // 放开 Users 的修改权限，使服务(LocalSystem)创建的文件也能被普通用户编辑。
                    // 代价要说清楚：本机任意用户都能改写任务文件（完整性），
                    // 但反序列化白名单 + 目录不可被替换，不足以被用来提权。
                    try
                    {
                        var sec = d.GetAccessControl();
                        sec.AddAccessRule(new FileSystemAccessRule(
                            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                            FileSystemRights.Modify | FileSystemRights.Synchronize,
                            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                            PropagationFlags.None,
                            AccessControlType.Allow));
                        d.SetAccessControl(sec);
                    }
                    catch { }
                }
            }
            catch { }
        }

        public List<AutomationTask> Load()
        {
            LastLoadWarning = null;

            var primary = TryLoad(FilePath);
            if (primary.Ok) return primary.Tasks;

            // 主文件读不出来 —— 以前这里直接返回空列表，后果是引擎照常启动，
            // 之后任意一次 Save()（新增任务 / 排期变化 / 退出）都把空列表写回去，任务全丢。
            // 现在：先试备份；备份也不行就把损坏文件改名隔离，再以空列表启动，原文件一个字节都不覆盖。
            var bak = FilePath + BackupSuffix;
            if (File.Exists(bak))
            {
                var fromBak = TryLoad(bak);
                if (fromBak.Ok)
                {
                    LastLoadWarning =
                        "任务文件损坏，已从备份恢复。\n\n" +
                        "损坏原因：" + primary.Error + "\n" +
                        "已恢复任务数：" + fromBak.Tasks.Count;

                    Log.Error("任务文件损坏，已从备份恢复：" + primary.Error);
                    try { Save(fromBak.Tasks); } catch { }   // 立刻把恢复结果写回主文件
                    return fromBak.Tasks;
                }
            }

            string quarantine;
            try
            {
                quarantine = FilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Move(FilePath, quarantine);
            }
            catch (Exception ex)
            {
                quarantine = "（隔离失败：" + ex.Message + "）";
            }

            LastLoadWarning =
                "任务文件无法解析，程序以「无任务」状态启动。\n\n" +
                "损坏原因：" + primary.Error + "\n" +
                "原文件已保留在：\n" + quarantine + "\n\n" +
                "原文件没有被覆盖，修好内容后可改回 tasks.json 再重启。";

            Log.Error("任务文件损坏：" + primary.Error + " → " + quarantine);
            return new List<AutomationTask>();
        }

        private sealed class LoadResult
        {
            public List<AutomationTask> Tasks;
            public string Error;
            public bool Ok => Tasks != null;
        }

        private static LoadResult TryLoad(string path)
        {
            if (!File.Exists(path)) return new LoadResult { Tasks = new List<AutomationTask>() };

            Exception lastIo = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var json = File.ReadAllText(path);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        // 空文件等同于被截断：不能当作"没有任务"，否则下一轮 Save 就固化了
                        return new LoadResult { Error = "文件为空（疑似写入被中断）" };
                    }

                    var list = JsonConvert.DeserializeObject<List<AutomationTask>>(json, JsonSafety.Persistent);
                    return new LoadResult { Tasks = list ?? new List<AutomationTask>() };
                }
                catch (JsonException ex)
                {
                    // 解析失败 / 被白名单拒绝 —— 重试没有意义
                    return new LoadResult { Error = ex.Message };
                }
                catch (Exception ex)
                {
                    // IO 层面的瞬时问题（文件被短暂占用、杀软扫描）值得重试
                    lastIo = ex;
                    System.Threading.Thread.Sleep(150);
                }
            }
            return new LoadResult { Error = lastIo?.Message ?? "未知错误" };
        }

        public void Save(IEnumerable<AutomationTask> tasks)
        {
            try
            {
                var json = JsonConvert.SerializeObject(tasks, JsonSafety.Persistent);

                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // 原子替换：WriteAllText 直接覆盖时，写到一半断电/被杀就会留下截断的半个 JSON。
                var tmp = FilePath + TmpSuffix;
                File.WriteAllText(tmp, json);

                if (!File.Exists(FilePath))
                {
                    File.Move(tmp, FilePath);
                    return;
                }

                try
                {
                    File.Replace(tmp, FilePath, FilePath + BackupSuffix, true);
                }
                catch (Exception ex)
                {
                    // 部分文件系统（网络盘 / 某些加密卷）不支持 Replace，退化为直接覆盖
                    Log.Error("原子替换任务文件失败，回退为直接写入：" + ex.Message);
                    File.Copy(tmp, FilePath, true);
                    TryDelete(tmp);
                }
            }
            catch (Exception ex)
            {
                Log.Error("保存任务失败：" + ex.Message);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
