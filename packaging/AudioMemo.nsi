# -*- coding: utf-8 -*-
Unicode true
ManifestDPIAware true
ManifestSupportedOS all
RequestExecutionLevel user
CRCCheck force
SetCompressor /SOLID lzma
SetCompressorDictSize 32
AllowSkipFiles off

!include MUI2.nsh
!include LogicLib.nsh
!include "${__FILEDIR__}\SetupGuard.nsh"
!include "${PAYLOAD_INCLUDE}"

!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\AudioMemo"

Name "AudioMemo"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\AudioMemo"
ShowInstDetails show
ShowUninstDetails show
VIProductVersion "${APP_VERSION}"
VIFileVersion "${APP_VERSION}"
VIAddVersionKey /LANG=1033 "ProductName" "AudioMemo"
VIAddVersionKey /LANG=1033 "CompanyName" "Afanti Ma"
VIAddVersionKey /LANG=1033 "LegalCopyright" "${APP_COPYRIGHT}"
VIAddVersionKey /LANG=1033 "FileDescription" "AudioMemo per-user installer"
VIAddVersionKey /LANG=1033 "FileVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=1033 "ProductVersion" "${DISPLAY_VERSION}"
VIAddVersionKey /LANG=1033 "OriginalFilename" "${OUTPUT_NAME}"

!define MUI_ICON "${APP_ICON}"
!define MUI_UNICON "${APP_ICON}"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "$(WelcomeText)"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_TEXT "$(FinishedText)"
!insertmacro MUI_PAGE_FINISH
!define MUI_UNCONFIRMPAGE_TEXT_TOP "$(UninstallText)"
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "SimpChinese"

