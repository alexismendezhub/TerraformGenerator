@echo off
REM ============================================================
REM  Terraform Generator - builds and runs ALL tests on Windows
REM  Requirements: .NET SDK 10, Node.js 22.12+ / 24, PowerShell 7 (pwsh); optional: Terraform, Bicep
REM  Usage: scripts\build-and-test.cmd   (from the repo root)
REM ============================================================
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-and-test.ps1" || exit /b 1
