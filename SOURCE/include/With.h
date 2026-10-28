////////////////////////////////////////////////////////////////////////////////
/// Igor Tebelev
/// (c) Copyright 2026
///
/// With.dll — map a Windows EXE and run it inside the caller's process.
///
/// UINT WINAPI RunEx(exe, cmdLine, flags, pEp)
///   exe:     FULL on-disk EXE path unless the file sits next to the host EXE
///            (same directory as the process image). Not PATH-searched.
///   cmdLine: guest command line. Written via GetCommandLineW / GetCommandLineA
///            (and PEB Length) when WITH_PEB_CMDLINE is set. Common case: a
///            substring of the host line (absolute paths), so it always fits.
///            If cmdLine is longer than the original host line, the write still
///            happens and GetLastError() is ERROR_INSUFFICIENT_BUFFER after
///            Run/RunEx returns — a crash is possible. Host pads (cmd/bat/ps)
///            or restarts with a longer line if it needs more room.
///   flags:   WITH_* bits below. Each bit is one piece of policy the host opts
///            into; nothing is forced on the guest beyond mapping + calling EP.
///   pEp:     NULL = call guest EP on this thread.
///            non-NULL = map only; *pEp = guest EP. Does not call it.
///            Return 0 if map/setup failed (*pEp = NULL). Return 1 if mapped.
///            Host invokes *pEp (other thread, __try, ExitThread, ...).
///            WITH_EXIT / WITH_EXIT_THREAD still apply on this thread.
///
/// UINT WINAPI Run(exe, cmdLine)
///   Back-compatible wrapper == RunEx with the six default WITH_* bits set
///   (not WITH_EXIT_THREAD) and pEp NULL.
///
/// With pEp NULL: maps the EXE and calls the guest EP on THIS thread. Host
/// may __try / __except around Run / RunEx. Returns whatever the EP returns,
/// or the guest's ExitProcess code when WITH_EXIT is set. WITH_EXIT_THREAD
/// ends this thread instead; RunEx does not return. 0 if setup/map failed.
///
/// Compile-time (With.cpp, not these flags):
///   WITH_IAT        import APIs by name instead of EAT caps (~1kb larger)
////////////////////////////////////////////////////////////////////////////////

#pragma once

#include <windows.h>

// WITH_* flags for RunEx.
#define WITH_PEB_IMAGE    0x1   // PEB ImageBaseAddress = guest (GetModuleHandle(NULL), FindResource(NULL))
#define WITH_PEB_CMDLINE  0x2   // GetCommandLineW/A + PEB Length = cmdLine (error if longer than original)
#define WITH_PEB_NAME     0x4   // loader-list name of the host EXE entry = guest (unlocked, sequential map)
#define WITH_EXIT         0x8   // guest ExitProcess returns from RunEx with its code
#define WITH_DLLDIR       0x10  // SetDllDirectoryW to the guest EXE folder (no CD)
#define WITH_SYSTEM_LOADLIBRARY 0x20  // default ON: system LoadLibrary binds IAT+TLS. Off: With.dll snaps IAT+TLS
#define WITH_EXIT_THREAD  0x40  // guest ExitProcess ends this thread (RunEx does not return). Wins over WITH_EXIT.

#ifdef __cplusplus
extern "C" {
#endif

#ifdef __cplusplus
UINT WINAPI RunEx(LPCWSTR exe, LPCWSTR cmdLine, DWORD flags, FARPROC* pEp = NULL);
#else
UINT WINAPI RunEx(LPCWSTR exe, LPCWSTR cmdLine, DWORD flags, FARPROC* pEp);
#endif
UINT WINAPI Run(LPCWSTR exe, LPCWSTR cmdLine);

#ifdef __cplusplus
}
#endif
