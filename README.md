# 自动化任务调度器 (TaskScheduler)

[![CI](https://github.com/yann9/TaskScheduler/actions/workflows/release.yml/badge.svg)](https://github.com/yann9/TaskScheduler/actions/workflows/release.yml)

Windows 下基于多种**触发器 + 条件 + 动作**的定时 / 自动化任务桌面程序，支持以 **Windows 后台服务**方式保活运行（无需登录用户）、单实例 + 命名管道 IPC 远程控制，以及**中国法定节假日**判断。

## 特性

- **多种触发器**
  - 定时触发（`TimeTrigger`）
  - 文件监控触发（`FileWatcherTrigger`）
  - 系统事件触发（`SystemEventTrigger`，如设备接入 / 卷变动等 WMI 事件）
- **丰富条件**（命中全部条件才执行动作）
  - 磁盘空间、文件状态、前台窗口、HTTP 健康检查、本地 IP、网络连通性 / 类型、电源状态、进程运行、服务状态、系统负载、时间窗口
- **多种动作**
  - 运行程序（`RunProgramAction`，支持 cmd / bat / Git Bash 脚本，自动按编码解码输出）
  - HTTP 请求、文件操作、模拟键鼠输入、桌面通知
- **后台服务保活**：可安装为 Windows 服务（`AutomationService`），系统启动即运行，不依赖当前登录用户
- **单实例 + 命名管道 IPC**：`PipeServer` / `PipeClient` 控制已运行实例（如优雅退出、状态查询）
- **中国法定节假日**：`ChineseCalendar` + `HolidayProvider`，支撑「工作日 / 节假日」类调度逻辑
- **运行历史、日志、托盘常驻、开机自启**（`AutoStartRegistration`）

## 技术栈

| 项 | 说明 |
| --- | --- |
| 框架 | .NET Framework 4.8（`net48`，SDK-style 项目） |
| UI | WPF（`UseWPF`），托盘使用 WinForms `NotifyIcon` |
| MVVM | `CommunityToolkit.Mvvm` 8.2.2 |
| 序列化 | `Newtonsoft.Json` 13.0.3 |
| 进程间通信 | 命名管道（Named Pipes） |
| 系统能力 | Windows 服务（`ServiceProcess`）、WMI、注册表自启、P/Invoke |

## 环境要求

- Windows 7 及以上（建议 Windows 10 / 11）
- 已安装 **.NET Framework 4.8** 运行时
- 构建需要 **.NET SDK**（建议 8.x；SDK-style `net48` 项目由 .NET SDK 驱动，**仅用于编译**，不进入最终运行产物）+ Visual Studio 2022（或任意支持 SDK-style 的 IDE，VS2022 自带 .NET SDK）

## 构建

用 Visual Studio 打开 `TaskScheduler.sln`，选择 `Release | Any CPU` 构建即可。

或使用命令行：

```powershell
# 需先安装 .NET SDK（建议 8.x；VS2022 已自带）
dotnet build TaskScheduler.sln -c Release
```

## 运行与安装

**作为桌面程序运行**：直接启动 `TaskScheduler.exe`，程序最小化到托盘常驻。

**安装为后台服务**（推荐用于「无人值守 / 开机自启」场景）：

```powershell
# 以管理员身份执行
TaskScheduler.exe --install-service
TaskScheduler.exe --start-service
```

**卸载服务**：

```powershell
TaskScheduler.exe --stop-service
TaskScheduler.exe --uninstall-service
```

## 命令行参数

| 参数 | 说明 |
| --- | --- |
| _(无参数)_ | 启动 WPF 主界面 |
| `--service` | 以 Windows 服务方式运行（由 SCM 调用） |
| `--install-service` | 安装 Windows 服务 |
| `--uninstall-service` | 卸载 Windows 服务 |
| `--start-service` | 启动已安装的服务 |
| `--stop-service` | 停止服务 |
| `--exit` | 请求已运行的实例优雅退出（落盘最新排期后退出） |

> 带任意命令行参数时程序进入「命令行模式」：不弹模态对话框，问题一律写日志 + 以退出码（0 成功 / 1 失败）传递，便于安装器与发布脚本调用。

## 发布

- `publish.ps1`：构建并将产物发布到 `dist/`（含安装包 `Setup.exe`）。
- `TaskScheduler.nsi`（NSIS）：生成可分发的安装包。

## 目录结构

```
TaskScheduler/
├── TaskScheduler.sln          # 解决方案
├── publish.ps1                # 发布脚本
├── TaskScheduler.nsi          # NSIS 安装包脚本
├── dist/                      # 发布产物（构建生成，不纳入版本控制）
└── TaskScheduler/             # 主项目
    ├── Actions/               # 动作：运行程序 / HTTP / 文件 / 输入 / 通知
    ├── Conditions/            # 条件：磁盘 / 网络 / 电源 / 进程 / 时间窗口 …
    ├── Triggers/              # 触发器：定时 / 文件监控 / 系统事件
    ├── Engine/                # 调度引擎、节假日、日志、路径、设置、自启
    ├── Service/               # Windows 服务与安装控制
    ├── Tray/                  # 托盘图标管理
    ├── Host/                  # 任务服务（本地 / 远程 IPC）
    ├── Ipc/                   # 命名管道通信
    ├── Persistence/           # 任务持久化（JSON）
    ├── Views/                 # 主窗口 / 任务编辑 / 日志 / 运行历史 / 设置
    ├── Models/                # 数据模型
    ├── Styles/                # 主题（经典工具软件风格）
    ├── Converters/            # XAML 值转换器
    ├── Native/                # P/Invoke
    ├── Program.cs             # 入口与命令行分发
    └── App.xaml(.cs)          # 应用定义
```

## 许可证

本项目采用 [MIT 许可证](LICENSE)。详见仓库根目录的 `LICENSE` 文件。

## 自动构建与发布（GitHub Actions）

仓库内置 `.github/workflows/release.yml`：

- **触发方式**
  - 推送形如 `v*` 的 tag（如 `v1.0.0`）自动发布；
  - 或在 GitHub Actions 页面手动 `workflow_dispatch` 并填写版本号（会创建对应 `v<版本>` tag）。
- **构建流程**（Windows runner）
  1. 安装 .NET SDK（CI 用 8.x）与 NSIS（`makensis`）；
  2. 按版本号替换 `TaskScheduler.nsi` 中的 `TS_VERSION`；
  3. 调用 `publish.ps1` 完成契约检查、`dotnet publish` 与安装包编译（`dist\Setup.exe`）。
- **发布产物**：自动创建 GitHub Release，上传
  - `TaskScheduler-<版本>-setup.exe` —— NSIS 安装包；
  - `TaskScheduler-<版本>-portable.zip` —— 免安装绿色版（仅主程序 + 依赖）。
