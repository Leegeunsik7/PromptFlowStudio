#!/usr/bin/env bash
# PromptFlow Studio v4.4.0 — iOS IPA 패키징 + OTA(manifest.plist/install.html) 생성 (macOS 전용).
# 사용: bash scripts/package-all.sh --target ios --version 4.4.0 --out outputs
set -euo pipefail

TARGET="ios"; VERSION="4.4.0"; OUT="outputs"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --target) TARGET="$2"; shift 2;;
    --version) VERSION="$2"; shift 2;;
    --out) OUT="$2"; shift 2;;
    *) shift;;
  esac
done

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUTDIR="$ROOT/$OUT"
mkdir -p "$OUTDIR"
echo "[PKG] iOS IPA 패키징 (v$VERSION)…"

if [[ "$TARGET" != "ios" && "$TARGET" != "all" ]]; then echo "[PKG] iOS 타깃 아님 — 건너뜀"; exit 0; fi
if [[ "$(uname)" != "Darwin" ]]; then echo "[PKG] ⚠ iOS IPA 는 macOS(Xcode) 에서만 빌드됩니다 — Graceful 건너뜀"; exit 0; fi

MOBILE="$ROOT/../PromptFlowStudio.Mobile/PromptFlowStudio.Mobile.csproj"
dotnet publish "$MOBILE" -f net8.0-ios -c Release \
  -p:ApplicationDisplayVersion="$VERSION" -p:RuntimeIdentifier=ios-arm64 -p:BuildIpa=true

IPA="$(find "$(dirname "$MOBILE")" -name '*.ipa' | head -1 || true)"
if [[ -n "$IPA" ]]; then
  cp "$IPA" "$OUTDIR/PromptFlowStudio-v$VERSION-ios.ipa"
  echo "[PKG] ✅ IPA → $OUTDIR/PromptFlowStudio-v$VERSION-ios.ipa"
  # OTA manifest/HTML 은 CI 게이트(IosOtaManifestGenerator) 또는 앱 UI 에서 배포 URL 확정 후 생성.
else
  echo "[PKG] ⚠ IPA 산출물 없음(서명/프로파일 확인)"
fi
