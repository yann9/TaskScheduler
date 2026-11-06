using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TaskScheduler.Engine;
using TaskScheduler.Triggers;

namespace TaskScheduler.Persistence
{
    /// <summary>
    /// 任务导入 / 导出的文件格式。
    ///
    /// 为什么不在文件里直接放一个裸数组：包一层元信息后，将来改任务模型时能用
    /// FormatVersion 做迁移，也能在导入界面上告诉用户"这份文件是哪个版本、什么时候导出的"。
    /// 裸数组仍然兼容（早期手工备份的 tasks.json 片段），导入时当作 FormatVersion=0 处理。
    ///
    /// ★ 反序列化一律走 <see cref="JsonSafety.Persistent"/>：导出文件来自外部，
    /// 裸 DeserializeObject 会绕过白名单绑定器，等于把 TypeNameHandling.Auto 的提权入口
    /// 从 tasks.json 扩大到任意来源的文件（见 JsonSafety 的威胁模型说明）。
    /// </summary>
    public class TaskExportDocument
    {
        public string Format { get; set; } = "TaskScheduler.Tasks";
        public int FormatVersion { get; set; } = 1;
        public DateTime ExportedAt { get; set; }
        public string AppVersion { get; set; }
        public List<AutomationTask> Tasks { get; set; } = new List<AutomationTask>();
    }

    /// <summary>导入文件的解析结果（还没有写进任务库）</summary>
    public class TaskImportResult
    {
        public List<AutomationTask> Tasks { get; set; } = new List<AutomationTask>();

        /// <summary>因配置不完整被跳过的条目数</summary>
        public int Skipped { get; set; }

        /// <summary>非致命提示（旧格式、字段缺失等）</summary>
        public string Warning { get; set; }

        public int FormatVersion { get; set; }
        public DateTime? ExportedAt { get; set; }
        public string SourceAppVersion { get; set; }
    }

    /// <summary>任务导入导出 + 覆盖前备份</summary>
    public static class TaskTransfer
    {
        public const string DocumentFormat = "TaskScheduler.Tasks";
        public const int CurrentFormatVersion = 1;

        /// <summary>
        /// 导出到指定文件。返回实际导出的任务数。
        /// 导出的是"配置副本"：Id / 名称 / 启用状态 / 触发器 / 条件 / 动作都保留，
        /// 但运行历史与本机排期（NextRunTime、HasScheduled、随机窗口锚点）一律清掉 ——
        /// 这些是这台机器的状态，带到别的机器上会让任务看起来"刚跑过 / 已经排到期"。
        /// </summary>
        public static int Export(IEnumerable<AutomationTask> tasks, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("导出路径为空", nameof(path));

            var doc = new TaskExportDocument
            {
                ExportedAt = DateTime.Now,
                AppVersion = AppVersion.Current,
                Tasks = (tasks ?? Enumerable.Empty<AutomationTask>())
                    .Where(t => t != null).Select(CleanForExport).ToList()
            };

            var json = JsonConvert.SerializeObject(doc, JsonSafety.Persistent);
            AtomicWrite(path, json);
            return doc.Tasks.Count;
        }

