@echo off
rem NASA APOD for Windows - build with the .NET Framework compiler.
rem No SDK and no NuGet needed: csc.exe ships with Windows.
setlocal
cd /d "%~dp0"
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe not found at %CSC%
  exit /b 1
)
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ ^
  /out:NasaApod.exe ^
  /win32icon:NasaApod.ico ^
  /reference:System.Windows.Forms.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Net.Http.dll ^
  /reference:System.Web.Extensions.dll ^
  /reference:System.IO.Compression.dll ^
  /reference:System.IO.Compression.FileSystem.dll ^
  NasaApod.cs
if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
echo Built NasaApod.exe
endlocal
