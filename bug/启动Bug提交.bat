@echo off
chcp 65001 >nul
title Bug 提交器
echo =============================================
echo   Bug Uploader - 构建并启动...
echo =============================================
echo.
cd /d "%~dp0"

:: 检查 .NET SDK
dotnet --version >nul 2>&1
if %errorlevel% neq 0 (
    echo [错误] 未找到 .NET SDK，请先安装 .NET 8.0 SDK！
    echo 下载地址: https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

:: 先杀掉可能残留的进程（防止文件锁定）
taskkill /F /IM BugUploader.exe >nul 2>&1

echo [1/4] 清理编译缓存...
if exist "obj\" rmdir /s /q "obj\" 2>nul
if exist "bin\" rmdir /s /q "bin\" 2>nul

echo [2/4] 恢复 NuGet 包...
dotnet restore BugUploader.csproj -q

echo [3/4] 编译项目...
dotnet build BugUploader.csproj -c Release --no-restore
if %errorlevel% neq 0 (
    echo [错误] 编译失败！
    pause
    exit /b 1
)

echo [4/4] 启动 Bug 提交器...
echo.
start "" "bin\Release\net8.0-windows\BugUploader.exe"
echo Bug 提交器已启动！
echo.
pause
