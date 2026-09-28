; ============================================================================
;  自动化任务调度器 —— NSIS 安装脚本
;
;  编译： makensis.exe TaskScheduler.nsi   （由 publish.ps1 自动调用）
;  产物： dist\Setup.exe                  （单文件，内含压缩后的整个 dist）
;  静默： Setup.exe /S
;
;  ---- 改这个脚本前必读 ----
;    1) 所有"停服务 / 停界面实例"的动作必须发生在文件被覆盖之前（主 Section 开头）。
;    2) 自启的值名必须与主程序 Engine.AutoStartRegistration.ValueName 一致，
;       命令行开关必须与 AutoStartRegistration.MinimizedArgument 一致。
;       不一致的后果：登录被拉起两个实例，或者设了"进托盘"却弹出主窗口。
;       publish.ps1 里有一道自动比对（契约检查）拦住不一致。
;    3) 服务用主程序自己的 --install-service / --uninstall-service，不要在这里重写
;       sc create —— 那条命令的引号转义踩过坑（binPath 值里的引号没转义会被空格切开）。
;    4) 绝对不要用 tasklist 的退出码判断"进程还在不在"。实测（Win11 10.0.26200）：
;          tasklist /FI "IMAGENAME eq zzz_not_exist.exe" /NH   →  退出码 0
;       找到了和没找到都返回 0，只有参数非法才返回 1。用它判断存在性 = 恒为真。
;       要确认进程是否已退出，用 taskkill 的退出码（0=杀掉 / 128=没有该进程）。
;    5) NSIS 是 32 位安装器：64 位系统上读写 HKLM 必须在 .onInit / un.onInit 里
;       SetRegView 64，否则我们的卸载键/服务键会在 32 位视图里"消失"。
; ============================================================================

; ---- 契约常量：与主程序 C# 侧的字符串/ID 必须一致，publish.ps1 自动比对 ----
!define TS_SERVICE_NAME     "TaskSchedulerSvc"            ; = ServiceControl.ServiceName
!define TS_AUTOSTART_VALUE  "TaskScheduler"               ; = AutoStartRegistration.ValueName
!define TS_MINIMIZED_ARG    "--minimized"                 ; = AutoStartRegistration.MinimizedArgument
!define TS_EXIT_EVENT       "Local\TaskSchedulerUI_Exit"  ; = App.ExitEventName
!define TS_EXIT_MESSAGE     0x805A                        ; = NativeMethods.ExitRequestMessageId

; ---- 其它常量 ----
!define TS_APP_NAME  "自动化任务调度器"
!define TS_EXE_NAME  "TaskScheduler.exe"
!define TS_VERSION   "1.0.0"
!define TS_DIR_NAME  "TaskScheduler"
; 本程序的卸载键（写入卸载信息用）
!define UninstallKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\TaskScheduler"
; 服务是否还注册着，就看这个键。sc delete 之后键会被删掉；
; 主程序 --uninstall-service 内部走的就是 sc delete + ServiceController。
!define ServiceKey "SYSTEM\CurrentControlSet\Services\${TS_SERVICE_NAME}"
!define RunKey     "Software\Microsoft\Windows\CurrentVersion\Run"
!define Net48Key   "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"
!define Net48ReleaseMin 528040

; ---- 自定义消息/事件的数值 ----
!define EVENT_MODIFY_STATE 0x0002
!define HwndBroadcast      0xFFFF
; 优雅退出的等待时长。刻意**短**：对方正常应答时是毫秒级的，
; 等到几秒还没动静就说明它卡住了，再等只是折磨用户 —— 后面有 taskkill 兜底。
!define GracefulWaitMs  2500    ; 有界面实例（命名事件存在）时
!define BroadcastWaitMs 1200    ; 没探到实例、只靠广播时
; 卸旧服务的等待上限：20 × 500ms = 10 秒。
!define ServiceUninstallRounds 20
!define TaskKillNoProcess 128

