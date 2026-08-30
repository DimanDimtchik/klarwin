@echo off
setlocal
set "TARGET=%LOCALAPPDATA%\KlarWin"
set "SRC=%~dp0"
echo KlarWin wird nach "%TARGET%" installiert ...
mkdir "%TARGET%" 2>nul
copy /Y "%SRC%KlarWin.exe" "%TARGET%\KlarWin.exe" >nul
powershell -NoProfile -Command ^
  "$ws = New-Object -ComObject WScript.Shell; $s = $ws.CreateShortcut([Environment]::GetFolderPath('Desktop') + '\KlarWin.lnk'); $s.TargetPath = $env:LOCALAPPDATA + '\KlarWin\KlarWin.exe'; $s.WorkingDirectory = $env:LOCALAPPDATA + '\KlarWin'; $s.Description = 'KlarWin'; $s.Save(); $sm = [Environment]::GetFolderPath('StartMenu') + '\Programs\KlarWin.lnk'; $s2 = $ws.CreateShortcut($sm); $s2.TargetPath = $s.TargetPath; $s2.WorkingDirectory = $s.WorkingDirectory; $s2.Save()"
echo Fertig. Desktop- und Startmenue-Verknuepfung angelegt.
start "" "%TARGET%\KlarWin.exe"
endlocal
