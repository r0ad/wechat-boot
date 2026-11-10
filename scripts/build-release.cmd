@echo off
rem 发布单文件 exe 到 dist\WeChatBoot.exe（需已安装 .NET 9 桌面运行时）
setlocal
cd /d "%~dp0.."
dotnet publish src\WeChatBoot.App\WeChatBoot.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
if errorlevel 1 (
  echo 发布失败
  exit /b 1
)
echo.
echo 产物：%cd%\dist\WeChatBoot.exe
