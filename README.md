# 🏗️ WithExe

## 📜 Preface & Motivation

In-process EXE host for Windows x64. **Not another PE mapper.** Official name **WithExe** — the Detours counterpart of **withdll**: that sample injects a DLL into a new process; WithExe maps an **EXE** into yours.

`With.dll` loads the target with the OS `LoadLibrary` path and a few tricks so a **Windows EXE** can be mapped and run inside the caller. Imports, relocs, manifests, and `SEC_IMAGE` are the loader’s job — not a from-scratch mapper’s.

This is an **instrumentation POC**, in the same neighborhood as Detours. The two sample hosts follow the Detours `samples/withdll` skeleton (parse options, load helper DLLs, start) — `Run` instead of `DetourCreateProcessWithDlls`.

---

## ⚠️ Notice & Disclaimer

> **Binary mapper, tiny on purpose:**
> `With.dll` is shipped **in binary form** — a **tiny 2 KB** DLL (`SOURCE\bin\x64\With.dll`, copied next to the hosts under `REDISTRIBUTABLES\BIN\x64`). There is no mapper source in this tree. Drop it in IDA if you are a specialist — 2 KB is not a secret. If you need customization, look **below**: change a sample host, or `LoadLibrary` a helper DLL before the guest. `SOURCE\include\With.h` is the API; `SOURCE\lib\x64` has the import library if you link your own host. Rebuild the hosts if you want; do not rebuild the mapper from here.

> **NO WARRANTY:** This is a proof of concept. Unsupported use of the Windows loader. Provided **"AS IS"** with no warranty. Extra behavior belongs in your host or a helper DLL, not in `With.dll`. Use, modify, and experiment at your own risk.

---

## 🔍 Overview

The sample hosts load **one EXE**. Helper DLLs come first (`LoadLibrary`); the first EXE is the guest; everything after that is that guest’s command line.

There is also **no patch-to-load / patch-to-run** story. WithExe does not rewrite the guest’s IAT, and it does not patch system exports to hook start, run, or exit. The subset of redirections it needs is done with the thread’s **DRx** hardware breakpoints and a vectored handler. The targeted EXE’s import table and system DLL bytes stay as the loader left them.

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

1. **Open the solution:** `WithExe.slnx` in Visual Studio. Platform **x64**.
2. **Pick a sample:** Set the startup project to `WithExe` (native) or `WithExeClr` (CLR).
3. **F5:** Debugger arguments are `%SystemRoot%\System32\notepad.exe`. Working directory is `REDISTRIBUTABLES\BIN\x64\<config>`, so `With.dll` sits beside the host. Change the arguments for another guest (full path unless the file sits next to the host).

**Command line.**

```text
MSBuild WithExe.slnx /restore /p:Configuration=Release /p:Platform=x64
```

Output: `REDISTRIBUTABLES\BIN\x64\Release` or `Debug`.

```text
REDISTRIBUTABLES\BIN\x64\Release\WithExe.exe notepad.exe
REDISTRIBUTABLES\BIN\x64\Release\WithExe.exe helper.dll C:\full\path\app.exe --flag
REDISTRIBUTABLES\BIN\x64\Release\WithExe.exe -d C:\full\path\app.exe
REDISTRIBUTABLES\BIN\x64\Release\WithExeClr.exe C:\full\path\app.exe
```

`dlls` / `exe` need a **full path** unless the file is next to that host EXE. Helpers (any number of DLLs) first, then **one** EXE, then that EXE’s arguments. A helper DLL is the place for your own detours. `-d` is host-only (DLL directory + cwd = guest folder).

---

## ⚙️ Why not PE mapping

A typical PE mapper `VirtualAlloc`s, copies sections, walks imports, applies relocs, and fakes an entry. That is a second loader, always behind the OS, and the image is usually private memory rather than a real image section.

WithExe uses `LoadLibrary` on the EXE (the image is briefly treated as a DLL for the loader, then restored). Compared to mapping by hand:

