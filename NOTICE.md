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
then a media path. This repository now accepts the mirror control and TCP
data path. Windows can decode H.264 through Media Foundation. FairPlay unwrap is
still not included.

## Not included

This repository does not contain, link, or translate:

- PlayFair / FairPlay stream decryption (upstream `AirPlayServerLib/lib/playfair`, GPL-3.0)
- FFmpeg binaries or sources (upstream uses LGPL-2.1-or-later builds)
- libplist, SDL, ImGui, or the vendored Bonjour/mDNSResponder `dnssd` sources
- Any prebuilt `.dll`, `.lib`, or `.exe` from the upstream `external/` directory

mDNS advertisement is a small RFC 6762/6763 sender written for this project.
Pairing uses the runtime's X25519 and AES plus an RFC 8032 Ed25519
implementation written for this project. The same AES-CTR primitive derives
the mirror stream key once a 16-byte content key exists. That derivation is
not FairPlay, and PlayFair is not included. A 72-byte `ekey` stays undecrypted
unless the process is given `IMediaKeyUnwrapper` or a native plugin exporting
`AirPlay_TryUnwrapContentKey`. `POST /fp-setup` stays HTTP 501 unless
`IFairPlaySetup` or `AirPlay_FairPlaySetup` returns a body. Neither hook ships
a FairPlay implementation.

Windows H.264 decode uses Media Foundation (`mfplat` / the system H.264 MFT),
compiled only into the Windows host. The linux-x64 publish does not link
`mfplat` or `user32`. FFmpeg is not linked. JPEG stills for TapClock are
encoded in-process; ImageSharp is not linked.

## Trademarks

Apple, AirPlay, Bonjour, and Apple TV are trademarks of Apple Inc. Model and
source-version strings advertised on the wire are compatibility values, not a
claim that this process is an Apple device.