; ============================================================================
;  常规设置
; ============================================================================
Unicode true                      ; 3.x Unicode 构建：全字符串宽字符
ManifestDPIAware true             ; 125%/150% 缩放下不模糊
XPStyle on
SetCompressor /SOLID lzma         ; 压缩率高、包更小（编译略慢）
RequestExecutionLevel admin       ; 要写 Program Files、注册服务 → 必须提权
ShowInstDetails show              ; 让 DetailPrint 的状态文字可见
BrandingText "${TS_APP_NAME}"

Name "${TS_APP_NAME}"
OutFile "dist\Setup.exe"
; 默认目录在 .onInit 里手动决定：优先复用上次安装路径，否则回退到 Program Files。

; ============================================================================
;  MUI2 页面
; ============================================================================
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "nsDialogs.nsh"
!include "WinVer.nsh"

!define MUI_ICON   "TaskScheduler\app.ico"
!define MUI_UNICON "TaskScheduler\app.ico"

; 欢迎页与各页头部使用 MUI2 默认观感（不定义位图/头部图）。
!define MUI_ABORTWARNING

!define MUI_FINISHPAGE_RUN        "$INSTDIR\${TS_EXE_NAME}"
!define MUI_FINISHPAGE_RUN_TEXT   "立即启动 ${TS_APP_NAME}"
; 静默安装时欢迎/完成页自动跳过 → 不会突然弹出界面。

!insertmacro MUI_PAGE_WELCOME
Page custom OptionsPageCreate OptionsPageLeave   ; 三个勾选框（自启 / 桌面快捷 / 后台服务）
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "SimpChinese"

; ---- 版本信息（Setup.exe 属性 → 详细信息）----
VIProductVersion "${TS_VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName"     "${TS_APP_NAME}"
VIAddVersionKey /LANG=2052 "CompanyName"     "${TS_APP_NAME}"
VIAddVersionKey /LANG=2052 "FileDescription" "${TS_APP_NAME} 安装程序"
VIAddVersionKey /LANG=2052 "FileVersion"     "${TS_VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductVersion"  "${TS_VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright"  "${TS_APP_NAME}"

; ============================================================================
;  变量
; ============================================================================
Var OptDesktop        ; 桌面快捷方式（1=勾选）
Var OptAutoStart      ; 登录自启
Var OptInstallService ; 安装后台服务
Var hDlg
Var hChkDesktop
Var hChkAutoStart
Var hChkService
Var CommonDataDir     ; C:\ProgramData（提示日志路径用）

; ============================================================================
;  .onInit：64 位视图 / .NET 运行时检查 / 目录回填 / 选项默认值
; ============================================================================
Function .onInit
  ${If} ${RunningX64}
    SetRegView 64
  ${EndIf}

  ; net48 是 framework-dependent 的，缺运行时会"装得上、打不开"。
  ; 先查注册表 Release 值（528040 = .NET 4.8 在 Win10 1903+ 上的值），
  ; 让用户自己决定要不要继续，而不是装完才发现跑不起来。
  ReadRegDWORD $0 HKLM "${Net48Key}" "Release"
  ${If} $0 < ${Net48ReleaseMin}
    MessageBox MB_YESNO|MB_ICONEXCLAMATION \
      "未检测到 .NET Framework 4.8 运行时。$\n$\n本程序依赖它才能运行。在缺少运行时的系统上安装，$\n双击图标后不会有任何反应。$\n$\n是否仍要继续安装？" \
      IDYES net48_ok
    Abort
    net48_ok:
  ${EndIf}

  ; 默认目录：优先复用上次安装路径，否则回退到 Program Files
  ReadRegStr $0 HKLM "${UninstallKey}" "InstallLocation"
  ${If} $0 != ""
    StrCpy $INSTDIR $0
  ${ElseIf} ${RunningX64}
    StrCpy $INSTDIR "$PROGRAMFILES64\${TS_DIR_NAME}"
  ${Else}
    StrCpy $INSTDIR "$PROGRAMFILES\${TS_DIR_NAME}"
  ${EndIf}

  ; 三个选项默认全选。
  StrCpy $OptDesktop 1
  StrCpy $OptAutoStart 1
  StrCpy $OptInstallService 1

  ; 说明：NSIS 官方构建不支持 LogSet（NSIS_CONFIG_LOG 未定义），安装过程
  ; 线索以 detail 区文字 + 主程序自己的 %ProgramData%\TaskScheduler\scheduler.log 为准。
