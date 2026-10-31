// Google's NearbyConnections behind C functions that .NET calls through function pointers
// (Shiny.UniversalHubs.Nearby/Platforms/Apple/AppleNearbyRadio.cs). Everything the radio reports comes back through the
// one callback given to snb_start, on the bridge's own serial queue. Pointers passed to the callback are only valid
// during the call - .NET copies what it keeps.

import Foundation
import NearbyConnections

/// kind, endpoint id (UTF-8), data, data length, a, b, c, status
public typealias SnbCallback = @convention(c) (
  Int32, UnsafePointer<CChar>?, UnsafePointer<UInt8>?, Int32, Int64, Int64, Int64, Int32
) -> Void

// event kinds - the same numbers as AppleNearbyRadio.Event
private let evCompleted: Int32 = 1          // a = request id, data = error (UTF-8) or none
private let evEndpointFound: Int32 = 2      // data = endpoint info
private let evEndpointLost: Int32 = 3
private let evConnectionInitiated: Int32 = 4  // data = info, status = 1 when incoming
private let evConnectionResult: Int32 = 5   // status = 1 when connected, data = error
private let evDisconnected: Int32 = 6
private let evBytesReceived: Int32 = 7      // data = the payload
private let evFileTransfer: Int32 = 8       // a = payload id, b = transferred, c = total, status, data = path (incoming, done)

// file transfer status - NearbyTransferStatus
private let tsInProgress: Int32 = 0
private let tsSuccess: Int32 = 1
private let tsFailure: Int32 = 2
private let tsCanceled: Int32 = 3


private final class Bridge: NSObject, ConnectionManagerDelegate, AdvertiserDelegate, DiscovererDelegate {
  static let shared = Bridge()

  let queue = DispatchQueue(label: "shiny.nearby.bridge")
  var callback: SnbCallback?
  var manager: ConnectionManager?
  var advertiser: Advertiser?
  var discoverer: Discoverer?
  var pendingRequests: [EndpointID: (Bool) -> Void] = [:]
  var connected: Set<EndpointID> = []
  var incomingFiles: [PayloadID: URL] = [:]
  var outgoingFiles: Set<PayloadID> = []
  var sending: [PayloadID: CancellationToken] = [:]

  // ---- to .NET ----

  func emit(
    _ kind: Int32, _ endpoint: String? = nil, _ data: Data? = nil,
    a: Int64 = 0, b: Int64 = 0, c: Int64 = 0, status: Int32 = 0
  ) {
    guard let callback = callback else { return }
    let call = { (endpointPtr: UnsafePointer<CChar>?) in
      if let data = data, !data.isEmpty {
        data.withUnsafeBytes { raw in
          callback(kind, endpointPtr, raw.bindMemory(to: UInt8.self).baseAddress, Int32(data.count), a, b, c, status)
        }
      } else {
        callback(kind, endpointPtr, nil, 0, a, b, c, status)
      }
    }
    if let endpoint = endpoint {
      endpoint.withCString { call($0) }
    } else {
      call(nil)
    }
  }

  func complete(_ request: Int64, _ error: Error?) {
    emit(evCompleted, nil, error.map { Data(String(describing: $0).utf8) }, a: request)
  }

  /// One ConnectionManager per app - the link makes sure the service id and strategy never change
  func manager(_ serviceId: String, _ strategy: Int32) -> ConnectionManager {
    if let manager = manager { return manager }
    let created = ConnectionManager(
      serviceID: serviceId, strategy: strategy == 1 ? .star : .cluster, queue: queue)
    created.delegate = self
    // BLE v2 is what Android uses - without it an iPhone has no Bluetooth medium in common with an Android phone.
    // Wi-Fi Aware stays off: on iOS it needs devices paired through Apple's Wi-Fi Aware pairing first.
    created.enableBLEV2()
    manager = created
    return created
  }

  // ---- AdvertiserDelegate ----

  func advertiser(
    _ advertiser: Advertiser, didReceiveConnectionRequestFrom endpointID: EndpointID,
    with context: Data, connectionRequestHandler: @escaping (Bool) -> Void
  ) {
    pendingRequests[endpointID] = connectionRequestHandler
    emit(evConnectionInitiated, endpointID, context, status: 1)
  }

  // ---- DiscovererDelegate ----

  func discoverer(_ discoverer: Discoverer, didFind endpointID: EndpointID, with context: Data) {
    emit(evEndpointFound, endpointID, context)
  }

  func discoverer(_ discoverer: Discoverer, didLose endpointID: EndpointID) {
    emit(evEndpointLost, endpointID)
  }