LangString WelcomeText ${LANG_ENGLISH} "Install AudioMemo for your Windows account in:$\r$\n$LOCALAPPDATA\Programs\AudioMemo$\r$\n$\r$\nNo administrator permission is needed. The .NET runtime is included, and this installer does not download anything.$\r$\n$\r$\nRecordings, settings, and models are kept separately. On a new PC, the first-run wizard requires an audio device and saving folder. Speech models and LLM setup are optional; local speech also needs Microsoft's Visual C++ 2022 x64 runtime.$\r$\n$\r$\nClose AudioMemo normally before installing. Existing portable copies will not be moved or removed."
LangString WelcomeText ${LANG_SIMPCHINESE} "为当前 Windows 用户安装 AudioMemo，位置为：$\r$\n$LOCALAPPDATA\Programs\AudioMemo$\r$\n$\r$\n无需管理员权限，已包含 .NET 运行库，安装程序不会下载任何内容。$\r$\n$\r$\n录音、设置和模型单独保存。在新电脑上，首次设置向导要求配置音频设备和保存文件夹。语音模型和大语言模型设置可以跳过；本地语音还需要微软 Visual C++ 2022 x64 运行库。$\r$\n$\r$\n安装前请正常关闭 AudioMemo。现有便携版不会被移动或删除。"
LangString FinishedText ${LANG_ENGLISH} "AudioMemo is ready. Open it from the Start menu when you want to begin.$\r$\n$\r$\nThe installer has not started the app or recording. Your existing settings, downloaded models, and recordings are unchanged.$\r$\n$\r$\nHelp & About contains the offline user guide and your keyboard shortcuts. Installation notes are in support\Docs\INSTALLATION.txt."
LangString FinishedText ${LANG_SIMPCHINESE} "AudioMemo 已安装。需要使用时，请从开始菜单打开。$\r$\n$\r$\n安装程序没有启动应用或录音。已有设置、下载的模型和录音保持不变。$\r$\n$\r$\n“帮助与关于”中提供离线用户指南和快捷键列表。安装说明位于 support\Docs\INSTALLATION.txt。"
LangString CoreSection ${LANG_ENGLISH} "AudioMemo (required)"
LangString CoreSection ${LANG_SIMPCHINESE} "AudioMemo（必需）"
LangString DesktopSection ${LANG_ENGLISH} "Desktop shortcut"
LangString DesktopSection ${LANG_SIMPCHINESE} "桌面快捷方式"
LangString UnsupportedWindows ${LANG_ENGLISH} "AudioMemo requires Windows 11 on an Intel/AMD x64 PC. Nothing was installed or removed."
LangString UnsupportedWindows ${LANG_SIMPCHINESE} "AudioMemo 需要 Intel/AMD x64 电脑上的 Windows 11。没有安装或删除任何内容。"
LangString CloseAudioMemo ${LANG_ENGLISH} "AudioMemo or another AudioMemo installer/uninstaller is open. Close it normally so recordings and unfinished work are saved, then choose Retry. Setup will never force-close it."
LangString CloseAudioMemo ${LANG_SIMPCHINESE} "AudioMemo 或其安装/卸载程序正在运行。请正常关闭以保存录音和未完成的工作，然后选择“重试”。安装程序不会强制关闭它。"
LangString IdentityCheckFailed ${LANG_ENGLISH} "Setup could not safely check the current user's AudioMemo session. Nothing was installed or removed."
LangString IdentityCheckFailed ${LANG_SIMPCHINESE} "无法安全检查当前用户的 AudioMemo 会话。没有安装或删除任何内容。"
LangString DestinationInUse ${LANG_ENGLISH} "The installation folder already contains files but is not a registered AudioMemo setup installation:$\r$\n$INSTDIR$\r$\n$\r$\nTo avoid overwriting unrelated files, setup has stopped. Keep or move the existing folder before trying again."
LangString DestinationInUse ${LANG_SIMPCHINESE} "安装文件夹已有文件，但不是已登记的 AudioMemo 安装目录：$\r$\n$INSTDIR$\r$\n$\r$\n为避免覆盖其他文件，安装已停止。请先保留或移走已有文件夹，再重试。"
LangString SetupFailed ${LANG_ENGLISH} "Setup could not finish writing the application, shortcuts, or uninstall information. No recording data was changed. Correct the disk or permission problem and run setup again."
LangString SetupFailed ${LANG_SIMPCHINESE} "无法完成应用文件、快捷方式或卸载信息的写入。没有修改录音数据。请处理磁盘或权限问题后重新运行安装程序。"
LangString UninstallText ${LANG_ENGLISH} "Remove AudioMemo and the shortcuts created by this installer. Recordings, settings, downloaded models, message history, and files you added will be kept. Close AudioMemo normally before continuing."
LangString UninstallText ${LANG_SIMPCHINESE} "移除 AudioMemo 及本安装程序创建的快捷方式。录音、设置、已下载模型、消息历史和您自行添加的文件将被保留。继续前请正常关闭 AudioMemo。"
LangString WrongUninstallLocation ${LANG_ENGLISH} "The uninstaller is not in its registered installation folder for this Windows user. Nothing was removed. Run the uninstaller from the original installed location."
LangString WrongUninstallLocation ${LANG_SIMPCHINESE} "卸载程序不在当前 Windows 用户登记的安装位置。没有删除任何内容。请从原安装位置运行卸载程序。"
LangString UninstallFailed ${LANG_ENGLISH} "Some application files could not be removed. Close programs using this installation and run the uninstaller again. Your recordings, settings, and models have not been deleted."
LangString UninstallFailed ${LANG_SIMPCHINESE} "部分应用文件无法移除。请关闭使用此安装目录的程序，再次运行卸载程序。您的录音、设置和模型未被删除。"

!insertmacro DefineAudioMemoGuard ""
!insertmacro DefineAudioMemoGuard "un."

Function .onInit
    SetShellVarContext current
    SetRegView 64
    ; Use one separate per-user program folder; do not honor a /D data-folder override.
    StrCpy $INSTDIR "$LOCALAPPDATA\Programs\AudioMemo"
    !insertmacro MUI_LANGDLL_DISPLAY
    Call AcquireAudioMemoMutex
    Call VerifyDestination
