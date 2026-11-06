# 自动化任务调度器 (TaskScheduler)

[![CI](https://github.com/yann9/TaskScheduler/actions/workflows/release.yml/badge.svg)](https://github.com/yann9/TaskScheduler/actions/workflows/release.yml)

Windows 下基于多种**触发器 + 条件 + 动作**的定时 / 自动化任务桌面程序，支持以 **Windows 后台服务**方式保活运行（无需登录用户）、单实例 + 命名管道 IPC 远程控制，以及**中国法定节假日**判断。

## 特性

- **多种触发器**
  - 定时触发（`TimeTrigger`）：一次性 / 每天 / 每周 / 每月 / 间隔循环 / **时间段内随机**
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
- **检测更新**：Gitee 国内镜像清单优先、GitHub 直连与加速节点兜底、跟随系统代理、一键下载安装
- **任务导入导出**：全部 / 选中任务导出为 `.json`，导入可选合并追加或覆盖（覆盖前自动备份）

## 时间段内随机 / 检测更新 / 任务导入导出

### 时间段内随机

在任务编辑器的「触发器 → 重复方式」里选 **时间段内随机**，填写起止时刻与「每隔 N 天」：
程序每天（或每隔 N 天）在该窗口内均匀随机挑一个时刻执行。

- 支持跨午夜窗口（如 `22:00` 至 `02:00`）；结束时刻小于等于起始时刻即表示延续到次日。
- **本次抽中的时刻就是「下次运行」显示的时间**，并随任务一起存档；重启 / 重开程序不会重新抽取。
- 可与「跳过周末 / 跳过法定节假日」叠加；该方式本身已是随机，高级页的「随机抖动」不叠加。
- 用于「每天不定点上报」这类需要错峰的场景，和原有的「计划点 + 0–N 分钟抖动」互不干扰。

实现位于 `Triggers/TimeTrigger.cs`（`ComputeRandomWindowNext`），随机窗口的日历锚点也随任务持久化，
保证「每隔 N 天」在重启后不会重新起算。

### 检测更新

入口：**帮助 → 检查更新…**、关于窗口、设置页的「立即检查更新…」；
「设置 → 更新」可关闭启动时自动检查（每天最多一次，失败静默）、填写自定义清单地址。

发现新版本后可一键下载 NSIS 安装包并启动安装（程序先优雅退出以释放文件占用），
也可选择「打开下载页」自行下载，或「跳过此版本」。

#### 国内访问不了 GitHub 怎么办

默认链路**不依赖 GitHub**：

1. 先读 Gitee 制品仓库里本项目专属的固定清单
   `https://gitee.com/yann9/dist/raw/master/TaskScheduler/update.json`，命中就直接用，
   其余线路立刻取消（国内通常 0.1 秒返回）。
2. 镜像 404 / 不可达时，GitHub 直连与多个加速节点延后 0.6 秒接手、并发竞速，
   谁先返回合法清单用谁。
3. 下载安装包按「清单主地址 → 备用地址 → 加速节点 → 直连」自动换线路，单条只等 12 秒。

- **跟随系统代理**（默认开）：开着代理软件的用户无需任何配置；若本机代理异常反而连不上，
  在「设置 → 更新」取消勾选即改为纯直连。
- **换加速节点**：内置的是公益节点，可能失效或限流。在「加速节点」里填自己可用的前缀
  （多个用英文分号隔开，如 `https://gh-proxy.com/`）；填 `direct` 则完全不走加速。
- **国内镜像清单**：设置页可整条替换（换仓库 / 换目录 / 换自建站点）；留空 = 禁用国内源，只查 GitHub。
- **版本清单地址**：填了就以它为准并独占（私有源 / 内网 / 自建站点），不再查公开镜像。
  设置页点「立即检查更新」会直接使用框里尚未保存的地址，方便先试通再点「确定」。

#### 发布侧：Gitee 制品仓库

`yann9/dist` 是**所有项目共用**的 dist 制品库，按项目分目录，流水线只读写自己的子目录：

```
yann9/dist (master)
├── TaskScheduler/
│   ├── update.json                    ← 客户端读的固定清单
│   ├── TaskScheduler-setup.exe        ← 每次发版覆盖，固定名
│   └── TaskScheduler-portable.zip
└── <其它项目>/                        ← 完全不碰
```

`release.yml` 在发完 GitHub Release 后，用 `GITEE_TOKEN` 把这三个文件推上去。配置方法：
GitHub 仓库 → Settings → Secrets and variables → Actions → Secrets → `GITEE_TOKEN`
（Gitee 私人令牌，`projects` 写权限）。可用仓库变量 `GITEE_DIST_REPO` / `GITEE_DIST_DIR`
覆盖默认仓库与目录；令牌不属于仓库 owner 时，用 `GITEE_DIST_USER` 指定令牌所属账号的登录名。
**没配令牌就整步跳过**，GitHub 发布照常，客户端自动回退；配了令牌却推不上去会让这次
run 变红（早期版本用 `continue-on-error` 把失败包成绿勾，容易漏看）。

认证走 Gitee 官方支持的 `https://<登录名>:<私人令牌>@gitee.com/...` 单一凭据 URL。
两个已踩过的坑：URL 限定版的 `http.https://gitee.com/.extraheader` 在 `git push` 时不会生效
（公开仓库 clone 免登录，会把问题掩盖到 push 才暴露）；而同时叠加全局 `http.extraHeader`
与 URL 凭据会发出重复的 `Authorization` 头，Gitee 直接回 `Unauthorized`。
令牌自检会用 `/api/v5/user` 确认令牌有效性并取真实登录名，用 `/api/v5/repos/...` 确认 push 权限，
诊断结果会打印在 CI 日志里。所有 git 输出都做了脱敏，令牌及其 URL 转义形式都不会写进日志。

**为什么不用 Gitee 的 `releases/latest`**：仓库是多项目共用的，latest 会指向别的项目
最新发的那个 Release，tag 也会跨项目撞名。固定清单按目录隔离、内容完全由 CI 控制，
还不受草稿 / 预发布干扰。客户端也兼容 Gitee Release 响应的 `created_at` 字段。

**一个实测限制**：Gitee 的 `raw/` 路径对较大的 `.exe` 会返回 403（小 JSON、图片正常；
Release 附件直链 15MB 也能免登录下载）。所以清单里总带 `fallbackUrl` 指向 GitHub Release，
主地址失败时客户端自动换过去并套用加速节点——最坏情况只是下载绕回加速节点，
检查更新始终国内直连。若安装包变大导致 raw 长期 403，把清单的 `url` 改成 Gitee Release
附件直链（`https://gitee.com/{owner}/{repo}/releases/download/{tag}/{file}`）即可，客户端无需改动。

清单格式（大小写不敏感，镜像与自定义清单同格式）：

```json
{
  "version": "1.2.0",
  "title": "1.2.0 更新",
  "notes": "更新说明，支持多行",
  "url": "https://gitee.com/yann9/dist/raw/master/TaskScheduler/TaskScheduler-setup.exe",
  "fallbackUrl": "https://github.com/yann9/TaskScheduler/releases/download/v1.2.0/TaskScheduler-1.2.0-setup.exe",
  "pageUrl": "https://gitee.com/yann9/dist/tree/master/TaskScheduler",
  "fileName": "TaskScheduler-1.2.0-setup.exe",
  "publishedAt": "2026-11-06T10:00:00Z"
}
```

`version` 必填；`url` 留空时只提供「打开下载页」；`fallbackUrl` 是主地址失败后的备用下载地址，
若属于 GitHub 域名会自动套用加速节点。版本比较按数字段进行（`1.10.0 > 1.9.0`）。
GitHub Releases 的 latest 接口也仍是有效的回退源，会从 `assets` 里挑 `*-setup.exe`。

### 任务导入导出

入口：**文件 → 导入任务… / 导出全部任务… / 导出选中任务…**。

- 导出文件带 `Format` / `FormatVersion` / 导出时间与程序版本，内容为触发器 / 条件 / 动作配置，
  不含运行记录与本机排期（`NextRunTime`、`HasScheduled`、随机窗口锚点等）。
- 导入时可选择**合并追加**（默认）或**覆盖全部**；覆盖前自动把 `tasks.json` 备份为
  `tasks.backup-<时间戳>.json`，完成后显示备份路径。
- 导入的任务一律重新分配 ID、**默认停用**，清掉运行记录与排期，确认无误后手动启用，避免导入瞬间双跑。
- 服务模式下「覆盖全部」通过一次 IPC 往返在服务端原子替换任务库，不会逐条落盘留下半套数据。
- 反序列化一律走 `JsonSafety.Persistent` 的类型白名单，外部文件不能绕过防提权闸门。
- 兼容裸数组格式（例如手工备份的 `tasks.json` 片段），会按旧格式提示后导入。

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
