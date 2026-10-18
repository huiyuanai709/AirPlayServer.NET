# Packaging

`publish.sh` and `publish.ps1` produce a self-contained Native AOT binary of `AirPlayServer.Host`. That binary is GPL-3.0-only because it compiles AirPlayFairPlay in-process. FairPlay adds no native dependency.

```bash
./packaging/publish.sh linux-x64
```

```powershell
./packaging/publish.ps1 -Runtime win-x64
```

The output directory is `artifacts/<rid>/`. Linux needs `clang` and `zlib1g-dev`. Windows needs the Visual Studio C++ build tools, which Native AOT uses as its linker. GitHub-hosted `windows-latest` already has that toolset.

`airplayserver.service` is a systemd stub. It does not embed a pairing seed or any other secret. Copy the published binary to `/usr/local/bin/AirPlayServer.Host` before enabling it. The linux-x64 release zip includes this unit.

`publish.sh` and `publish.ps1` do not create a GitHub Release. `.github/workflows/release.yml` does, on a `v*` tag or a manual run. It publishes win-x64 and linux-x64 with the same `dotnet publish` arguments, smokes `--help` and `--self-check`, and uploads zips that contain the binary, `RUN.txt`, `LICENSE`, and `NOTICE.md`. The host stays GPL-3.0-only. The workflow does not read or embed key material.

To stage a zip locally after a publish, without creating a release:

```bash
./packaging/stage-release.sh linux-x64 v0.1.0 artifacts/linux-x64 /tmp/AirPlayServer.Host-v0.1.0-linux-x64.zip
```

`resolve-release-version.sh` is what the workflow uses to turn a tag or a dispatch input into `vMAJOR.MINOR.PATCH`. A dispatch input of `0.1.0` becomes tag `v0.1.0`. See the README section on cutting a release.