FunctionEnd

Function un.onInit
  ; ⚠ 卸载器是独立进程：卸载注册表键也必须用 64 位视图，
  ;   否则 64 位系统上会找不到自己写的卸载键，"设置 → 应用"里留下死条目。
  ${If} ${RunningX64}
    SetRegView 64
  ${EndIf}
FunctionEnd

; ============================================================================
;  选项页（nsDialogs）：三个自定义勾选项
; ============================================================================
Function OptionsPageCreate
  !insertmacro MUI_HEADER_TEXT "附加选项" "选择安装时需要执行的附加任务"
  nsDialogs::Create 1018
  Pop $hDlg
  ${NSD_CreateCheckbox} 0 18u  100% 12u "创建桌面快捷方式"
  Pop $hChkDesktop
  ${NSD_CreateCheckbox} 0 44u  100% 12u "登录 Windows 时自动启动（直接最小化到托盘）"
  Pop $hChkAutoStart
  ${NSD_CreateCheckbox} 0 70u  100% 12u "安装为 Windows 后台服务（开机自启，无需登录）"
  Pop $hChkService
  ${NSD_SetState} $hChkDesktop    ${BST_CHECKED}
  ${NSD_SetState} $hChkAutoStart  ${BST_CHECKED}
  ${NSD_SetState} $hChkService    ${BST_CHECKED}
  nsDialogs::Show
FunctionEnd

Function OptionsPageLeave
  ${NSD_GetState} $hChkDesktop    $OptDesktop
  ${NSD_GetState} $hChkAutoStart  $OptAutoStart
  ${NSD_GetState} $hChkService    $OptInstallService
FunctionEnd

; ============================================================================
;  停进程 / 停服务的工具函数
;
;  ★ 这一整套存在的唯一理由：**安装/卸载过程中绝不允许出现无限等待。**
;  规矩（实测教训，改这里务必保持）：
;    1) 不判断"某进程还在不在" —— 发信号 → 固定睡一小段 → taskkill 收尾，
;       用 taskkill 自己的退出码读结果（0=杀掉 / 128=没有该进程）。
;       没有探针，就没有"探针误报"这种失效模式。
;    2) 服务是否卸载完成，用注册表键判断，不碰进程名。
;    3) 任何等待都有上限，且用"固定轮次 × 固定睡眠"计数。
; ============================================================================
!macro StopRunningInstances un
Function ${un}StopRunningInstances
  DetailPrint "正在停止旧版本程序…"
  System::Store Push

  ; 通道 1：命名事件（同时充当探针）。不启动子进程，没有被挂住的风险。
  ; 返回值当"是否有界面实例"用：服务进程不创建这个事件。
  System::Call 'kernel32::OpenEventW(i ${EVENT_MODIFY_STATE}, i 0, w "${TS_EXIT_EVENT}") p .r0'
  ${If} $0 P<> 0
    System::Call 'kernel32::SetEvent(p r0)'
    System::Call 'kernel32::CloseHandle(p r0)'
    Sleep ${GracefulWaitMs}
  ${Else}
    Sleep ${BroadcastWaitMs}
  ${EndIf}

  ; 通道 2：窗口消息广播。覆盖"实例活着但没建出命名事件"的情况。同样不启动进程。
  System::Call 'user32::PostMessageW(p ${HwndBroadcast}, i ${TS_EXIT_MESSAGE}, i 0, i 0)'

  ; 通道 3：强杀 + 用 taskkill 的退出码读出真实结果。
  ;   0   = 确实结束了进程 → 优雅退出没生效（或杀的是残留服务进程），日志留证
  ;   128 = 没有该映像名的进程 → 优雅退出已生效，或本来就干净，最好的结果
  ;   其它= 权限不足 / taskkill 自身出错 → 会在文件覆盖阶段以别的方式暴露
  nsExec::Exec '"$SYSDIR\taskkill.exe" /IM ${TS_EXE_NAME} /F'
  Pop $1
  ${If} $1 = 0
    DetailPrint "优雅退出未生效，已强杀 ${TS_EXE_NAME}（本次排期存档可能丢失一次）"
  ${ElseIf} $1 = ${TaskKillNoProcess}
    DetailPrint "确认已无 ${TS_EXE_NAME} 在运行"
  ${Else}
    DetailPrint "警告：taskkill 返回退出码 $1，可能没能结束进程"
  ${EndIf}

  System::Store Pop
