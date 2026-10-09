#!/bin/zsh
# Compila PayloIos con swiftc y lo instala en el simulador de iOS, sin proyecto de Xcode.
# Uso: ./build.sh [udid] [--no-install]   (sin udid usa el simulador encendido)
set -euo pipefail

root=${0:A:h}
out=$root/build
app=$out/PayloIos.app
device=booted
install=1
for arg in "$@"; do
  case $arg in
    --no-install) install=0 ;;
    *) device=$arg ;;
  esac
done

rm -rf $out && mkdir -p $app

sdk=$(xcrun --sdk iphonesimulator --show-sdk-path)
xcrun --sdk iphonesimulator swiftc -target $(uname -m)-apple-ios15.0-simulator -sdk $sdk -O \
  -o $app/PayloIos $root/Sources/*.swift

cp $root/Info.plist $app/
cp $root/Resources/* $app/
codesign --force --sign - $app

echo "App: $app"
(( install )) || exit 0

xcrun simctl install $device $app
xcrun simctl launch --terminate-running-process $device com.driftcheck.payloios
