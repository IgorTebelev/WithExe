////////////////////////////////////////////////////////////////////////////////
/// Igor Tebelev
/// (c) Copyright 2026
///
/// UINT WINAPI Run(exe, cmdLine)
///   exe:     FULL on-disk EXE path unless the file sits next to the
///            host EXE (same directory as WithExe.exe / the process
///            image). CreateProcess lpApplicationName. Not PATH.
///   cmdLine: written into PEB CommandLine (Buffer + Length) and
///            kernel32 GetCommandLineA cache (CreateProcess lpCommandLine).
///
/// Maps the EXE and calls the guest EP on this thread. Always rewrites
/// GetModuleFileNameW(NULL) to the guest image. Host may __try / __except
/// around Run. Returns whatever the EP returns (WinMain-style UINT),
/// or the guest's ExitProcess code: VEH returns from the EP call inside
/// Run so Run's epilogue restores host registers. 0 if setup/map failed.
////////////////////////////////////////////////////////////////////////////////

#pragma once

#include <windows.h>

#ifdef __cplusplus
extern "C" {
#endif

UINT WINAPI Run(LPCWSTR exe, LPCWSTR cmdLine);

#ifdef __cplusplus
}
#endif
