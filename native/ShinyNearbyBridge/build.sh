#!/bin/bash
# Builds ShinyNearbyBridge.xcframework into native/ShinyNearbyBridge/build/ios (device + simulator) and build/macos, where
# Shiny.UniversalHubs.Nearby picks it up as a NativeReference. CI runs it before packing; run it locally once before
# building the package or an app for iOS / macOS. NearbyConnections' C++ core makes the first build take a few minutes.
set -euo pipefail
cd "$(dirname "$0")"

OUT=build
DD=build/derived
rm -rf "$OUT/archives"
mkdir -p "$OUT/archives"

archive() {
  local destination="$1" name="$2"
  xcodebuild archive \
    -scheme ShinyNearbyBridge \
    -destination "$destination" \
    -archivePath "$OUT/archives/$name" \
    -derivedDataPath "$DD" \
    -configuration Release \
    SKIP_INSTALL=NO \
    BUILD_LIBRARY_FOR_DISTRIBUTION=YES \
    -quiet
}

archive "generic/platform=iOS" ios
archive "generic/platform=iOS Simulator" ios-simulator
archive "generic/platform=macOS" macos

# one xcframework per platform, so each of the package's target frameworks carries only its own slices
framework() {
  local name="$1"
  local found
  found=$(find "$OUT/archives/$name.xcarchive" -name ShinyNearbyBridge.framework -type d | head -1)
  if [ -z "$found" ]; then
    echo "no framework in the $name archive" >&2
    exit 1
  fi
  echo "$found"
}

rm -rf "$OUT/ios" "$OUT/macos"
xcodebuild -create-xcframework \
  -framework "$(framework ios)" \
  -framework "$(framework ios-simulator)" \
  -output "$OUT/ios/ShinyNearbyBridge.xcframework"
xcodebuild -create-xcframework \
  -framework "$(framework macos)" \
  -output "$OUT/macos/ShinyNearbyBridge.xcframework"
echo "Built $OUT/ios/ShinyNearbyBridge.xcframework and $OUT/macos/ShinyNearbyBridge.xcframework"
