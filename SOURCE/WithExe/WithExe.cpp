//////////////////////////////////////////////////////////////////////////////
//
//  WithExe.cpp — sample HOST for With.dll
//
//  WHAT THIS PROGRAM IS
//  --------------------
//  A console EXE that loads With.dll and asks it to run another EXE
//  *inside this same process* (not CreateProcess). That other EXE is the
//  "guest". You are the "host".
//
//  Detours' withdll does the opposite: it CreateProcess's the target
//  suspended and injects a DLL into that new process. We do not inject
//  into anyone. We map their EXE into US via With.dll's Run().
//
//  THIS IS A TEMPLATE
//  ------------------
//  Demonstrates usage. Not a product. Copy, modify, extend.
//  The sample runs ONE guest on THIS thread. That is not a limit.
//  You can change this host to:
//    - multithread (Run on other threads)
//    - load multiple targets
//    - execute guests sequentially (call Run again after one returns)
//    - intercept system calls by some other methods
//
//  COMMAND LINE SHAPE
//  ------------------
//      WithExe.exe [options] [helper.dll ...] guest.exe [guest args...]
//
//  Helpers (any number of .dll) are LoadLibrary'd first so YOU can install
//  detours AND/OR other instrumentation before the guest starts. The first
//  .exe is the guest. Everything after that is that guest's command line.
//
//  PATHS — FULL / ABSOLUTE unless the file is in THIS EXE's directory.
//  Bare "helper.dll" / "game.exe" only work if that file sits next to
//  WithExe.exe. Anywhere else pass C:\...\file.dll / C:\...\game.exe.
//  We do not search PATH. Quotes around a path with spaces are fine.
//
//  .dll vs .exe is decided by FILE EXTENSION only (shlwapi). We do not
//  open the file or read PE headers. "whoami" with no .exe is rejected.
//
//  Copyright (c) 2026 Igor Tebelev / TIG. All Rights Reserved.
//
#include <stdio.h>
#include <windows.h>
#include <shlwapi.h>            // PathFindExtensionW, PathGetArgsW, PathAppendW, ...
#include "With.h"

#pragma comment(lib, "shlwapi.lib")

// RunEx() lives in With.dll. Same signature as With.h.
typedef UINT (WINAPI* PFN_RUNEX)(LPCWSTR, LPCWSTR, DWORD, FARPROC*);

static const WCHAR g_csWithDll[] = L"With.dll";
static const WCHAR g_csWithExe[] = L"WithExe.exe";

//////////////////////////////////////////////////////////////////////////////
// PrintUsage — /? and bad args. Letters after the dash are a CLUSTER.
// i/c/f/e/s turn OFF a RunEx bit; default is the six Run() bits ON.
// -t turns ON WITH_EXIT_THREAD (default OFF).
//
void PrintUsage(void)
{
    printf("Usage:\n"
           "    %S [options] [dlls...] exe [args...]\n"
           "    dlls / exe : FULL path unless the file is next to this EXE.\n"
           "Options (one cluster, e.g. -e or -icf):\n"
           "    Default ON: PEB image, command line, loader name, ExitProcess trap,\n"
           "    SetDllDirectory to the guest folder, and system LoadLibrary (IAT/TLS).\n"
           "    i/c/f/e/s turn that bit OFF. -d is ON (default is already ON).\n"
           "    -t turns ON ExitThread (default OFF).\n"
           "    -d       : ON  SetDllDirectory to the guest folder (default ON).\n"
           "    -i       : OFF PEB ImageBaseAddress = guest.\n"
           "    -c       : OFF GetCommandLineW/A write.\n"
           "    -f       : OFF loader-list EXE name = guest.\n"
           "    -e       : OFF ExitProcess returns from RunEx (guest ExitProcess kills us).\n"
           "    -s       : OFF system LoadLibrary; With.dll snaps IAT+TLS.\n"
           "    -t       : ON  guest ExitProcess ends this thread (RunEx does not return).\n"
           "    /?       : This help screen.\n"
           "(c) TIG 2026 All Rights Reserved\n",
           g_csWithExe);
}

