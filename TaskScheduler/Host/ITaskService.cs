using System.Collections.Generic;
using System.Threading.Tasks;
using TaskScheduler.Engine;

namespace TaskScheduler
{
    /// <summary>
    /// 任务托管抽象：UI 无论连后台服务还是本进程托管，都通过它操作。
    ///
    /// 读写约定的差别是刻意的：写操作（保存 / 删除 / 立即运行 / 启停 / 清空记录）失败必须抛异常，
    /// 界面才能给出提示；读操作（列表 / 日志 / 状态 / 运行记录）失败只记日志并返回空值，
    /// 因为它们每秒都在轮询，弹窗会淹没用户。
    /// </summary>
    public interface ITaskService
    {
        IReadOnlyList<AutomationTask> ListTasks();
        void SaveTask(AutomationTask task);
        void DeleteTask(AutomationTask task);
        Task RunNowAsync(AutomationTask task);

        string GetLogs();
        bool IsEngineRunning { get; }
        void StartEngine();
        void StopEngine();

        /// <summary>运行记录（倒序）。全部参数可空，limit &lt;= 0 表示不限条数。</summary>
        IReadOnlyList<RunRecord> GetRunHistory(string taskId = null, RunOutcome? outcome = null,
                                               string keyword = null, int limit = 0);

        /// <summary>清空运行记录；taskId 为空时清空全部</summary>
        void ClearRunHistory(string taskId = null);

        /// <summary>从日志文件尾部读取最多 maxLines 行（内存缓冲只有 1000 行，翻旧日志必须落文件）</summary>
        string ReadLogFile(int maxLines);

        /// <summary>删除日志文件（失败抛异常，界面需要给出提示）</summary>
        void ClearLogFile();

        /// <summary>让持有引擎的一侧重新加载设置（服务模式下界面改了设置要通知服务端）</summary>
        void ReloadSettings();
    }
}
