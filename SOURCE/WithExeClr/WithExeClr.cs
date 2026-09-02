//////////////////////////////////////////////////////////////////////////////
//
//  WithExeClr.cs — sample HOST for With.dll (C# / CLR)
//
//  WHAT THIS PROGRAM IS
//  --------------------
//  A console EXE that loads With.dll and asks it to run another EXE
//  *inside this same process* (not CreateProcess). That other EXE is the
//  "guest". You are the "host".
//
//  Same job as WithExe.cpp. This file is the CLR spelling of that
//  template. Detours' withdll does the opposite: it CreateProcess's the
//  target suspended and injects a DLL into that new process. We do not
//  inject into anyone. We map their EXE into US via With.dll's Run().
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
//      WithExeClr.exe [options] [helper.dll ...] guest.exe [guest args...]
//
//  Helpers (any number of .dll) are LoadLibrary'd first so YOU can install
//  detours AND/OR other instrumentation before the guest starts. The first
//  .exe is the guest. Everything after that is that guest's command line.
//
//  PATHS — FULL / ABSOLUTE unless the file is in THIS EXE's directory.
//  Bare "helper.dll" / "game.exe" only work if that file sits next to
//  WithExeClr.exe. Anywhere else pass C:\...\file.dll / C:\...\game.exe.
//  We do not search PATH. Quotes around a path with spaces are fine.
//
//  .dll vs .exe is decided by FILE EXTENSION only (Path.GetExtension).
//  We do not open the file or read PE headers. "whoami" with no .exe
//  is rejected.
//
//  CLR vs WithExe.cpp
//  ------------------
//  C# Main(string[] args) does NOT include the host EXE as args[0].
//  wmain's argv[0] is WithExe.exe. Guest-cmdline skip is therefore
//  (arg + 1) PathGetArgsW steps: +1 for this EXE, then the same tokens
//  wmain would skip.
//  C# has no __try around native code. SEH is a native-host choice
//  (a GUI app can run a secondary message pump). Here a guest AV
//  kills the process.
//
//  Copyright (c) 2026 Igor Tebelev / TIG. All Rights Reserved.
//
using System.Runtime.InteropServices;

internal static class Program
{
    const string csWithDll = "With.dll";
    const string csWithExe = "WithExeClr.exe";

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    delegate uint PfnRun(string exe, string cmdLine);

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibraryW(string path);