//////////////////////////////////////////////////////////////////////////////
// ExtIs — TRUE if path's extension is ext (".dll" / ".exe"), case-insensitive.
// Options (-d, -icfe, /?) are not files: leading '-' or '/' => FALSE.
// PathFindExtensionW returns a pointer into path, e.g. ".exe", or "\0" if none.
//
static BOOL ExtIs(LPCWSTR path, LPCWSTR ext)
{
    LPCWSTR e;
    if (!path || !*path || *path == L'-' || *path == L'/')
        return FALSE;
    e = PathFindExtensionW(path);
    return e && *e && StrCmpIW(e, ext) == 0;
}

//////////////////////////////////////////////////////////////////////////////
// ParseOptions — one argv token like "-de" or "/icf".
// Letters after the dash are a CLUSTER (Detours withdll style).
//
//   d  set DLLDIR       SetDllDirectoryW to the guest folder (default already ON)
//   i  clear IMAGE      no PEB ImageBaseAddress = guest
//   c  clear CMDLINE    no GetCommandLineW/A write
//   f  clear NAME       no loader-list EXE name = guest
//   e  clear EXIT       no return-from-RunEx on ExitProcess
//   s  clear SYSTEM_LOADLIBRARY  With.dll snaps IAT+TLS instead of the system loader
//   t  set   EXIT_THREAD guest ExitProcess ends this thread (wins over EXIT)
//
// Unknown letter => FALSE (caller prints help). Does not consume argv; wmain does.
//
static BOOL ParseOptions(LPCWSTR pToken, DWORD* pFlags)
{
    if ((pToken[0] != L'-' && pToken[0] != L'/') || pToken[1] == L'\0')
        return FALSE;
    for (int i = 1; pToken[i]; i++)
    {
        WCHAR tmp[2] = { pToken[i], 0 };
        CharLowerW(tmp);
        if (tmp[0] == L'd')
            *pFlags |= WITH_DLLDIR;
        else if (tmp[0] == L'i')
            *pFlags &= ~WITH_PEB_IMAGE;
        else if (tmp[0] == L'c')
            *pFlags &= ~WITH_PEB_CMDLINE;
        else if (tmp[0] == L'f')
            *pFlags &= ~WITH_PEB_NAME;
        else if (tmp[0] == L'e')
            *pFlags &= ~WITH_EXIT;
        else if (tmp[0] == L's')
            *pFlags &= ~WITH_SYSTEM_LOADLIBRARY;
        else if (tmp[0] == L't')
            *pFlags |= WITH_EXIT_THREAD;
        else
            return FALSE;
    }
    return TRUE;
}

//////////////////////////////////////////////////////////////////////////////
// LoadWithDll — With.dll must sit next to THIS host EXE.
// GetModuleFileNameW(NULL) = full path of WithExe.exe. Strip the filename,
// append "With.dll", LoadLibraryW. Then GetProcAddress("RunEx").
// LoadLibrary of an already-loaded module just returns that HMODULE.
//
static PFN_RUNEX LoadWithDll(void)
{
    WCHAR path[MAX_PATH];
    HMODULE hDll;
    PFN_RUNEX pfn;

    GetModuleFileNameW(NULL, path, MAX_PATH);
    PathRemoveFileSpecW(path);
    PathAppendW(path, g_csWithDll);

    hDll = LoadLibraryW(path);
    if (!hDll)
    {
        printf("[%S] LoadLibrary(%S) failed: %ld\n", g_csWithExe, g_csWithDll, GetLastError());
        return NULL;
    }
    printf("[%S] %S %p\n", g_csWithExe, g_csWithDll, hDll);
    pfn = (PFN_RUNEX)GetProcAddress(hDll, "RunEx");
    if (!pfn)
        printf("[%S] GetProcAddress(RunEx) failed.\n", g_csWithExe);
    return pfn;
}