  // ---- ConnectionManagerDelegate ----

  func connectionManager(
    _ connectionManager: ConnectionManager, didReceive verificationCode: String,
    from endpointID: EndpointID, verificationHandler: @escaping (Bool) -> Void
  ) {
    // the hub handshake decides who gets in - and the requesting side hears its connection begin here
    verificationHandler(true)
    if pendingRequests[endpointID] == nil {
      emit(evConnectionInitiated, endpointID, nil, status: 0)
    }
  }

  func connectionManager(
    _ connectionManager: ConnectionManager, didReceive data: Data, withID payloadID: PayloadID,
    from endpointID: EndpointID
  ) {
    emit(evBytesReceived, endpointID, data)
  }

  func connectionManager(
    _ connectionManager: ConnectionManager, didReceive stream: InputStream,
    withID payloadID: PayloadID, from endpointID: EndpointID,
    cancellationToken token: CancellationToken
  ) {
    // hubs never send streams
    token.cancel()
  }

  func connectionManager(
    _ connectionManager: ConnectionManager, didStartReceivingResourceWithID payloadID: PayloadID,
    from endpointID: EndpointID, at localURL: URL, withName name: String,
    cancellationToken token: CancellationToken
  ) {
    incomingFiles[payloadID] = localURL
  }

  func connectionManager(
    _ connectionManager: ConnectionManager, didReceiveTransferUpdate update: TransferUpdate,
    from endpointID: EndpointID, forPayload payloadID: PayloadID
  ) {
    let outgoing = outgoingFiles.contains(payloadID)
    guard outgoing || incomingFiles[payloadID] != nil else { return }  // a BYTES payload

    switch update {
    case .progress(let progress):
      emit(evFileTransfer, endpointID, a: payloadID, b: progress.completedUnitCount, c: progress.totalUnitCount, status: tsInProgress)

    case .success:
      if outgoing {
        outgoingFiles.remove(payloadID)
        sending.removeValue(forKey: payloadID)
        emit(evFileTransfer, endpointID, a: payloadID, status: tsSuccess)
      } else if let url = incomingFiles.removeValue(forKey: payloadID) {
        // hand over a file the link owns
        let target = FileManager.default.temporaryDirectory.appendingPathComponent("shiny-nearby-" + UUID().uuidString)
        do {
          try FileManager.default.moveItem(at: url, to: target)
          emit(evFileTransfer, endpointID, Data(target.path.utf8), a: payloadID, status: tsSuccess)
        } catch {
          emit(evFileTransfer, endpointID, a: payloadID, status: tsFailure)
        }
      }

    case .canceled, .failure:
      outgoingFiles.remove(payloadID)
      sending.removeValue(forKey: payloadID)
      incomingFiles.removeValue(forKey: payloadID)
      if case .canceled = update {
        emit(evFileTransfer, endpointID, a: payloadID, status: tsCanceled)
      } else {
        emit(evFileTransfer, endpointID, a: payloadID, status: tsFailure)
      }
    }
  }

  func connectionManager(
    _ connectionManager: ConnectionManager, didChangeTo state: ConnectionState,
    for endpointID: EndpointID
  ) {
    switch state {
    case .connecting:
      break

    case .connected:
      pendingRequests.removeValue(forKey: endpointID)
      connected.insert(endpointID)
      emit(evConnectionResult, endpointID, status: 1)

    case .rejected:
      pendingRequests.removeValue(forKey: endpointID)
      emit(evConnectionResult, endpointID, Data("rejected".utf8), status: 0)

    case .disconnected:
      pendingRequests.removeValue(forKey: endpointID)
      if connected.remove(endpointID) != nil {
        emit(evDisconnected, endpointID)
      } else {
        emit(evConnectionResult, endpointID, Data("disconnected".utf8), status: 0)
      }
    }
  }
}


private func string(_ pointer: UnsafePointer<CChar>?) -> String {
  pointer.map { String(cString: $0) } ?? ""
}

private func data(_ pointer: UnsafePointer<UInt8>?, _ length: Int32) -> Data {
  guard let pointer = pointer, length > 0 else { return Data() }
  return Data(bytes: pointer, count: Int(length))
}


@_cdecl("snb_start")
public func snb_start(_ callback: SnbCallback) {
  Bridge.shared.queue.sync { Bridge.shared.callback = callback }
}

