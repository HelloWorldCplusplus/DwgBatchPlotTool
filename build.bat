@echo off
rem DwgBatchPlotTool 构建脚本 - 作者: 唐明龙 (QQ 979123550)
rem 本文件是 UTF-8 编码, 先切到 65001 否则 cmd 默认 936 下中文全是乱码
chcp 65001 >nul 2>&1
setlocal enabledelayedexpansion
cd /d "%~dp0"

set "DOTNET=dotnet"
where dotnet >nul 2>nul
if errorlevel 1 (
    if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
    if exist "%ProgramFiles(x86)%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles(x86)%\dotnet\dotnet.exe"
)
where "%DOTNET%" >nul 2>nul
if errorlevel 1 (
    echo [错误] 找不到 dotnet SDK.
    echo        请安装 .NET SDK 8/9, 或用 Visual Studio 2022 打开 DwgBatchPlotTool.sln 构建.
    exit /b 1
)

echo [1/3] 构建 ODA 转换引擎 ...
"%DOTNET%" build "src\ODADrawingTools\ODADrawingTools.csproj" -c Release --nologo -v q
if errorlevel 1 goto :fail

echo [2/3] 构建图形界面 ...
"%DOTNET%" build "src\DwgBatchPlotTool\DwgBatchPlotTool.csproj" -c Release --nologo -v q
if errorlevel 1 goto :fail

if exist "tests\Selftest\Selftest.csproj" (
  echo [3/3] 构建自检程序 ...
  "%DOTNET%" build "tests\Selftest\Selftest.csproj" -c Release --nologo -v q
  if errorlevel 1 goto :fail
) else (
  echo [3/3] 跳过自检程序 ^(tests\Selftest 不在仓库里^) ...
)

echo.
echo 构建完成.
echo   主程序: %~dp0bin\DwgBatchPlotTool.exe
echo   自检:   %~dp0tests\Selftest\bin\Selftest.exe
exit /b 0

:fail
echo.
echo [错误] 构建失败.
exit /b 1