    [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)]
    static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32")]
    static extern uint GetLastError();

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    static extern uint GetModuleFileNameW(IntPtr hModule, [Out] char[] buf, uint size);

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    static extern IntPtr GetCommandLineW();

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    static extern bool SetDllDirectoryW(string lpPathName);

    // Returns a pointer INTO pszPath (the process command line). Do not
    // pass a C# string: the marshaler would copy, and the pointer would die.
    [DllImport("shlwapi", CharSet = CharSet.Unicode)]
    static extern IntPtr PathGetArgsW(IntPtr pszPath);

    static string FmtPtr(IntPtr p) => $"0x{unchecked((ulong)p.ToInt64()):X}";

    //////////////////////////////////////////////////////////////////////////
    // PrintUsage — /? and bad args. -d is host-only.
    //
    static void PrintUsage()
    {
        Console.Write(
            "Usage:\n" +
            $"    {csWithExe} [options] [dlls...] exe [args...]\n" +
            "    dlls / exe : FULL path unless the file is next to this EXE.\n" +
            "Options:\n" +
            "    -d       : set DLL directory and cwd to the guest folder.\n" +
            "    /?       : This help screen.\n" +
            "(c) TIG 2026 All Rights Reserved\n");
    }

    //////////////////////////////////////////////////////////////////////////
    // ExtIs — TRUE if path's extension is ext (".dll" / ".exe"),
    // case-insensitive. Options (-d, /?) are not files: leading '-' or
    // '/' => FALSE. Path.GetExtension returns e.g. ".exe", or "" if none.
    //
    static bool ExtIs(string? path, string ext)
    {
        if (string.IsNullOrEmpty(path) || path[0] == '-' || path[0] == '/')
            return false;
        return string.Equals(Path.GetExtension(path), ext,
            StringComparison.OrdinalIgnoreCase);
    }

    //////////////////////////////////////////////////////////////////////////
    // ParseOptions — one argv token like "-d" or "/d".
    // Letters after the dash are a CLUSTER (Detours withdll style).
    // Help calls this block [options].
    //
    //   d  set dllDir                      (THIS host will SetDllDirectory to the guest folder)
    //
    static bool ParseOptions(string token, ref bool dllDir)
    {
        if ((token[0] != '-' && token[0] != '/') || token.Length < 2)
            return false;
        for (int i = 1; i < token.Length; i++)
        {
            char x = char.ToLowerInvariant(token[i]);
            if (x == 'd')
                dllDir = true;
            else
                return false;
        }
        return true;
    }

    //////////////////////////////////////////////////////////////////////////
    // SetGuestDllDir — only if the user passed -d.
    //
    // CreateProcess puts the EXE's directory on the DLL search path and as cwd.
    // In-process we do NOT get that for free: this process started as
    // WithExeClr.exe. So -d takes the guest path, strips the filename
    // (Path.GetDirectoryName), then SetDllDirectoryW + SetCurrentDirectory
    // to that folder.
    // Bare "game.exe" with no path: nothing to strip, we return and leave cwd alone.
    //
    static void SetGuestDllDir(string exe)
    {
        string? dir = Path.GetDirectoryName(exe);
        if (string.IsNullOrEmpty(dir))
            return;
        SetDllDirectoryW(dir);
        Directory.SetCurrentDirectory(dir);
        Console.WriteLine($"[{csWithExe}] DLL directory and cwd: {dir}");
    }

    //////////////////////////////////////////////////////////////////////////
    // LoadWithDll — With.dll must sit next to THIS host EXE.
    // GetModuleFileNameW(NULL) = full path of WithExeClr.exe. Strip the
    // filename, append "With.dll", LoadLibraryW. Then GetProcAddress("Run").
    // LoadLibrary of an already-loaded module just returns that HMODULE.
    //
    static PfnRun? LoadWithDll()
    {
        char[] buf = new char[260];
        uint n = GetModuleFileNameW(IntPtr.Zero, buf, (uint)buf.Length);
        string path = n > 0 ? new string(buf, 0, (int)n) : "";
        int slash = path.LastIndexOfAny(new[] { '\\', '/' });
        string dir = slash >= 0 ? path.Substring(0, slash + 1) : "";
        string dll = dir + csWithDll;

        IntPtr hDll = LoadLibraryW(dll);
        if (hDll == IntPtr.Zero)
        {
            Console.WriteLine($"[{csWithExe}] LoadLibrary({csWithDll}) failed: {GetLastError()}");
            return null;
        }
        Console.WriteLine($"[{csWithExe}] {csWithDll} {FmtPtr(hDll)}");
        IntPtr pfn = GetProcAddress(hDll, "Run");
        if (pfn == IntPtr.Zero)
        {
            Console.WriteLine($"[{csWithExe}] GetProcAddress(Run) failed.");
            return null;
        }
        return Marshal.GetDelegateForFunctionPointer<PfnRun>(pfn);
    }

    //////////////////////////////////////////////////////////////////////////
    // Main
    //
    //  1. Optional ONE [options] token at args[0] (-d, /?).
    //     (C# args[0] is the first user token, NOT this EXE.)
    //  2. Load With.dll, get Run.
    //  3. Remaining args: .dll helpers (LoadLibrary), then ONE .exe (the guest).
    //  4. Guest command line = pointer into GetCommandLineW() at that .exe.
    //     We do NOT rebuild the line. PathGetArgsW skips one token each time
    //     (quotes stay as the shell wrote them). Skip (arg + 1) tokens =
    //     skip WithExeClr.exe, the options token, and every helper DLL.
    //
    //     Host line:
    //       WithExeClr.exe -d helper.dll "C:\My App\game.exe" --level 3
    //                                    ^
    //                                    cmd points here (guest's CreateProcess-style line)
    //
    //     If we passed the WHOLE GetCommandLineW(), the guest would see
    //     WithExeClr.exe as its command line. Wrong.
    //
    //  5. Optional -d: DLL dir / cwd = guest folder.
    //  6. Run(exe, cmd).
    //     Run MAPS the guest and CALLS its entry point on THIS thread.
    //     Guest ExitProcess is turned into a return from Run (VEH restores
    //     RIP/RSP saved at Run entry).
    //
    static int Main(string[] args)
    {
        bool fNeedHelp = false;
        bool fDllDir = false;   // host-only; -d sets this
        int arg = 0;            // args[0] is first option/dll/exe, not this EXE

        if (arg < args.Length && (args[arg][0] == '-' || args[arg][0] == '/'))
        {
            if (args[arg].Length == 2 && args[arg][1] == '?')
                fNeedHelp = true;
            else if (!ParseOptions(args[arg], ref fDllDir))
            {
                Console.WriteLine($"[{csWithExe}] Bad argument: {args[arg]}");
                fNeedHelp = true;
            }
            else
                arg++;          // consumed the cluster; next token is dll/exe
        }

        if (arg >= args.Length)
            fNeedHelp = true;

        if (fNeedHelp)
        {
            PrintUsage();
            return 9001;
        }

        PfnRun? pfnRun = LoadWithDll();
        if (pfnRun == null)
            return 9003;

        string? exe = null;     // guest path (from args, quotes already stripped)
        string cmd = "";        // guest cmdline (pointer into the original string)

        while (arg < args.Length)
        {
            if (ExtIs(args[arg], ".dll"))
            {
                Console.WriteLine($"[{csWithExe}] with `{args[arg]}'");
                if (LoadLibraryW(args[arg]) == IntPtr.Zero)
                {
                    Console.WriteLine(
                        $"[{csWithExe}] Error: {args[arg]} failed to load (error {GetLastError()}).");
                    return 9003;
                }
                arg++;
                continue;
            }
            if (!ExtIs(args[arg], ".exe"))
            {
                Console.WriteLine($"[{csWithExe}] Error: {args[arg]} is not an EXE.");
                return 9002;
            }
            exe = args[arg];
            // +1 = this host EXE, which C# omitted from args[].
            IntPtr p = GetCommandLineW();
            for (int i = 0; i < arg + 1; i++)
                p = PathGetArgsW(p);
            cmd = Marshal.PtrToStringUni(p) ?? "";
            break;              // one guest only; rest of argv is inside cmd
        }

        if (exe == null)
        {
            Console.WriteLine($"[{csWithExe}] Error: no EXE.");
            return 9001;
        }

        Console.WriteLine($"[{csWithExe}] Starting in-proc: {exe}");
        Console.WriteLine($"[{csWithExe}] Command Line:{cmd}");
        Console.Out.Flush();

        if (fDllDir)
            SetGuestDllDir(exe);

        // Maps the EXE, Runs it by calling its EP,
        // returns result by Intercepting original ExitProcess (0 if map/setup failed).
        uint code = pfnRun(exe, cmd);
        Console.WriteLine($"[{csWithExe}] Returned from run with code: {(int)code}");
        Console.WriteLine($"[{csWithExe}] Exiting");
        return (int)code;
    }
}
//
///////////////////////////////////////////////////////////////// End of File.