@_cdecl("snb_start_advertising")
public func snb_start_advertising(
  _ serviceId: UnsafePointer<CChar>?, _ info: UnsafePointer<UInt8>?, _ length: Int32,
  _ strategy: Int32, _ request: Int64
) {
  let service = string(serviceId)
  let context = data(info, length)
  let bridge = Bridge.shared
  bridge.queue.async {
    let advertiser = Advertiser(connectionManager: bridge.manager(service, strategy))
    advertiser.delegate = bridge
    bridge.advertiser = advertiser
    advertiser.startAdvertising(using: context) { error in
      bridge.queue.async { bridge.complete(request, error) }
    }
  }
}

@_cdecl("snb_stop_advertising")
public func snb_stop_advertising() {
  let bridge = Bridge.shared
  bridge.queue.async {
    bridge.advertiser?.stopAdvertising()
    bridge.advertiser = nil
  }
}

@_cdecl("snb_start_discovery")
public func snb_start_discovery(_ serviceId: UnsafePointer<CChar>?, _ strategy: Int32, _ request: Int64) {
  let service = string(serviceId)
  let bridge = Bridge.shared
  bridge.queue.async {
    let discoverer = Discoverer(connectionManager: bridge.manager(service, strategy))
    discoverer.delegate = bridge
    bridge.discoverer = discoverer
    discoverer.startDiscovery { error in
      bridge.queue.async { bridge.complete(request, error) }
    }
  }
}

@_cdecl("snb_stop_discovery")
public func snb_stop_discovery() {
  let bridge = Bridge.shared
  bridge.queue.async {
    bridge.discoverer?.stopDiscovery()
  }
}

@_cdecl("snb_request_connection")
public func snb_request_connection(
  _ endpointId: UnsafePointer<CChar>?, _ info: UnsafePointer<UInt8>?, _ length: Int32, _ request: Int64
) {
  let endpoint = string(endpointId)
  let context = data(info, length)
  let bridge = Bridge.shared
  bridge.queue.async {
    guard let discoverer = bridge.discoverer else {
      bridge.complete(request, NSError(domain: "ShinyNearby", code: 1, userInfo: [NSLocalizedDescriptionKey: "Not discovering"]))
      return
    }
    discoverer.requestConnection(to: endpoint, using: context) { error in
      bridge.queue.async { bridge.complete(request, error) }
    }
  }
}

@_cdecl("snb_accept")
public func snb_accept(_ endpointId: UnsafePointer<CChar>?) {
  let endpoint = string(endpointId)
  let bridge = Bridge.shared
  // a connection this device asked for was accepted when it was verified
  bridge.queue.async { bridge.pendingRequests.removeValue(forKey: endpoint)?(true) }
}

@_cdecl("snb_reject")
public func snb_reject(_ endpointId: UnsafePointer<CChar>?) {
  let endpoint = string(endpointId)
  let bridge = Bridge.shared
  bridge.queue.async { bridge.pendingRequests.removeValue(forKey: endpoint)?(false) }
}

@_cdecl("snb_send_bytes")
public func snb_send_bytes(_ endpointId: UnsafePointer<CChar>?, _ bytes: UnsafePointer<UInt8>?, _ length: Int32) {
  let endpoint = string(endpointId)
  let payload = data(bytes, length)
  let bridge = Bridge.shared
  // on the bridge's queue, so payloads leave in the order they were sent
  bridge.queue.async {
    _ = bridge.manager?.send(payload, to: [endpoint])
  }
}

@_cdecl("snb_send_file")
public func snb_send_file(_ endpointId: UnsafePointer<CChar>?, _ path: UnsafePointer<CChar>?) -> Int64 {
  let endpoint = string(endpointId)
  let url = URL(fileURLWithPath: string(path))
  let id = PayloadID.unique()
  let bridge = Bridge.shared
  bridge.queue.async {
    bridge.outgoingFiles.insert(id)
    bridge.sending[id] = bridge.manager?.sendResource(at: url, withName: url.lastPathComponent, to: [endpoint], id: id) { error in
      if error != nil {
        bridge.queue.async {
          bridge.sending.removeValue(forKey: id)
          if bridge.outgoingFiles.remove(id) != nil {
            bridge.emit(evFileTransfer, endpoint, a: id, status: tsFailure)
          }
        }
      }
    }
  }
  return id
}

@_cdecl("snb_cancel")
public func snb_cancel(_ payloadId: Int64) {
  // through the token sendResource returned - the transfer then reports as canceled
  let bridge = Bridge.shared
  bridge.queue.async {
    bridge.sending[payloadId]?.cancel()
  }
}

@_cdecl("snb_disconnect")
public func snb_disconnect(_ endpointId: UnsafePointer<CChar>?) {
  let endpoint = string(endpointId)
  let bridge = Bridge.shared
  bridge.queue.async {
    bridge.manager?.disconnect(from: endpoint)
  }
}
