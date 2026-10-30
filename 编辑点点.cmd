@echo off
chcp 65001 >nul
start "" "%~dp0outputs\Windows-v1.6.3\点点\点点.exe" --editor --project-root "%~dp0app"