* **`SEC_IMAGE`.** The file is mapped as an image section, not a memcpy into anonymous pages. Section permissions, sharing, and “this is a PE” come from the kernel.
* **No `PAGE_EXECUTE_READWRITE`.** Hand-rolled maps `VirtualProtect` the image to that flag so one region is writable and executable. AV scanners treat RWX as a malware sign. We never set it. Section rights come from `SEC_IMAGE`, not a RWX pass.
* **Real import graph.** API sets, forwards, delay-load, and the current `kernel32`/`ntdll` rules are resolved by Windows, not a snapshot of 2015 import walking.
* **Relocs, TLS, unwind.** The loader applies them. `.pdata` is registered the usual way.
* **SxS / manifest.** An activation context is applied around the load from the EXE’s own manifest resource.
* **A real `HMODULE`.** The guest is an LDR module, not a floating allocation you must keep secret from every API that wants a module handle.
* **Command line and path.** `Run` writes the PEB command line (CreateProcess-style). The guest sees its own module path.
* **Size.** The mapper file is 2048 bytes. A serious PE mapper is a project.
* **File-backed image.** A hand-rolled PE map is usually `VirtualAlloc` + memcpy: private executable pages, an `HMODULE` that is just a base, no section object, often no honest LDR path. Memory scanners look for exactly that — a PE in RAM with no confirmation of a real file on disk — and treat the process as a possible packer/injector. `LoadLibrary` maps `SEC_IMAGE` from the file. The base is a real image mapping; the path is on disk; `GetMappedFileName` / the loader list match the file.

---

## 🔄 Why not Detours `withdll`

Detours `samples/withdll` starts the target with `CreateProcess` **suspended**, then **injects** a DLL into that new PID (`WriteProcessMemory` / `CreateRemoteThread` / `LoadLibrary`, or an entry-point patch). You instrument *someone else’s* process.

WithExe is the inverse: you write the **host**; `Run` maps the EXE **into this process**. Helper DLLs and Detours-style hooks run as your code in your address space. Same sample shape as `withdll` (options, helper DLLs, start) — not a remote inject.

* **No stranger PID.** No remote thread, no remote `WriteProcessMemory`, no `LoadLibrary` into a process that did not invite you in.
* **Mitigations aim at inject.** Process-protection / mitigation policy is built to stop cross-process `CreateRemoteThread` + `LoadLibrary`. Those APIs can be blocked or useless against a process you do not own. They are not in this path.
* **You still own the hooks.** Want IAT detours, logging, a GUI message pump on SEH? That is **your** host or a helper DLL `LoadLibrary`’d before the guest — the Detours toolkit still applies *in-process*. `With.dll` only maps and calls the EP.
* **Not `CreateProcess`.** The guest shares the host PID. Child processes the guest starts are ordinary processes. Mitigations that key off “this process image is X.exe” still see `WithExe.exe` / `WithExeClr` unless you deal with that yourself.

---

## 💻 Sample hosts

This tree ships **two sample hosts**. Same job: `LoadLibrary` `With.dll`, call `Run`, map **one** guest EXE on this thread. They are templates, not a product.

### 1. Native C++ (`SOURCE\WithExe\WithExe.cpp`)

* CRT console host. `__try { Run }` — the host decides how to process SEH. A GUI host can run a secondary message pump; this sample just reports the exception.
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
UINT WINAPI Run(LPCWSTR exe, LPCWSTR cmdLine);
```

* `exe` — **full path** of a PE **without** `IMAGE_FILE_DLL`, unless the file sits next to the host EXE. Not PATH.
* `cmdLine` — written into the PEB UNICODE `CommandLine` (`Buffer` + `Length`) and kernel32's `GetCommandLineA` cache. Same idea as `CreateProcess` `lpCommandLine`. The guest line is a substring of the host line, so both buffers already fit.
* Return — whatever the guest EP returns (`UINT`), or the guest’s process-exit code (`Run` returns instead of the process dying). `0` if setup/map failed. Native host: `__try { Run(...) }`.

x64 only. `With.dll` must sit next to the host (already true under `REDISTRIBUTABLES\BIN`). The host thread calls `Run`. The sample uses `LoadLibrary` / `GetProcAddress`; `With.lib` is there if you want to link your own host.

---

## 🎩 A Tip of the Hat & "Premium" Support

I am a retired veteran of the software industry currently living on a fixed Social Security income — to the point where I can hardly afford my internet bill and AI helper. This **2 KB** in-process EXE host is offered as a working POC, not a funded product line.

This repository is provided as a read-only reference. There is no active maintenance, and I will not be responding to questions, bug reports, or pull requests regarding this code — **unless your request includes a transaction ID proving you sent a donation to the wallet below.**

If this tiny mapper just saved your well-funded enterprise from shipping a `PAGE_EXECUTE_READWRITE` PE map or a cross-process inject, please consider a tip of the hat. Donations are genuinely needed, deeply appreciated, and are the only way to get this old developer to come out of retirement to look at your issue.

* **BTC (Bitcoin):** `3CVm3CZ3JoRv7jW8ypkYfUED599hdorJgJ`

![BTC](assets/3CVm3CZ3JoRv7jW8ypkYfUED599hdorJgJ.png)

Thank you!

---

Copyright Igor G. Tebelev TIG(c) 2026
