# -*- coding: utf-8 -*-
Unicode true
!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
Name "Grove"
OutFile "${INSTALLER_OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\Grove"
RequestExecutionLevel user
ManifestDPIAware true
SetCompressor /SOLID lzma
SetCompressorDictSize 32
VIProductVersion "${FILE_VERSION}"
VIAddVersionKey "ProductName" "Grove"
VIAddVersionKey "FileDescription" "Grove ${APP_VERSION} (${APP_ARCH}) Setup"
VIAddVersionKey "FileVersion" "${APP_VERSION}"
VIAddVersionKey "ProductVersion" "${APP_VERSION}"
VIAddVersionKey "LegalCopyright" ""
Icon "${APP_ICON}"
UninstallIcon "${APP_ICON}"
!define MUI_ICON "${APP_ICON}"
!define MUI_UNICON "${APP_ICON}"
!define MUI_WELCOMEPAGE_TEXT "$(GroveWelcome)"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\Grove.exe"
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!define MUI_FINISHPAGE_TEXT "$(GroveFinished)"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "SimpChinese"
SetFont /LANG=1033 "Segoe UI" 9
SetFont /LANG=2052 "Microsoft YaHei UI" 9
LangString GroveWelcome 1033 "Install Grove ${APP_VERSION} (${APP_ARCH}) for your current Windows user. Close Grove before continuing. Existing repository data and settings are preserved."
LangString GroveWelcome 2052 "为当前 Windows 用户安装 Grove ${APP_VERSION} (${APP_ARCH})。继续之前请先关闭 Grove。现有仓库、配置和评论数据会保留。"
LangString GroveFinished 1033 "Grove is installed. Git and Microsoft Edge WebView2 Runtime must be installed separately. Existing settings are preserved."
LangString GroveFinished 2052 "Grove 已安装。请另行安装 Git 和 Microsoft Edge WebView2 Runtime。现有配置会保留。"
Var NoIntegration

Function .onInit
  SetShellVarContext current
  SetRegView 64
  StrCpy $NoIntegration "0"
  ${GetParameters} $0
  ${GetOptions} $0 "/NOINTEGRATION" $1
  ${IfNot} ${Errors}
    StrCpy $NoIntegration "1"
  ${EndIf}
!if "${APP_ARCH}" == "arm64"
  ${IfNot} ${IsNativeARM64}
!else
  ${IfNot} ${IsNativeAMD64}
!endif
    MessageBox MB_OK|MB_ICONSTOP "This package requires native Windows ${APP_ARCH}. Download the matching Grove installer."
    SetErrorLevel 1633
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_OK|MB_ICONSTOP "Grove requires Windows 10 or later."
    SetErrorLevel 1633
    Abort
  ${EndIf}
FunctionEnd

Function .onVerifyInstDir
  GetFullPathName $INSTDIR "$INSTDIR"
  ${GetRoot} "$INSTDIR" $0
  StrCmp "$INSTDIR" "$0\" invalid
  StrCmp "$INSTDIR" "$LOCALAPPDATA" invalid
  StrCmp "$INSTDIR" "$LOCALAPPDATA\GitX" invalid
  Return
invalid:
  Abort
FunctionEnd

Section "Grove"
  SetOverwrite on
  !include "${PAYLOAD_FILES}"
  WriteINIStr "$INSTDIR\install-options.ini" "Grove" "NoIntegration" "$NoIntegration"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  ${If} $NoIntegration != "1"
    CreateShortCut "$SMPROGRAMS\Grove.lnk" "$INSTDIR\Grove.exe"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Grove-${APP_ARCH}" "DisplayName" "Grove (${APP_ARCH})"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Grove-${APP_ARCH}" "DisplayVersion" "${APP_VERSION}"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Grove-${APP_ARCH}" "Publisher" "Grove"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Grove-${APP_ARCH}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Grove-${APP_ARCH}" "DisplayIcon" "$INSTDIR\Grove.exe"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Grove-${APP_ARCH}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Grove-${APP_ARCH}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
    WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Grove-${APP_ARCH}" "NoModify" 1
    WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Grove-${APP_ARCH}" "NoRepair" 1
  ${EndIf}
SectionEnd

Section "Uninstall"
  SetShellVarContext current
  SetRegView 64
  ReadINIStr $NoIntegration "$INSTDIR\install-options.ini" "Grove" "NoIntegration"
  ${If} $NoIntegration != "1"
    ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Grove-${APP_ARCH}" "InstallLocation"
    ${If} $0 == $INSTDIR
      Delete "$SMPROGRAMS\Grove.lnk"
      DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Grove-${APP_ARCH}"
    ${EndIf}
  ${EndIf}
  ; Only build-manifest files are deleted. Never recursively delete the chosen
  ; directory, user repositories, configuration, or extra user-created files.
  !include "${UNINSTALL_FILES}"
  Delete "$INSTDIR\install-options.ini"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
SectionEnd
