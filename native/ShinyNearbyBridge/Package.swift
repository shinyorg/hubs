// swift-tools-version:5.9
//
// The native half of Shiny.UniversalHubs.Nearby on iOS and macOS: Google's NearbyConnections (Swift, not callable
// from .NET) behind a handful of C functions. build.sh turns it into ShinyNearbyBridge.xcframework, which the package
// carries as a NativeReference.
import PackageDescription

let package = Package(
  name: "ShinyNearbyBridge",
  platforms: [.iOS(.v15), .macOS(.v12)],
  products: [
    .library(name: "ShinyNearbyBridge", type: .dynamic, targets: ["ShinyNearbyBridge"])
  ],
  dependencies: [
    // pinned: NearbyConnections has no releases, and main moves
    .package(url: "https://github.com/google/nearby.git", revision: "203b6e1597d5cf2ba0336d86cb7669bb74879082")
  ],
  targets: [
    .target(
      name: "ShinyNearbyBridge",
      dependencies: [.product(name: "NearbyConnections", package: "nearby")]
    )
  ]
)
