# Packaging

`publish.sh` and `publish.ps1` produce a self-contained Native AOT binary of `AirPlayServer.Host`. That binary is GPL-3.0-only because it compiles AirPlayFairPlay in-process. FairPlay adds no native dependency.

```bash
./packaging/publish.sh linux-x64
```

```powershell
./packaging/publish.ps1 -Runtime win-x64
```

The output directory is `artifacts/<rid>/`. Linux needs `clang` and `zlib1g-dev`. Windows needs the Visual Studio C++ build tools, which Native AOT uses as its linker.

`airplayserver.service` is a systemd stub. It does not embed a pairing seed or any other secret. Copy the published binary to `/usr/local/bin/AirPlayServer.Host` before enabling it.

These scripts do not create a GitHub release.
