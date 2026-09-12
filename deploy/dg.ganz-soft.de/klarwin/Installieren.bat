@echo off
setlocal
set "TARGET=%LOCALAPPDATA%\KlarWin"
set "SRC=%~dp0"
echo KlarWin wird nach "%TARGET%" installiert ...
mkdir "%TARGET%" 2>nul
mkdir "%TARGET%\Tools" 2>nul
copy /Y "%SRC%KlarWin.exe" "%TARGET%\KlarWin.exe" >nul
if exist "%SRC%Tools\" xcopy /E /I /Y "%SRC%Tools\*" "%TARGET%\Tools\" >nul
if exist "%SRC%browser-extension\" xcopy /E /I /Y "%SRC%browser-extension\*" "%TARGET%\browser-extension\" >nul
powershell -NoProfile -Command ^
  "$exe = $env:LOCALAPPDATA + '\KlarWin\KlarWin.exe'; $dir = $env:LOCALAPPDATA + '\KlarWin'; $icon = $exe + ',0'; $ws = New-Object -ComObject WScript.Shell; $s = $ws.CreateShortcut([Environment]::GetFolderPath('Desktop') + '\KlarWin.lnk'); $s.TargetPath = $exe; $s.WorkingDirectory = $dir; $s.IconLocation = $icon; $s.Description = 'KlarWin'; $s.Save(); $sm = [Environment]::GetFolderPath('StartMenu') + '\Programs\KlarWin.lnk'; $s2 = $ws.CreateShortcut($sm); $s2.TargetPath = $exe; $s2.WorkingDirectory = $dir; $s2.IconLocation = $icon; $s2.Description = 'KlarWin'; $s2.Save()"
echo Fertig. Desktop- und Startmenue-Verknuepfung angelegt.
start "" "%TARGET%\KlarWin.exe"
endlocal
