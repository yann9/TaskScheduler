using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TaskScheduler.Actions
{
    /// <summary>文件 / 文件夹 复制或移动</summary>
    public class FileAction : ObservableModel, IAction
    {
        public ActionType Type => ActionType.FileOperation;

        private FileOperationKind _operation = FileOperationKind.Copy;
        public FileOperationKind Operation
        {
            get => _operation;
            set => Set(ref _operation, value);
        }

        private string _source = "";
        public string Source
        {
            get => _source;
            set => Set(ref _source, value);
        }

        private string _destination = "";
        public string Destination
        {
            get => _destination;
            set => Set(ref _destination, value);
        }

        private bool _overwrite = true;
        public bool Overwrite
        {
            get => _overwrite;
            set => Set(ref _overwrite, value);
        }

        public async Task ExecuteAsync(ActionContext context, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(Source) || string.IsNullOrWhiteSpace(Destination))
                throw new InvalidOperationException("源或目标未设置");

            await Task.Run(() =>
            {
                if (Directory.Exists(Source))
                {
                    if (Operation == FileOperationKind.Copy)
                        CopyDir(Source, Destination, Overwrite);
                    else
                        Directory.Move(Source, Destination);
                }
                else if (File.Exists(Source))
                {
                    var destDir = Path.GetDirectoryName(Destination);
                    if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                    if (Operation == FileOperationKind.Copy)
                        File.Copy(Source, Destination, Overwrite);
                    else
            {
                if (Overwrite && File.Exists(Destination)) File.Delete(Destination);
                File.Move(Source, Destination);
            }
                }
                else
                {
                    throw new FileNotFoundException("源不存在", Source);
                }
            }, ct);
        }

        private static void CopyDir(string src, string dst, bool overwrite)
        {
            Directory.CreateDirectory(dst);
            foreach (var dir in Directory.GetDirectories(src))
                CopyDir(dir, Path.Combine(dst, Path.GetFileName(dir)), overwrite);
            foreach (var file in Directory.GetFiles(src))
                File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite);
        }

        public string Describe()
        {
            var op = Operation == FileOperationKind.Copy ? "复制" : "移动";
            return $"{op}：{Source} → {Destination}";
        }
    }
}
