# About

Unity Debug Adapter (DA) for debugging the Unity Editor or applications using the Mono scripting
backend.
> [!IMPORTANT]
> debugging IL2CPP applications is not supported.

This project is adjusted (somewhat forked) from the deprecated and quite frankly bloated
[vscode-unity-debug][vscode-unity-debug] project. [vscode-unity-debug][vscode-unity-debug] does
not work out-of-the-box with new dotnet because of failure to detect the '\r\n\r\n' sequence
in client <-> debug-adapter messages. The failure is caused by an IndexOf("\r\n\r\n") issue
(see https://github.com/dotnet/runtime/issues/43736).

Since the project is stale and no longer accepts pull-requests/patches, fixing
issues of the original [vscode-unity-debug][vscode-unity-debug] project and debloating
it are the reasons for the existence of this project.

Hopefully when Unity finally moves to .NET Core, the need for this repository will cease to
exist. In the meantime, if you are doing Unity development on a text-editor/IDE other than
VSCode, Ryder, or Visual Studio, and you want debugging functionalities with a clear license
(MIT) then this project is for you.

In case you are looking for instructions on how to hook this to Neovim, see [neovim-unity][unity-debugger-support].

## Installation

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

Then, if you want to run the debug adapter, a global installation of Mono has to be
available:

```bash
mono bin/Release/unity-debug-adapter.exe
```

You should then be seeying an output like this:

```text
21/08/2025 00:31:01 [I] waiting for debug protocol on stdin/stdout
21/08/2025 00:31:01 [I] constructing UnityDebugSession
21/08/2025 00:31:01 [I] done constructing UnityDebugSession
```

## Usage

`unity-debug-adapter.exe` accepts two optional long parameters:
- `--trace-level` sets the logging trace level: `trace` | `debug` | `info` | `warn` | `error` | `critical` | `none`
- `--log-file` provides a path to a log file. In case this is not provided, and `--trace-level` is not `none`, logging
  is output to stderr.

Example of an invocation:
```bash
mono bin/Release/unity-debug-adapter.exe --trace-level=trace --log-file=dap-log.txt
```

## For Developers

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

## License

MIT License. See LICENSE.txt.

[vscode-unity-debug]: https://github.com/Unity-Technologies/vscode-unity-debug
[unity-debugger-support]: https://github.com/walcht/neovim-unity#unity-debugger-support
