#!/usr/bin/env bash
# Write GitHub Release notes for one tag to stdout.
# Usage: release-notes.sh v1.2.3
set -euo pipefail

if [ "$#" -ne 1 ]; then
  echo "usage: release-notes.sh <tag>" >&2
  exit 2
fi

tag="$1"
case "$tag" in
  v*.*.*) ;;
  *)
    echo "tag must look like v1.2.3" >&2
    exit 2
    ;;
esac
case "$tag" in
  *[!A-Za-z0-9._-]*)
    echo "unsafe tag: ${tag}" >&2
    exit 2
    ;;
esac

prerelease_block=""
case "$tag" in
  *-*)
    prerelease_block="这是预发布版本，不会标成 Latest。

"
    ;;
esac

notes=$(cat <<'EOF'
__PRERELEASE_BLOCK__发布出来的 Native AOT 宿主是 **GPL-3.0-only**，因为进程内编进了 FairPlay。源码就是本仓库的 tag `__TAG__`。压缩包里没有配对种子，也没有 FairPlay 密钥材料，也没有 .pdb / .dbg 调试符号。

| 文件 | 说明 |
| --- | --- |
| `AirPlayServer.Host-__TAG__-win-x64.zip` | Windows x64。TapClock 用这个。 |
| `AirPlayServer.Host-__TAG__-linux-x64.zip` | Linux x64。在 `ubuntu-latest` 上构建，需要兼容的 glibc。 |
| `SHA256SUMS.txt` | 上面两个 zip 的 SHA-256。 |

Windows 上让手机画面进 TapClock：

1. 先启动 TapClock，让它监听 ATBF（TCP 42125）。
2. 解压 win-x64 包，运行 `AirPlayServer.Host.exe --screen 480x1040 --tapclock 127.0.0.1:42125`。不要加 `--demo-frame`。
3. iPhone 在同一局域网里发现接收端并镜像。

每个 zip 里有 `RUN.txt`。构建时已经跑过 `--help` 和 `--self-check`。拿到包之后可以再跑一次 `--self-check`，通过时会打印 `self-check ok`。
EOF
)

notes="${notes//__TAG__/$tag}"
notes="${notes//__PRERELEASE_BLOCK__/$prerelease_block}"
printf '%s\n' "$notes"
