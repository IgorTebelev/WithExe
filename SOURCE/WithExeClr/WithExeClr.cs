//////////////////////////////////////////////////////////////////////////////
//
//  WithExeClr.cs — sample HOST for With.dll (C# / CLR)
//
//  Same job as WithExe.cpp. This file is the CLR spelling of that
//  template. RunEx maps the guest EXE into this process.
//
//  COMMAND LINE SHAPE
//      WithExeClr.exe [options] [helper.dll ...] guest.exe [guest args...]
//
//  C# Main(string[] args) does NOT include the host EXE as args[0].
//  Guest-cmdline skip is (arg + 1) PathGetArgsW steps.
//  C# has no __try around native code. A guest AV kills the process.
//
//  Copyright (c) 2026 Igor Tebelev / TIG. All Rights Reserved.
//
using System.Runtime.InteropServices;

internal static class Program
{
    const string csWithDll = "With.dll";
    const string csWithExe = "WithExeClr.exe";

    const uint WITH_PEB_IMAGE = 0x1;
    const uint WITH_PEB_CMDLINE = 0x2;
    const uint WITH_PEB_NAME = 0x4;
    const uint WITH_EXIT = 0x8;
    const uint WITH_DLLDIR = 0x10;
    const uint WITH_SYSTEM_LOADLIBRARY = 0x20;
    const uint WITH_EXIT_THREAD = 0x40;
    const uint WITH_ALL = WITH_PEB_IMAGE | WITH_PEB_CMDLINE | WITH_PEB_NAME | WITH_EXIT | WITH_DLLDIR | WITH_SYSTEM_LOADLIBRARY;
    const int ERROR_INSUFFICIENT_BUFFER = 122;

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    delegate uint PfnRunEx(string exe, string cmdLine, uint flags, IntPtr pEp);

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

    [DllImport("shlwapi", CharSet = CharSet.Unicode)]
    static extern IntPtr PathGetArgsW(IntPtr pszPath);

    static string FmtPtr(IntPtr p) => $"0x{unchecked((ulong)p.ToInt64()):X}";

    static void PrintUsage()
    {
        Console.Write(
            "Usage:\n" +
            $"    {csWithExe} [options] [dlls...] exe [args...]\n" +
            "    dlls / exe : FULL path unless the file is next to this EXE.\n" +
            "Options (one cluster, e.g. -e or -icf):\n" +
            "    Default ON: PEB image, command line, loader name, ExitProcess trap,\n" +
            "    SetDllDirectory to the guest folder, and system LoadLibrary (IAT/TLS).\n" +
            "    i/c/f/e/s turn that bit OFF. -d is ON (default is already ON).\n" +
            "    -t turns ON ExitThread (default OFF).\n" +
            "    -d       : ON  SetDllDirectory to the guest folder (default ON).\n" +
            "    -i       : OFF PEB ImageBaseAddress = guest.\n" +
            "    -c       : OFF GetCommandLineW/A write.\n" +
            "    -f       : OFF loader-list EXE name = guest.\n" +
            "    -e       : OFF ExitProcess returns from RunEx (guest ExitProcess kills us).\n" +
            "    -s       : OFF system LoadLibrary; With.dll snaps IAT+TLS.\n" +
            "    -t       : ON  guest ExitProcess ends this thread (RunEx does not return).\n" +
            "    /?       : This help screen.\n" +
            "(c) TIG 2026 All Rights Reserved\n");
    }

    static bool ExtIs(string? path, string ext)
    {
        if (string.IsNullOrEmpty(path) || path[0] == '-' || path[0] == '/')
            return false;
        return string.Equals(Path.GetExtension(path), ext,
            StringComparison.OrdinalIgnoreCase);
    }

    static bool ParseOptions(string token, ref uint flags)
    {
        if ((token[0] != '-' && token[0] != '/') || token.Length < 2)
            return false;
        for (int i = 1; i < token.Length; i++)
        {
            char x = char.ToLowerInvariant(token[i]);
            if (x == 'd')
                flags |= WITH_DLLDIR;
            else if (x == 'i')
                flags &= ~WITH_PEB_IMAGE;
            else if (x == 'c')
                flags &= ~WITH_PEB_CMDLINE;
            else if (x == 'f')
                flags &= ~WITH_PEB_NAME;
            else if (x == 'e')
                flags &= ~WITH_EXIT;
            else if (x == 's')
                flags &= ~WITH_SYSTEM_LOADLIBRARY;
            else if (x == 't')
                flags |= WITH_EXIT_THREAD;
            else
                return false;
        }
        return true;
    }

    static PfnRunEx? LoadWithDll()
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
        IntPtr pfn = GetProcAddress(hDll, "RunEx");
        if (pfn == IntPtr.Zero)
        {
            Console.WriteLine($"[{csWithExe}] GetProcAddress(RunEx) failed.");
            return null;
        }
        return Marshal.GetDelegateForFunctionPointer<PfnRunEx>(pfn);
    }

    static int Main(string[] args)
    {
        bool fNeedHelp = false;
        uint flags = WITH_ALL;
        int arg = 0;

        if (arg < args.Length && (args[arg][0] == '-' || args[arg][0] == '/'))
        {
            if (args[arg].Length == 2 && args[arg][1] == '?')
                fNeedHelp = true;
            else if (!ParseOptions(args[arg], ref flags))
            {
                Console.WriteLine($"[{csWithExe}] Bad argument: {args[arg]}");
                fNeedHelp = true;
            }
            else
                arg++;
        }

        if (arg >= args.Length)
            fNeedHelp = true;

        if (fNeedHelp)
        {
            PrintUsage();
            return 9001;
        }

        PfnRunEx? pfnRun = LoadWithDll();
        if (pfnRun == null)
            return 9003;

        string? exe = null;
        string cmd = "";

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
            IntPtr p = GetCommandLineW();
            for (int i = 0; i < arg + 1; i++)
                p = PathGetArgsW(p);
            cmd = Marshal.PtrToStringUni(p) ?? "";
            break;
        }

        if (exe == null)
        {
            Console.WriteLine($"[{csWithExe}] Error: no EXE.");
            return 9001;
        }

        Console.WriteLine($"[{csWithExe}] Starting: {exe}");
        Console.WriteLine($"[{csWithExe}] Command Line:{cmd}");
        Console.WriteLine($"[{csWithExe}] flags=0x{flags:X}");
        Console.Out.Flush();

        uint code = pfnRun(exe, cmd, flags, IntPtr.Zero);
        if (GetLastError() == ERROR_INSUFFICIENT_BUFFER)
            Console.WriteLine($"[{csWithExe}] Command line longer than host line — crash possible.");
        Console.WriteLine($"[{csWithExe}] RunEx returned {code}");
        return (int)code;
    }
}
//
///////////////////////////////////////////////////////////////// End of File.
