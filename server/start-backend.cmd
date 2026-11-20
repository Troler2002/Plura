@echo off
rem Arranca el backend de Plura y lo mantiene vivo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-backend.ps1" %*
