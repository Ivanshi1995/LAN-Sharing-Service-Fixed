@echo off
chcp 65001 >nul
title Bug Upload Server
echo =============================================
echo   Bug Upload - 启动本地服务器...
echo =============================================
echo.
cd /d "%~dp0"

:: 检查 Python
python --version >nul 2>&1
if %errorlevel% neq 0 (
    echo [错误] 未找到 Python，请先安装 Python 3！
    echo 下载地址: https://www.python.org/downloads/
    pause
    exit /b 1
)

:: 安装依赖
echo [1/2] 检查依赖...
pip install pymysql cryptography -q 2>nul

:: 启动服务器
echo [2/2] 启动服务器...
echo.
python server.py

pause
