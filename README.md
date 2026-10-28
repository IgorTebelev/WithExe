# 🏗️ WithExe

## 📜 Preface & Motivation

In-process EXE host for Windows x64. **Not another PE mapper.** Official name **WithExe** — the Detours counterpart of **withdll**: that sample starts a **new** process and pulls a DLL into it; WithExe maps an **EXE** into yours.

`With.dll` loads the target with the OS `LoadLibrary` path and a few tricks so a **Windows EXE** can be mapped and run inside the caller. Imports, relocs, manifests, and `SEC_IMAGE` are the loader’s job when `WITH_SYSTEM_LOADLIBRARY` is on (the default).

This is an **instrumentation POC**, in the same neighborhood as Detours. The two sample hosts follow the Detours `samples/withdll` skeleton (parse options, load helper DLLs, start) — `Run` / `RunEx` instead of `DetourCreateProcessWithDlls`.

---

## ⚠️ Notice & Disclaimer

> **Binary mapper, tiny on purpose:**
> `With.dll` is shipped **in binary form** — a **tiny 3584-byte (3.5 KB)** DLL (`SOURCE\bin\x64\With.dll`, copied next to the hosts under `REDISTRIBUTABLES\BIN\x64`). There is no mapper source in this tree. Drop it in IDA if you are a specialist — 3.5 KB is not a secret. If you need customization, look **below**: change a sample host, or `LoadLibrary` a helper DLL before the guest. `SOURCE\include\With.h` is the API; `SOURCE\lib\x64` has the import library if you link your own host. Rebuild the hosts if you want; do not rebuild the mapper from here.

> **NO WARRANTY:** This is a proof of concept. Unsupported use of the Windows loader. Provided **"AS IS"** with no warranty. Extra behavior belongs in your host or a helper DLL, not in `With.dll`. Use, modify, and experiment at your own risk.

---

## 🔍 Overview

The sample hosts load **one EXE**. Helper DLLs come first (`LoadLibrary`); the first EXE is the guest; everything after that is that guest’s command line.

Default mapping leaves the guest IAT to the system loader. `-s` (`WITH_SYSTEM_LOADLIBRARY` off) makes `With.dll` snap IAT + TLS itself after the map.

There is **no patch-to-load / patch-to-run** on the guest or on system exports. The redirections `With.dll` needs (`NtMapViewOfSection` while the EXE is mapped so the loader will bind it, `ExitProcess` so the guest cannot kill the host) use this thread’s **DR0** and a vectored handler. The targeted EXE’s import table and `kernel32`/`ntdll` bytes stay as the loader left them (unless `-s`, which writes IAT slots in the mapped image only).

### Layout

```text
SOURCE\
  WithExe\          native host
  WithExeClr\      CLR host
  include\With.h   API
  lib\x64\Release|Debug      With.lib
  bin\x64\With.dll          one mapper (Debug and Release hosts copy this same file)
REDISTRIBUTABLES\BIN\x64\Release|Debug
  With.dll         prebuilt mapper (do not rebuild from this tree)
  WithExe.exe
  WithExeClr.exe   (+ .dll / .deps.json / .runtimeconfig.json)
WithExe.slnx
```

Hosts build into `REDISTRIBUTABLES\BIN\x64\Release` and `Debug`. `With.dll` is already there; a host rebuild must not delete it.

---

## 🚀 Fast Track (Quick Start)

x64 only. **Do not rebuild `With.dll` from this tree** — use the prebuilt `SOURCE\bin\x64\With.dll` (copied next to the hosts on build).

Paths are **absolute** unless the file sits next to the host EXE. PATH is not searched.

The PEB command line is only as large as the **host** line. If the guest line is longer, `Run` / `RunEx` still returns but `GetLastError()` is `ERROR_INSUFFICIENT_BUFFER` (122), which the host may return as its error code — a crash is possible. Pad the host line (cmd/bat/ps) or restart with a longer line if you need more room.

1. **Open the solution:** `WithExe.slnx` in Visual Studio. Platform **x64**.
2. **Pick a sample:** Set the startup project to `WithExe` (native) or `WithExeClr` (CLR).
3. **F5:** Debugger arguments are `%SystemRoot%\System32\notepad.exe`. Working directory is `REDISTRIBUTABLES\BIN\x64\<config>`, so `With.dll` sits beside the host. Change the arguments for another guest (full path unless the file sits next to the host).

**Command line.**

```text
MSBuild WithExe.slnx /restore /p:Configuration=Release /p:Platform=x64
```

