Unicode true
RequestExecutionLevel user
SetCompressor /SOLID lzma
!include "MUI2.nsh"
!include "x64.nsh"

!ifndef PAYLOAD
  !error "Define PAYLOAD as the directory containing the Windows application."
!endif
!ifndef OUTPUT
  !define OUTPUT "KassaSetup.exe"
!endif
Name "Kassa Minimarket"
OutFile "${OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\KassaMinimarket"
InstallDirRegKey HKCU "Software\KassaMinimarket" "InstallDir"
VIProductVersion "0.5.9.0"
VIAddVersionKey /LANG=1033 "ProductName" "Kassa Minimarket"
VIAddVersionKey /LANG=1033 "FileDescription" "Kassa Minimarket pilot installer"
VIAddVersionKey /LANG=1033 "FileVersion" "0.5.9"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Kassa Minimarket"

!define MUI_ABORTWARNING
!define MUI_LANGDLL_REGISTRY_ROOT HKCU
!define MUI_LANGDLL_REGISTRY_KEY "Software\KassaMinimarket"
!define MUI_LANGDLL_REGISTRY_VALUENAME "InstallerLanguage"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Russian"
!insertmacro MUI_LANGUAGE "Uzbek"

LangString CloseApp ${LANG_RUSSIAN} "Закройте программу «Касса минимаркета» и повторите."
LangString CloseApp ${LANG_UZBEK} "Minimarket kassasini yoping va qayta urinib ko‘ring."
LangString X64Only ${LANG_RUSSIAN} "Требуется 64-разрядная Windows."
LangString X64Only ${LANG_UZBEK} "64 bitli Windows talab qilinadi."

!macro Guard
    System::Call 'kernel32::CreateMutexW(p 0, i 0, w "Local\KasaDesktop.Stage1") p .r0 ?e'
    Pop $1
    ${If} $0 == 0
    ${OrIf} $1 == 183
        MessageBox MB_OK|MB_ICONSTOP "$(CloseApp)"
        Abort
    ${EndIf}
!macroend

Function .onInit
    SetRegView 64
    !insertmacro MUI_LANGDLL_DISPLAY
    ${IfNot} ${RunningX64}
        MessageBox MB_OK|MB_ICONSTOP "$(X64Only)"
        Abort
    ${EndIf}
    !insertmacro Guard
FunctionEnd

Function un.onInit
    SetRegView 64
    !insertmacro MUI_UNGETLANGUAGE
    !insertmacro Guard
FunctionEnd

Section "Application"
    SetShellVarContext current
    SetOutPath "$INSTDIR"
    File "${PAYLOAD}/KasaDesktop.exe"
    File "${PAYLOAD}/KasaDesktop.exe.config"
    File "${PAYLOAD}/Microsoft.Web.WebView2.Core.dll"
    File "${PAYLOAD}/Microsoft.Web.WebView2.WinForms.dll"
    File "${PAYLOAD}/KasaPilot.example.json"
    File "${PAYLOAD}/PrinterDrivers.example.json"
    File "${PAYLOAD}/PRINTER_DRIVERS.md"
    File "${PAYLOAD}/START.txt"
    File "${PAYLOAD}/PILOT.md"
    SetOutPath "$INSTDIR\web"
    File "${PAYLOAD}/web/*"
    SetOutPath "$INSTDIR\licenses"
    File "${PAYLOAD}/licenses/*"
    SetOutPath "$INSTDIR\runtimes\win-x64\native"
    File "${PAYLOAD}/runtimes/win-x64/native/WebView2Loader.dll"
    SetOutPath "$INSTDIR"
    WriteUninstaller "$INSTDIR\Uninstall.exe"
    CreateShortcut "$SMPROGRAMS\Kassa Minimarket.lnk" "$INSTDIR\KasaDesktop.exe" "" "$INSTDIR\KasaDesktop.exe" 0
    WriteRegStr HKCU "Software\KassaMinimarket" "InstallDir" "$INSTDIR"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\KassaMinimarket" "DisplayName" "Kassa Minimarket"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\KassaMinimarket" "DisplayVersion" "0.5.9"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\KassaMinimarket" "UninstallString" '"$INSTDIR\Uninstall.exe"'
    WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\KassaMinimarket" "NoModify" 1
    WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\KassaMinimarket" "NoRepair" 1
SectionEnd

Section "Uninstall"
    SetShellVarContext current
    Delete "$SMPROGRAMS\Kassa Minimarket.lnk"
    Delete "$INSTDIR\KasaDesktop.exe"
    Delete "$INSTDIR\KasaDesktop.exe.config"
    Delete "$INSTDIR\Microsoft.Web.WebView2.Core.dll"
    Delete "$INSTDIR\Microsoft.Web.WebView2.WinForms.dll"
    Delete "$INSTDIR\KasaPilot.example.json"
    Delete "$INSTDIR\PrinterDrivers.example.json"
    Delete "$INSTDIR\PRINTER_DRIVERS.md"
    Delete "$INSTDIR\START.txt"
    Delete "$INSTDIR\PILOT.md"
    Delete "$INSTDIR\web\app.css"
    Delete "$INSTDIR\web\app.js"
    Delete "$INSTDIR\web\design.css"
    Delete "$INSTDIR\web\i18n.js"
    Delete "$INSTDIR\web\management-demo.js"
    Delete "$INSTDIR\web\management.js"
    Delete "$INSTDIR\web\model.js"
    Delete "$INSTDIR\web\pilot.js"
    Delete "$INSTDIR\web\uz.js"
    Delete "$INSTDIR\web\warehouse-demo.js"
    Delete "$INSTDIR\web\warehouse.js"
    Delete "$INSTDIR\web\index.html"
    Delete "$INSTDIR\licenses\WebView2-LICENSE.txt"
    Delete "$INSTDIR\licenses\WebView2-NOTICE.txt"
    Delete "$INSTDIR\runtimes\win-x64\native\WebView2Loader.dll"
    Delete "$INSTDIR\Uninstall.exe"
    RMDir "$INSTDIR\web"
    RMDir "$INSTDIR\licenses"
    RMDir "$INSTDIR\runtimes\win-x64\native"
    RMDir "$INSTDIR\runtimes\win-x64"
    RMDir "$INSTDIR\runtimes"
    RMDir "$INSTDIR"
    DeleteRegKey HKCU "Software\KassaMinimarket"
    DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\KassaMinimarket"
SectionEnd