FunctionEnd

Function VerifyDestination
    ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "InstallLocation"
    ${If} $0 == $INSTDIR
        Return
    ${EndIf}
    FindFirst $0 $1 "$INSTDIR\*"
    StrCmp $0 "" destination_empty
destination_next:
    StrCmp $1 "" destination_done
    StrCmp $1 "." destination_continue
    StrCmp $1 ".." destination_continue
    FindClose $0
    MessageBox MB_OK|MB_ICONSTOP "$(DestinationInUse)" /SD IDOK
    SetErrorLevel 5
    Quit
destination_continue:
    FindNext $0 $1
    Goto destination_next
destination_done:
    FindClose $0
destination_empty:
FunctionEnd

Section "$(CoreSection)" Core
    SectionIn RO
    Call VerifyDestination
    SetOverwrite on
    ClearErrors
    !insertmacro InstallPayload
    WriteUninstaller "$INSTDIR\Uninstall.exe"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "AudioMemo"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${DISPLAY_VERSION}"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "Afanti Ma"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\AudioMemo.exe,0"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
    WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" ${INSTALL_SIZE_KB}
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "InstallerLanguage" $LANGUAGE
    CreateDirectory "$SMPROGRAMS\AudioMemo"
    SetOutPath "$INSTDIR"
    CreateShortcut "$SMPROGRAMS\AudioMemo\AudioMemo.lnk" "$INSTDIR\AudioMemo.exe"
    IfErrors setup_failed
    Goto setup_done
setup_failed:
    MessageBox MB_OK|MB_ICONSTOP "$(SetupFailed)" /SD IDOK
    SetErrorLevel 6
    Abort
setup_done:
SectionEnd

Section /o "$(DesktopSection)" Desktop
    ClearErrors
    CreateShortcut "$DESKTOP\AudioMemo.lnk" "$INSTDIR\AudioMemo.exe"
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "DesktopShortcut" 1
    ${If} ${Errors}
        MessageBox MB_OK|MB_ICONSTOP "$(SetupFailed)" /SD IDOK
        SetErrorLevel 6
        Abort
    ${EndIf}
SectionEnd

Function un.onInit
    SetShellVarContext current
    SetRegView 64
    ReadRegDWORD $LANGUAGE HKCU "${UNINSTALL_KEY}" "InstallerLanguage"
    StrCpy $0 "$LOCALAPPDATA\Programs\AudioMemo"
    ReadRegStr $1 HKCU "${UNINSTALL_KEY}" "InstallLocation"
    ${If} $INSTDIR != $0
    ${OrIf} $INSTDIR != $1
        MessageBox MB_OK|MB_ICONSTOP "$(WrongUninstallLocation)" /SD IDOK
        SetErrorLevel 5
        Quit
    ${EndIf}
    Call un.AcquireAudioMemoMutex
FunctionEnd

Section "Uninstall"
    ClearErrors
    !insertmacro RemovePayloadFiles
    ${If} ${Errors}
        MessageBox MB_OK|MB_ICONSTOP "$(UninstallFailed)" /SD IDOK
        SetErrorLevel 7
        Abort
    ${EndIf}
    ReadRegDWORD $0 HKCU "${UNINSTALL_KEY}" "DesktopShortcut"
    ${If} $0 = 1
        Delete "$DESKTOP\AudioMemo.lnk"
    ${EndIf}
    Delete "$SMPROGRAMS\AudioMemo\AudioMemo.lnk"
    RMDir "$SMPROGRAMS\AudioMemo"
    DeleteRegKey HKCU "${UNINSTALL_KEY}"
    Delete "$INSTDIR\Uninstall.exe"
    ; Remove only empty directories, never user-added recordings or other files.
    SetOutPath "$TEMP"
    !insertmacro RemovePayloadDirectories
    RMDir "$INSTDIR"
SectionEnd
