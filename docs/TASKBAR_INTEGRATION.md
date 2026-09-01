# Taskbar Integration

## Purpose
This document outlines the native Windows 11 taskbar docking and injection mechanism for FootballTray.

## Technical Approach
- Native HWND / Win32 window positioning and hierarchy management (inspired by the proven Awqat-Salaat approach).
- Interacting with `Shell_TrayWnd` and taskbar tray layout notifications without third-party frameworks like TaskbarWidgets.
- Handling Windows display scaling (DPI changes), taskbar auto-hide, multi-monitor setups, and Windows 11 taskbar alignment modes (center vs. left).
