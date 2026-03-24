# About

Unity Debug Adapter (DA) for debugging the Unity Editor or applications using the Mono scripting
backend.

> [!IMPORTANT]
> debugging IL2CPP applications is not and will not be supported.

This project is adjusted (forked) from the deprecated and *quite frankly* bloated
[vscode-unity-debug][vscode-unity-debug] project.

If you are doing Unity development on a text-editor/IDE other than VSCode,
Ryder, or Visual Studio, and you want debugging functionalities with a
permissive license (MIT) then this project is for you.

In case you are looking for instructions on how to hook this to Neovim, see
[neovim-unity][unity-debugger-support].

## Build from Source

Clone the repo and its submodule(s):

```bash
git clone --recurse-submodules https://github.com/walcht/unity-dap.git
cd unity-dap/
```

Then build using dotnet (tested on dotnet 9.0.108, on Ubuntu 24.04):

```bash
dotnet build --configuration=Release unity-debug-adapter/unity-debug-adapter.csproj
```

If you get build error messages related to `Microsoft.SymbolStore` and `Microsoft.FileFormats`
such as these:

```bash
/unity-dap/unity-debug-adapter/unity-debug-adapter.csproj : error NU1101: Unable to find package Microsoft.SymbolStore. No packages exist with this id in source(s): nuget.org
/unity-dap/unity-debug-adapter/unity-debug-adapter.csproj : error NU1101: Unable to find package Microsoft.FileFormats. No packages exist with this id in source(s): nuget.org
```

Then make sure to add outdated packages as a source for Nuget and then run the build command
again (the error is not caused by this project but rather its Mono debugger dependency -
see [this issue](https://github.com/mono/debugger-libs/issues/402)):

```bash
dotnet nuget add source 'https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/index.json' -n "OutdatedPackages"
```

Then, if you want to run the debug adapter:

```bash
bin/Release/unity-debug-adapter.exe
```

  If you built this from source, you might need to:
  ```bash
  chmod +x bin/Release/unity-debug-adapter.exe
  ```

You should then get an output like this:

```text
21/08/2025 00:31:01 [I] waiting for debug protocol on stdin/stdout
21/08/2025 00:31:01 [I] constructing UnityDebugSession
21/08/2025 00:31:01 [I] done constructing UnityDebugSession
```

## For Developers

This section is mainly for developers interested in contributing or want to
learn the intenals of this project.

### Overview

```
                    Translates `requests` from nvim (which are DAP conformant)
                    to Mono.Debugger-sepecific requests.
                    Translates Mono.Debugger-specific
                    responses to DAP-conformant `responses`.
                    Writes logs to s_LogFile or stderr              Locally running Unity Editor (which always uses Mono). Or
                              |                                     a local/remote running Unity Player instance using Mono
                              |                                                 backend (with debugging enabled)
                              |                                                             |
    +------+            +-----------+                  +--------------------+ <  - - - - -  +
    | Nvim |----------- | UNITY DAP | ---------------- |       UNITY        |
    +------+     ^      +-----------+        ^         |   (Mono.Debugger)  |
                 |                           |         +--------------------+
                 |                           |
         via stdin and stdout                + via a TCP/IP socket (ip:port)
         (_outputStream and inputStream)
```

### Backends

This debug adapter essentially communicates with the following backends:

- Mono.Debugger.Soft: this is the official Mono debugger in `debugger-libs`.
- GDB: Mono applications can be debugged using `gdb`. This is still not
implemented yet and is TODO.


### Why not IL2CPP

I will not include add support for debugging C# -> IL2CPP code mainly because
of these facts/opinions:

- IL2CPP is closed source and I might get into trouble if I implement something
like what Visual Studio does (i.e., some sort of mapping between original C#
code and generated IL2CPP C++ code).
- I think it makes little sense to debug C++ code (generated or not) via
stepping through a completely different language (e.g., managed C#).
- Complexity. There are very few people who are using Neovim for Unity, fewer
are using debuggers, and even fewer who want to debug IL2CPP through C# via
Neovim. This is simply not worth the effort.
- IL2CPP is simply C++. Just debug it using a proper C++ debugger (e.g., gdb).

### Why Not Use [vscode-unity-debug][vscode-unity-debug]?

[vscode-unity-debug][vscode-unity-debug] does not work out-of-the-box with new
dotnet because of failure to detect the '\r\n\r\n' sequence in
client <-> debug-adapter messages. The failure is caused by an
`IndexOf("\r\n\r\n")` issue (see https://github.com/dotnet/runtime/issues/43736).

Since the project is stale and no longer accepts pull-requests/patches, fixing
issues of the original [vscode-unity-debug][vscode-unity-debug] project and
debloating it are the reasons for the existence of this project.

The project is also very poorly written, all responses are sent twice, the
project relies on heavy usage of the `dynamic` keyword which requires JIT and
which causes issues when this project is compiled with dotnet (rather than
xbuild).


### In an Ideal World

Hopefully we get Unity .NET CLR support before the sun explodes so that we can
use actual proper, industry-standard, open-source, and actively maintained
.NET debuggers.

When that happens, this adapter will add support to it and will, probably, be
much more useful.

## License

MIT License. See LICENSE.txt.

[vscode-unity-debug]: https://github.com/Unity-Technologies/vscode-unity-debug
[unity-debugger-support]: https://github.com/walcht/neovim-unity#unity-debugger-support