        /// <summary>读取并校验导入文件。任何格式问题都抛出带中文说明的异常，由界面提示。</summary>
        public static TaskImportResult Read(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("导入路径为空", nameof(path));
            if (!File.Exists(path)) throw new FileNotFoundException("找不到要导入的文件", path);

            string json;
            try { json = File.ReadAllText(path); }
            catch (Exception ex) { throw new IOException("读取文件失败：" + ex.Message, ex); }
            if (string.IsNullOrWhiteSpace(json)) throw new InvalidDataException("文件是空的");

            JToken root;
            try { root = JToken.Parse(json); }
            catch (Exception ex) { throw new InvalidDataException("不是有效的 JSON：" + ex.Message, ex); }

            var result = new TaskImportResult();
            List<AutomationTask> tasks;

            if (root is JArray)
            {
                // 裸数组：兼容手工备份的 tasks.json 片段
                try
                {
                    tasks = JsonConvert.DeserializeObject<List<AutomationTask>>(json, JsonSafety.Persistent)
                            ?? new List<AutomationTask>();
                }
                catch (Exception ex) { throw new InvalidDataException("任务列表解析失败：" + ex.Message, ex); }

                result.FormatVersion = 0;
                result.Warning = "这是裸数组格式（无导出元信息），已按兼容方式导入。";
            }
            else if (root is JObject)
            {
                TaskExportDocument doc;
                try
                {
                    doc = JsonConvert.DeserializeObject<TaskExportDocument>(json, JsonSafety.Persistent)
                          ?? new TaskExportDocument();
                }
                catch (Exception ex) { throw new InvalidDataException("文件格式解析失败：" + ex.Message, ex); }

                if (!string.Equals(doc.Format, DocumentFormat, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "这不是本程序的任务导出文件（Format = " + (doc.Format ?? "空") + "）。\n" +
                        "请选择由「文件 → 导出任务」生成的 .json 文件。");

                if (doc.FormatVersion > CurrentFormatVersion)
                    throw new InvalidDataException(
                        $"该文件由更新版本的程序生成（格式版本 {doc.FormatVersion}，本程序只支持到 {CurrentFormatVersion}）。\n" +
                        "请先升级程序再导入。");

                tasks = doc.Tasks ?? new List<AutomationTask>();
                result.FormatVersion = doc.FormatVersion;
                result.ExportedAt = doc.ExportedAt == default(DateTime) ? (DateTime?)null : doc.ExportedAt;
                result.SourceAppVersion = doc.AppVersion;
            }
            else
            {
                throw new InvalidDataException("文件内容既不是任务列表，也不是任务导出文档。");
            }

            // 每个任务都要过一遍"干净化"：新身份、默认停用、清运行态。
            // 这样导入后绝不会因为和现有任务同排期而意外双跑。
            foreach (var t in tasks)
            {
                if (t == null || t.Trigger == null || t.Action == null)
                {
                    result.Skipped++;
                    continue;
                }
                result.Tasks.Add(CleanForImport(t));
            }

            if (result.Skipped > 0)
            {
                var note = $"已跳过 {result.Skipped} 个缺少触发器或动作的条目。";
                result.Warning = string.IsNullOrEmpty(result.Warning) ? note : result.Warning + "\n" + note;
            }

            return result;
        }

        /// <summary>
        /// 覆盖导入前把当前任务库另存一份。返回备份路径；任务库本来就不存在时返回 null。
        /// 备份放在 %ProgramData%\TaskScheduler 下，和 tasks.json 同目录，用户能在"打开数据目录"里找到。
        /// </summary>
        public static string BackupTasksFile()
        {
            var src = Paths.TasksFilePath;
            if (!File.Exists(src)) return null;

            var backup = Path.Combine(Paths.DataDir,
                "tasks.backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
            File.Copy(src, backup, true);
            return backup;
        }

        // ================= 内部 =================

        private static AutomationTask CleanForExport(AutomationTask source)
        {
            var t = source.Clone();
            ClearRuntimeState(t);
            return t;
        }

        private static AutomationTask CleanForImport(AutomationTask source)
        {
            var t = source.Clone();
            t.Id = Guid.NewGuid().ToString("N");     // 新的身份：合并时不会撞掉现有任务
            t.Enabled = false;                       // 默认停用，确认无误后再手动启用
            if (string.IsNullOrWhiteSpace(t.Name)) t.Name = "导入的任务";
            ClearRuntimeState(t);
            t.RefreshView();
            return t;
        }

        /// <summary>清掉只属于"这一台机器、这一次运行"的状态</summary>
        private static void ClearRuntimeState(AutomationTask t)
        {
            t.LastRunTime = null;
            t.LastRunResult = "";
            t.NextRunTime = null;

            if (t.Trigger is TimeTrigger tt)
            {
                tt.NextRunTime = null;
                tt.HasScheduled = false;
                // 「时间段内随机」的锚点是本机排期状态，带过去会让新机器沿用旧日历节奏
                tt.RandomWindowAnchorDate = null;
            }
            if (t.Conditions == null)
                t.Conditions = new System.Collections.ObjectModel.ObservableCollection<TaskScheduler.Conditions.ICondition>();
        }

        /// <summary>
        /// 原子写盘：先写 .tmp 再替换，避免写到一半留下截断的半份文件。
        /// Export 的目标文件是用户选的路径，可能在网络盘 / 不支持 Replace 的卷上，故留了 Copy 回退。
        /// </summary>
        private static void AtomicWrite(string path, string json)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);

            if (!File.Exists(path))
            {
                File.Move(tmp, path);
                return;
            }

            try { File.Replace(tmp, path, null, true); }
            catch
            {
                File.Copy(tmp, path, true);
                try { File.Delete(tmp); } catch { }
            }
        }
    }
}