Output: `REDISTRIBUTABLES\BIN\x64\Release` or `Debug`.

```text
REDISTRIBUTABLES\BIN\x64\Release\WithExe.exe C:\Windows\System32\hostname.exe
REDISTRIBUTABLES\BIN\x64\Release\WithExe.exe C:\path\helper.dll C:\Windows\System32\hostname.exe
REDISTRIBUTABLES\BIN\x64\Release\WithExe.exe -e C:\Windows\System32\hostname.exe
REDISTRIBUTABLES\BIN\x64\Release\WithExe.exe -s C:\Windows\System32\hostname.exe
REDISTRIBUTABLES\BIN\x64\Release\WithExe.exe -t C:\Windows\System32\hostname.exe
REDISTRIBUTABLES\BIN\x64\Release\WithExeClr.exe C:\Windows\System32\hostname.exe
```

Helpers (any number of DLLs) first, then **one** EXE, then that EXE’s arguments. A helper DLL is the place for your own detours. Letters after the dash are one cluster (`-e`, `-icf`, `-st`, …).

---

## ⚙️ Why not PE mapping

A typical PE mapper VirtualAllocates, copies sections, walks imports, applies relocs, and fakes an entry. That is a second loader, always behind the OS, and the image is usually private memory rather than a real image section.

WithExe uses `LoadLibrary` on the EXE. Compared to mapping by hand:

* **`SEC_IMAGE`.** The file is mapped as an image section, not a memcpy into anonymous pages. Section permissions, sharing, and “this is a PE” come from the kernel.
* **No `PAGE_EXECUTE_READWRITE`.** Hand-rolled maps `VirtualProtect` the image to that flag so one region is writable and executable. AV scanners treat RWX as a malware sign. We never set it. Section rights come from `SEC_IMAGE`, not a RWX pass.
* **Real import graph.** API sets, forwards, delay-load, and the current `kernel32`/`ntdll` rules are resolved by Windows when `WITH_SYSTEM_LOADLIBRARY` is on — not a snapshot of 2015 import walking.
* **Relocs, TLS, unwind.** The loader applies them in that mode. `.pdata` is registered the usual way.
* **SxS / manifest.** An activation context is applied around the load from the EXE’s own manifest resource.
* **A real `HMODULE`.** The guest is an LDR module, not a floating allocation you must keep secret from every API that wants a module handle.
* **Command line and path.** `RunEx` can write the PEB command line (CreateProcess-style) and the loader-list name of the host EXE entry so `GetModuleFileNameW(NULL)` sees the guest.
* **Size.** The mapper file is 3584 bytes. A serious PE mapper is a project.
* **File-backed image.** A hand-rolled PE map is usually `VirtualAlloc` + memcpy: private executable pages, an `HMODULE` that is just a base, no section object, often no honest LDR path. Memory scanners look for exactly that — a PE in RAM with no confirmation of a real file on disk — and treat the process as a possible packer/injector. `LoadLibrary` maps `SEC_IMAGE` from the file. The base is a real image mapping; the path is on disk; `GetMappedFileName` / the loader list match the file.

---

## 🔄 Why not Detours `withdll`