FunctionEnd
!macroend

!macro StopOldService un
Function ${un}StopOldService
  ClearErrors
  ReadRegStr $0 HKLM "${ServiceKey}" "Start"
  ${IfNot} ${Errors}
    ; 服务还在注册表里。必须用旧 exe 自己的 --uninstall-service：
    ; 它知道服务名，也有"停 + 等 SCM + 删 + 等清理"的完整逻辑（重写容易漏）。
    ; 但**不等待它结束**：服务停止最长可等 30 秒，
    ; 同步等待就是在赌；改成"发出去就走 + 用注册表键限时确认"。
    ${If} ${FileExists} "$INSTDIR\${TS_EXE_NAME}"
      DetailPrint "正在停止旧版本后台服务…"
      Exec '"$INSTDIR\${TS_EXE_NAME}" --uninstall-service'

      StrCpy $1 0
      ${Do}
        ClearErrors
        ReadRegStr $2 HKLM "${ServiceKey}" "Start"
        ${If} ${Errors}
          ${ExitDo}              ; 键没了 = 卸载完成
        ${EndIf}
        IntOp $1 $1 + 1
        ${If} $1 >= ${ServiceUninstallRounds}
          ${ExitDo}
        ${EndIf}
        Sleep 500
      ${Loop}

      ${If} $1 >= ${ServiceUninstallRounds}
        DetailPrint "警告：旧服务未在 10 秒内卸载完成，继续；残留进程会在下一步被强杀，装完再重新注册"
      ${Else}
        DetailPrint "旧后台服务已卸载"
      ${EndIf}
    ${EndIf}
  ${EndIf}
FunctionEnd
!macroend

!insertmacro StopRunningInstances ""
!insertmacro StopRunningInstances "un."
!insertmacro StopOldService ""
!insertmacro StopOldService "un."

