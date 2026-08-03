#!/bin/sh
# ============================================================
# macOS .app 번들 생성 — 독(Dock)에 "Aurum" 이름과
# 아이콘이 제대로 나오게 한다. (dotnet run 은 어셈블리명 + 기본 아이콘)
#
#   사용:  sh tools/packaging/macos/make-app.sh
#   결과:  tools/dist/Aurum.app
# ============================================================
set -eu

TOOLS_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
APP_NAME="Aurum"
BUNDLE_ID="com.infinitt.aurum"
VERSION="0.1.0"
RID="osx-arm64"          # Intel 맥 배포 시 osx-x64 로
PUBLISH_DIR="$TOOLS_DIR/dist/publish-$RID"
APP_DIR="$TOOLS_DIR/dist/$APP_NAME.app"
ICON_SRC="$TOOLS_DIR/src/PrismOne.Studio/Assets/icon.png"
ICON_1024="$TOOLS_DIR/src/PrismOne.Studio/Assets/icon_1024.png"

echo "== publish ($RID, self-contained) =="
dotnet publish "$TOOLS_DIR/src/PrismOne.Studio" -c Release -r "$RID" \
    --self-contained -o "$PUBLISH_DIR" -v q

echo "== icns 생성 =="
ICONSET="$TOOLS_DIR/dist/icon.iconset"
rm -rf "$ICONSET" && mkdir -p "$ICONSET"
for SZ in 16 32 64 128 256 512; do
    sips -z $SZ $SZ "$ICON_SRC" --out "$ICONSET/icon_${SZ}x${SZ}.png" >/dev/null
    SZ2=$((SZ * 2))
    SRC2="$ICON_SRC"; [ $SZ2 -eq 1024 ] && [ -f "$ICON_1024" ] && SRC2="$ICON_1024"
    sips -z $SZ2 $SZ2 "$SRC2" --out "$ICONSET/icon_${SZ}x${SZ}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$TOOLS_DIR/dist/icon.icns"

echo "== 번들 구성 =="
rm -rf "$APP_DIR"
mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"
cp -R "$PUBLISH_DIR/." "$APP_DIR/Contents/MacOS/"
cp "$TOOLS_DIR/dist/icon.icns" "$APP_DIR/Contents/Resources/icon.icns"

cat > "$APP_DIR/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>$APP_NAME</string>
    <key>CFBundleDisplayName</key><string>$APP_NAME</string>
    <key>CFBundleExecutable</key><string>Aurum</string>
    <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleIconFile</key><string>icon</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>LSMinimumSystemVersion</key><string>11.0</string>
</dict>
</plist>
PLIST

rm -rf "$ICONSET"
echo "== 완료: $APP_DIR =="
