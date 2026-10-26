!ifndef AUDIOMEMO_SETUP_GUARD
!define AUDIOMEMO_SETUP_GUARD

!include LogicLib.nsh
!include WinVer.nsh
!include x64.nsh

!ifndef AUDIOMEMO_MUTEX_PREFIX
!define AUDIOMEMO_MUTEX_PREFIX "Local\AudioMemo-"
!endif

Var AudioMemoMutex

!macro DefineAudioMemoGuard Prefix
Function ${Prefix}AcquireAudioMemoMutex
    ${IfNot} ${IsNativeAMD64}
        MessageBox MB_OK|MB_ICONSTOP "$(UnsupportedWindows)" /SD IDOK
        SetErrorLevel 2
        Quit
    ${EndIf}
    ${IfNot} ${AtLeastBuild} 22000
        MessageBox MB_OK|MB_ICONSTOP "$(UnsupportedWindows)" /SD IDOK
        SetErrorLevel 2
        Quit
    ${EndIf}

    System::Call 'kernel32::GetCurrentProcess() p .r0'
    System::Call 'advapi32::OpenProcessToken(p r0, i 8, *p .r1) i .r2'
    StrCmp $2 0 identity_failed
    System::Call 'advapi32::GetTokenInformation(p r1, i 1, p 0, i 0, *i .r2) i .r3'
    ${If} $2 = 0
        System::Call 'kernel32::CloseHandle(p r1)'
        Goto identity_failed
    ${EndIf}
    System::Alloc $2
    Pop $3
    ${If} $3 = 0
        System::Call 'kernel32::CloseHandle(p r1)'
        Goto identity_failed
    ${EndIf}
    System::Call 'advapi32::GetTokenInformation(p r1, i 1, p r3, i r2, *i .r2) i .r4'
    System::Call 'kernel32::CloseHandle(p r1)'
    ${If} $4 = 0
        System::Free $3
        Goto identity_failed
    ${EndIf}
    System::Call '*$3(p .r4)'
    System::Call 'advapi32::ConvertSidToStringSidW(p r4, *p .r5) i .r6'
    ${If} $6 = 0
        System::Free $3
        Goto identity_failed
    ${EndIf}
    System::Call '*$5(&w${NSIS_MAX_STRLEN} .r6)'
    System::Call 'kernel32::LocalFree(p r5)'
    System::Free $3
    StrCmp $6 "" identity_failed

mutex_retry:
    ; The existing app uses this SID-scoped mutex. Holding it also blocks new launches.
    System::Call 'kernel32::CreateMutexW(p 0, i 0, w "${AUDIOMEMO_MUTEX_PREFIX}$6") p .r0 ?e'
    Pop $1
    StrCmp $0 0 identity_failed
    ${If} $1 = 183
        System::Call 'kernel32::CloseHandle(p r0)'
        IfSilent mutex_busy
        MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "$(CloseAudioMemo)" /SD IDCANCEL IDRETRY mutex_retry
        Goto mutex_busy
    ${EndIf}
    StrCpy $AudioMemoMutex $0
    Return

mutex_busy:
    SetErrorLevel 3
    Quit
identity_failed:
    MessageBox MB_OK|MB_ICONSTOP "$(IdentityCheckFailed)" /SD IDOK
    SetErrorLevel 4
    Quit
FunctionEnd
!macroend

!endif