; ============================================================================
;  主安装段
; ============================================================================
Section ""

  ; ---- 0) 停旧：必须发生在文件覆盖之前，否则文件被服务进程占用 ----
  Call StopOldService
  Call StopRunningInstances

  ; ---- 1) 文件（总是覆盖旧文件）----
  SetOutPath "$INSTDIR"
  ; Setup.exe 必须排除 —— 那是上一次编译产出的安装器本身，否则体积逐次翻倍
  File /r /x Setup.exe /x Setup.exe.config /x *.pdb /x *.xml "dist\*.*"

  ; ---- 2) 注册卸载信息 ----
  WriteUninstaller "$INSTDIR\uninstall.exe"
  WriteRegStr   HKLM "${UninstallKey}" "DisplayName"     "${TS_APP_NAME}"
  WriteRegStr   HKLM "${UninstallKey}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegStr   HKLM "${UninstallKey}" "DisplayIcon"     '"$INSTDIR\${TS_EXE_NAME}"'
  WriteRegStr   HKLM "${UninstallKey}" "DisplayVersion"  "${TS_VERSION}"
  WriteRegStr   HKLM "${UninstallKey}" "Publisher"       "${TS_APP_NAME}"
  WriteRegStr   HKLM "${UninstallKey}" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKLM "${UninstallKey}" "NoModify" 1
  WriteRegDWORD HKLM "${UninstallKey}" "NoRepair" 1

  ; ---- 3) 快捷方式 ----
  CreateDirectory "$SMPROGRAMS\${TS_APP_NAME}"
  CreateShortCut  "$SMPROGRAMS\${TS_APP_NAME}\${TS_APP_NAME}.lnk" "$INSTDIR\${TS_EXE_NAME}"
  CreateShortCut  "$SMPROGRAMS\${TS_APP_NAME}\卸载 ${TS_APP_NAME}.lnk" "$INSTDIR\uninstall.exe"
  ${If} $OptDesktop = 1
    CreateShortCut "$DESKTOP\${TS_APP_NAME}.lnk" "$INSTDIR\${TS_EXE_NAME}"
  ${EndIf}

  ; ---- 4) 登录自启。HKCU 与主程序设置页写的是同一个位置、同一个值名 ----
  ;    admin 提权却写 HKCU 是**明知而为**：常见情形（登录用户即管理员）UAC 不换人，
  ;    HKCU 仍指向同一个 hive。兜底：主程序设置页以注册表实况为准，错了也能修正。
  ${If} $OptAutoStart = 1
    WriteRegStr HKCU "${RunKey}" "${TS_AUTOSTART_VALUE}" \
      '$\"$INSTDIR\${TS_EXE_NAME}$\" ${TS_MINIMIZED_ARG}'
  ${EndIf}

  ; ---- 5) 注册后台服务，并检查退出码 ----
  ;    主程序是 WinExe 没有控制台，退出码是唯一的结果通道。
  ${If} $OptInstallService = 1
    DetailPrint "正在注册 Windows 后台服务…"
    ExecWait '"$INSTDIR\${TS_EXE_NAME}" --install-service' $0
    System::Call 'shell32::SHGetFolderPathW(p 0, i 0x0023, p 0, p 0, t .r0)'   ; ProgramData
    StrCpy $CommonDataDir $0
    ${If} $CommonDataDir == ""
      StrCpy $CommonDataDir "C:\ProgramData"
    ${EndIf}
    ${If} $0 == "error"
      MessageBox MB_ICONEXCLAMATION \
        "无法启动服务注册程序，后台服务没有安装。$\n$\n程序本体已安装完成，可以稍后在界面菜单里重试。"
    ${ElseIf} $0 <> 0
      MessageBox MB_ICONEXCLAMATION \
        "后台服务注册失败（退出码 $0）。$\n$\n程序本体已安装完成，可以稍后在界面菜单里重试。$\n详细原因见日志：$CommonDataDir\TaskScheduler\scheduler.log"
    ${Else}
      DetailPrint "后台服务已注册"
    ${EndIf}
  ${EndIf}

  DetailPrint "安装完成"
SectionEnd

; ============================================================================
;  卸载段
;  先停服务 + 停进程再删文件：服务进程占着 exe，不先停就删不干净。
;  不用 NSIS 的任何"无限等待"调用 —— 复用与安装侧同一套限时逻辑。
; ============================================================================
Section "un.Install"
  Call un.StopOldService
  Call un.StopRunningInstances

  Delete "$INSTDIR\uninstall.exe"
  RMDir /r "$INSTDIR"

  DeleteRegKey HKLM "${UninstallKey}"
  DeleteRegValue HKCU "${RunKey}" "${TS_AUTOSTART_VALUE}"

  Delete "$SMPROGRAMS\${TS_APP_NAME}\${TS_APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${TS_APP_NAME}\卸载 ${TS_APP_NAME}.lnk"
  RMDir  "$SMPROGRAMS\${TS_APP_NAME}"
  Delete "$DESKTOP\${TS_APP_NAME}.lnk"
  ; %ProgramData%\TaskScheduler 下的任务配置与运行记录**不动**
  ; （卸载只删软件，保留用户数据）
SectionEnd