//////////////////////////////////////////////////////////////////////////////
// wmain
//
//  1. Optional ONE [options] token at argv[1] (-d, -icfe, /?).
//  2. Load With.dll, get RunEx.
//  3. Remaining args: .dll helpers (LoadLibrary), then ONE .exe (the guest).
//  4. Guest command line = pointer into GetCommandLineW() at that .exe.
//     We do NOT rebuild the line. PathGetArgsW skips one token each time
//     (quotes stay as the shell wrote them). Skip `arg` tokens = skip
//     WithExe.exe, the options token, and every helper DLL.
//
//     Host line:
//       WithExe.exe -de helper.dll "C:\My App\game.exe" --level 3
//                                 ^
//                                 cmd points here (guest's CreateProcess-style line)
//
//     If we passed the WHOLE GetCommandLineW(), the guest would see
//     WithExe.exe as its command line. Wrong.
//
//  5. __try { RunEx(exe, cmd, flags) }. The host decides how to process SEH.
//     A GUI host can run a secondary message pump from the filter / __except.
//     This sample just reports the exception. With.dll keeps unwind for Run
//     so x64 can walk: guest -> Run -> here. Guest ExitProcess is a return
//     from Run (VEH), not an exception.
//
int CDECL wmain(int argc, WCHAR** argv)
{
    BOOLEAN fNeedHelp = FALSE;
    DWORD flags = WITH_PEB_IMAGE | WITH_PEB_CMDLINE | WITH_PEB_NAME | WITH_EXIT | WITH_DLLDIR | WITH_SYSTEM_LOADLIBRARY;
    int arg = 1;                // argv[0] is WithExe.exe; we parse from 1

    if (arg < argc && (argv[arg][0] == L'-' || argv[arg][0] == L'/'))
    {
        if (argv[arg][1] == L'?' && argv[arg][2] == 0)
            fNeedHelp = TRUE;
        else if (!ParseOptions(argv[arg], &flags))
        {
            printf("[%S] Bad argument: %S\n", g_csWithExe, argv[arg]);
            fNeedHelp = TRUE;
        }
        else
            arg++;              // consumed the cluster; next token is dll/exe
    }

    if (arg >= argc)
        fNeedHelp = TRUE;

    if (fNeedHelp)
    {
        PrintUsage();
        return 9001;
    }

    PFN_RUNEX pfnRun = LoadWithDll();
    if (!pfnRun)
        return 9003;

    WCHAR* exe = NULL;          // guest path (from argv, quotes already stripped)
    LPCWSTR cmd = NULL;         // guest cmdline (pointer into the original string)

    while (arg < argc)
    {
        if (ExtIs(argv[arg], L".dll"))
        {
            printf("[%S] with `%S'\n", g_csWithExe, argv[arg]);
            if (!LoadLibraryW(argv[arg]))
            {
                printf("[%S] Error: %S failed to load (error %ld).\n",
                    g_csWithExe, argv[arg], GetLastError());
                return 9003;
            }
            arg++;
            continue;
        }
        if (!ExtIs(argv[arg], L".exe"))
        {
            printf("[%S] Error: %S is not an EXE.\n", g_csWithExe, argv[arg]);
            return 9002;
        }
        exe = argv[arg];
        cmd = GetCommandLineW();
        for (int i = 0; i < arg; i++)
            cmd = PathGetArgsW(cmd);
        break;                  // one guest only; rest of argv is inside cmd
    }

    if (!exe)
    {
        printf("[%S] Error: no EXE.\n", g_csWithExe);
        return 9001;
    }

    printf("[%S] Starting: %S\n[%S] Command Line:%S\n[%S] flags=0x%X\n",
        g_csWithExe, exe, g_csWithExe, cmd, g_csWithExe, flags);
    fflush(stdout);

    UINT code = 0;
    // Host decides how to process SEH. For a GUI app a secondary message
    // pump is possible; this sample just prints / beeps.
    __try
    {
        // Maps the EXE, runs it by calling its EP.
        // WITH_EXIT: guest ExitProcess returns here (0 if map/setup failed).
        code = pfnRun(exe, cmd, flags, NULL);
        if (GetLastError() == ERROR_INSUFFICIENT_BUFFER)
            printf("[%S] Command line longer than host line — crash possible.\n", g_csWithExe);
        printf("[%S] RunEx returned %u\n", g_csWithExe, code);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        printf("[%S] EXCEPTION CAUGHT!!!\n", g_csWithExe);
        printf("[%S] Run exception 0x%08lX\n", g_csWithExe, GetExceptionCode());
        fflush(stdout);
        Beep(1000, 400);
        code = GetExceptionCode();
    }
    ExitProcess(code);
}
//
///////////////////////////////////////////////////////////////// End of File.