Detours `withdll` does this: `CreateProcess` the target **suspended**, splice the new process’s **in-memory import table** (the helper DLL must export ordinal **#1**), `ResumeThread`. The guest is its own PID. That is `DetourCreateProcessWithDll` / `DetourCreateProcessWithDlls` — not `CreateRemoteThread` + `LoadLibrary` into a stranger.

WithExe is the inverse: you write the **host**; `Run` / `RunEx` maps the EXE **into this process**. Helper DLLs and Detours-style hooks run as your code in your address space. Same sample shape as `withdll` (options, helper DLLs, start) — not a remote inject.

* **No stranger PID.** No remote thread, no remote `WriteProcessMemory`, no `LoadLibrary` into a process that did not invite you in.
* **Mitigations aim at inject.** Process-protection / mitigation policy is built to stop cross-process inject. Those APIs can be blocked or useless against a process you do not own. They are not in this path.
* **You still own the hooks.** Want IAT detours, logging, a GUI message pump on SEH? That is **your** host or a helper DLL `LoadLibrary`’d before the guest — or `RunEx(..., &ep)` and call the EP yourself. `With.dll` maps; the host decides the rest.
* **Not `CreateProcess`.** The guest shares the host PID. Child processes the guest starts are ordinary processes. Mitigations that key off “this process image is X.exe” still see `WithExe.exe` / `WithExeClr` unless you deal with that yourself.

---

## 💻 Sample hosts

This tree ships **two sample hosts**. Same job: `LoadLibrary` `With.dll`, call `RunEx`, map **one** guest EXE. They are templates, not a product. The sample calls the EP on this thread (`pEp` NULL). That is not a limit.

### 1. Native C++ (`SOURCE\WithExe\WithExe.cpp`)

* CRT console host. `__try { RunEx }` — the host decides how to process SEH. A GUI host can run a secondary message pump; this sample just reports the exception.
* F5: Debug / Release x64, args = `%SystemRoot%\System32\notepad.exe`.

### 2. Managed C# (`SOURCE\WithExeClr\WithExeClr.cs`)

* .NET 8 console host. No `__try`; a guest AV kills the process. Same guest via `Properties\launchSettings.json` (native debug engine).

Command line is the same for both:

```text
WithExe.exe [options] [helper.dll ...] guest.exe [guest args...]
```

---

## 🔧 API

```c
UINT WINAPI RunEx(LPCWSTR exe, LPCWSTR cmdLine, DWORD flags, FARPROC* pEp);
UINT WINAPI Run(LPCWSTR exe, LPCWSTR cmdLine);
```

`Run` is `RunEx` with the six default bits on (`WITH_PEB_IMAGE | WITH_PEB_CMDLINE | WITH_PEB_NAME | WITH_EXIT | WITH_DLLDIR | WITH_SYSTEM_LOADLIBRARY`), `WITH_EXIT_THREAD` off, and `pEp` NULL.

* `exe` — **absolute** on-disk EXE path unless the file sits next to the host EXE. Not PATH-searched. PE **without** `IMAGE_FILE_DLL`.
* `cmdLine` — guest command line. Written into the existing PEB / `GetCommandLineW` / `GetCommandLineA` buffers when `WITH_PEB_CMDLINE` is set. Those buffers are the **host** command line; they are not grown. The guest line must already fit (usual case: a substring of the host line). If it is longer, the write still happens, `GetLastError()` after `Run` / `RunEx` is `ERROR_INSUFFICIENT_BUFFER` (**122**), and that may be returned as the host error code — a crash is possible. `-c` skips the write.
* `flags` — `WITH_*` bits below.
* `pEp` — NULL = call the guest EP on **this** thread. Non-NULL = map only; `*pEp` = guest EP; return **1**. Does not call it. `*pEp` is written only after a successful map. Host invokes `*pEp` (other thread, `__try`, …).
* Return — with `pEp` NULL: whatever the EP returns, or the guest `ExitProcess` code when `WITH_EXIT` is set. `WITH_EXIT_THREAD`: this thread ends, `RunEx` does not return. `0` if setup/map failed. Native host: `__try { RunEx(...) }`. With `pEp` set: `1` if mapped, `0` if not.

| Flag | Sample | Meaning when set |
|------|--------|------------------|
| `WITH_PEB_IMAGE` `0x1` | `-i` turns **off** | PEB `ImageBaseAddress` = guest (`GetModuleHandle(NULL)`, `FindResource(NULL)`) |
| `WITH_PEB_CMDLINE` `0x2` | `-c` turns **off** | `GetCommandLineW`/`A` + PEB `Length` = `cmdLine` |
| `WITH_PEB_NAME` `0x4` | `-f` turns **off** | loader-list name of the host EXE entry = guest (`GetModuleFileNameW(NULL)`) |
| `WITH_EXIT` `0x8` | `-e` turns **off** | guest `ExitProcess` returns from `RunEx` with its code |
| `WITH_DLLDIR` `0x10` | `-d` turns **on** (already on) | `SetDllDirectoryW` to the guest EXE folder (no CD) |
| `WITH_SYSTEM_LOADLIBRARY` `0x20` | `-s` turns **off** | system `LoadLibrary` binds IAT+TLS. Off: `With.dll` snaps IAT+TLS |
| `WITH_EXIT_THREAD` `0x40` | `-t` turns **on** | guest `ExitProcess` ends **this** thread (`RunEx` does not return). Wins over `WITH_EXIT` |

x64 only. `With.dll` must sit next to the host (already true under `REDISTRIBUTABLES\BIN`). The sample uses `LoadLibrary` / `GetProcAddress`; `With.lib` is there if you want to link your own host.

---

Copyright Igor G. Tebelev TIG(c) 2026
