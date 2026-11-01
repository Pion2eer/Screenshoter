@echo off
chcp 65001 >nul
rem Сборка без установки чего-либо: используется компилятор C#, встроенный в Windows (.NET Framework 4.x)
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /codepage:65001 /optimize+ /out:Screenshoter.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /win32icon:src\app.ico /resource:src\app.ico,Screenshoter.app.ico src\*.cs
if errorlevel 1 (echo Ошибка сборки) else (echo Готово: Screenshoter.exe)
pause
