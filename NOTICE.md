# Notices

AirPlayServer.NET is an independent .NET implementation of an AirPlay receiver.
It is not a copy of the upstream C/C++ tree and does not vendor that tree's
binaries or third-party libraries.

## Upstream

Protocol behavior for discovery and the transient pairing handshake follows the
publicly described receiver in:

- <https://github.com/xenos1337/AirPlayServer> — MIT, Copyright (c) 2025 xenos1337
- That project is an updated fork of <https://github.com/fingergit/airplay2-win>

The upstream `how-it-works.md`, README, and CLAUDE.md describe the pipeline this
port is structured around: mDNS advertisement, HTTP pair-setup / pair-verify,
then a media path. This repository accepts the mirror control and TCP data
path. Windows can decode H.264 through Media Foundation. FairPlay `fp-setup`
and 72-byte `ekey` unwrap run in-process.

## License

This repository is GPL-3.0-only. The license text is in `LICENSE`.

The published Native AOT host is GPL-3.0-only because it compiles FairPlay
into the process. FairPlay is not an optional download and it is not loaded
from a native plugin by default.

## FairPlay

`deps/AirPlayFairPlay/` is a verbatim copy of `src/AirPlayFairPlay` from
<https://github.com/huiyuanai709/AirPlayFairPlayPlugin> at commit
`8a6a9c46fc48e00ff69c760da7c04658f0b37e2f`, plus a local
`Directory.Build.props` that marks the project Native AOT compatible.
`FairPlaySession` answers `POST /fp-setup` and unwraps a 72-byte `ekey`.
The host attaches one `FairPlayKeys` instance to both hooks.

That library is a C# port of PlayFair:

- Upstream snapshot: <https://github.com/xenos1337/AirPlayServer>
- Commit: `aa9ce92455ac249076d9309bd435985df9cc37e8` (2026-09-26)
- Upstream paths: `AirPlayServerLib/lib/playfair/` and `AirPlayServerLib/lib/fairplay_playfair.c`
- License: GPL-3.0

`Tables.cs` holds the bytes from `omg_hax.h`. The AirPlayFairPlay port keeps
the setup replies, handshake layout, and decrypt results of that snapshot.

## Not included

This repository does not contain, link, or translate:

- FFmpeg binaries or sources (upstream uses LGPL-2.1-or-later builds)
- libplist, SDL, ImGui, or the vendored Bonjour/mDNSResponder `dnssd` sources
- Any prebuilt `.dll`, `.lib`, or `.exe` from the upstream `external/` directory

mDNS advertisement is a small RFC 6762/6763 sender written for this project.
Pairing uses the runtime's X25519 and AES plus an RFC 8032 Ed25519
implementation written for this project. The same AES-CTR primitive derives
the mirror stream key once a 16-byte content key exists. That derivation is
separate from FairPlay. A 72-byte `ekey` unwraps after the in-process
handshake. `--lab-key` can supply a raw 16-byte key instead. `--media-key-plugin`
is an optional native override and is not required for FairPlay.

Windows H.264 decode uses Media Foundation (`mfplat` / the system H.264 MFT),
compiled only into the Windows host. The linux-x64 publish does not link
`mfplat` or `user32`. FFmpeg is not linked. JPEG stills for TapClock are
encoded in-process; ImageSharp is not linked.

## Trademarks

Apple, AirPlay, Bonjour, and Apple TV are trademarks of Apple Inc. Model and
source-version strings advertised on the wire are compatibility values, not a
claim that this process is an Apple device.
