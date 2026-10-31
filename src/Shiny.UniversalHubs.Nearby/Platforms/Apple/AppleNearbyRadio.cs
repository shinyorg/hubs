using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Shiny.UniversalHubs;

/// <summary>
/// Google's NearbyConnections through ShinyNearbyBridge.framework (native/ShinyNearbyBridge): C functions in, one callback out. The
/// callback is a static [UnmanagedCallersOnly] function pointer - no delegates, no reflection.
/// </summary>
internal sealed unsafe partial class AppleNearbyRadio : INearbyRadio
{
    const string Library = "@rpath/ShinyNearbyBridge.framework/ShinyNearbyBridge";

    // the bridge's event kinds (Bridge.swift)
    enum Event
    {
        Completed = 1,
        EndpointFound = 2,
        EndpointLost = 3,
        ConnectionInitiated = 4,
        ConnectionResult = 5,
        Disconnected = 6,
        BytesReceived = 7,
        FileTransfer = 8
    }

    // there is one NearbyConnections client per app, so one radio
    static AppleNearbyRadio? current;

    readonly ConcurrentDictionary<long, TaskCompletionSource> requests = new();
    readonly ILogger? logger;
    INearbyRadioListener listener = null!;
    long nextRequest;


    public AppleNearbyRadio(ILogger<AppleNearbyRadio>? logger = null)
    {
        this.logger = logger;
        if (Interlocked.CompareExchange(ref current, this, null) != null)
            throw new InvalidOperationException("Nearby has one radio per app");
    }


    public bool IsSupported => true;


    public void Start(INearbyRadioListener listener)
    {
        this.listener = listener;
        snb_start(&OnEvent);
    }


    public Task StartAdvertising(string serviceId, byte[] endpointInfo, NearbyStrategy strategy)
    {
        var (id, task) = this.Request();
        fixed (byte* info = endpointInfo)
            snb_start_advertising(serviceId, info, endpointInfo.Length, (int)strategy, id);
        return task;
    }


    public void StopAdvertising() => snb_stop_advertising();


    public Task StartDiscovery(string serviceId, NearbyStrategy strategy)
    {
        var (id, task) = this.Request();
        snb_start_discovery(serviceId, (int)strategy, id);
        return task;
    }


    public void StopDiscovery() => snb_stop_discovery();


    public Task RequestConnection(string endpointId, byte[] info)
    {
        var (id, task) = this.Request();
        fixed (byte* bytes = info)
            snb_request_connection(endpointId, bytes, info.Length, id);
        return task;
    }


    public Task AcceptConnection(string endpointId)
    {
        snb_accept(endpointId);
        return Task.CompletedTask;
    }


    public void RejectConnection(string endpointId) => snb_reject(endpointId);


    public void SendBytes(string endpointId, byte[] data)
    {
        fixed (byte* bytes = data)
            snb_send_bytes(endpointId, bytes, data.Length);
    }


    public long SendFile(string endpointId, string path) => snb_send_file(endpointId, path);
    public void CancelFile(long payloadId) => snb_cancel(payloadId);
    public void Disconnect(string endpointId) => snb_disconnect(endpointId);


    (long Id, Task Task) Request()
    {
        var id = Interlocked.Increment(ref this.nextRequest);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        this.requests[id] = tcs;
        return (id, tcs.Task);
    }


    void Raise(Event kind, string? endpoint, ReadOnlySpan<byte> data, long a, long b, long c, int status)
    {
        var id = endpoint ?? String.Empty;
        switch (kind)
        {
            case Event.Completed:
                if (this.requests.TryRemove(a, out var request))
                {
                    if (data.IsEmpty)
                        request.TrySetResult();
                    else
                        request.TrySetException(new InvalidOperationException($"Nearby: {Encoding.UTF8.GetString(data)}"));
                }
                break;

            case Event.EndpointFound:
                this.listener.EndpointFound(id, data.ToArray());
                break;

            case Event.EndpointLost:
                this.listener.EndpointLost(id);
                break;

            case Event.ConnectionInitiated:
                this.listener.ConnectionInitiated(id, data.ToArray(), status == 1);
                break;

            case Event.ConnectionResult:
                this.listener.ConnectionResult(id, status == 1, data.IsEmpty ? null : Encoding.UTF8.GetString(data));
                break;

            case Event.Disconnected:
                this.listener.Disconnected(id);
                break;

            case Event.BytesReceived:
                this.listener.BytesReceived(id, data.ToArray());
                break;

            case Event.FileTransfer:
                this.listener.FileTransfer(id, a, b, c, (NearbyTransferStatus)status, data.IsEmpty ? null : Encoding.UTF8.GetString(data));
                break;
        }
    }


    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnEvent(int kind, byte* endpoint, byte* data, int length, long a, long b, long c, int status)
    {
        try
        {
            var radio = current;
            if (radio == null)
                return;

            var endpointId = endpoint == null ? null : Marshal.PtrToStringUTF8((IntPtr)endpoint);
            radio.Raise((Event)kind, endpointId, data == null ? [] : new ReadOnlySpan<byte>(data, length), a, b, c, status);
        }
        catch (Exception ex)
        {
            // an exception must never unwind into Swift
            current?.logger?.LogWarning(ex, "Nearby event {Kind} failed", kind);
        }
    }


    [LibraryImport(Library)]
    private static partial void snb_start(delegate* unmanaged[Cdecl]<int, byte*, byte*, int, long, long, long, int, void> callback);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial void snb_start_advertising(string serviceId, byte* info, int length, int strategy, long request);

    [LibraryImport(Library)]
    private static partial void snb_stop_advertising();

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial void snb_start_discovery(string serviceId, int strategy, long request);

    [LibraryImport(Library)]
    private static partial void snb_stop_discovery();

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial void snb_request_connection(string endpointId, byte* info, int length, long request);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial void snb_accept(string endpointId);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial void snb_reject(string endpointId);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial void snb_send_bytes(string endpointId, byte* data, int length);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial long snb_send_file(string endpointId, string path);

    [LibraryImport(Library)]
    private static partial void snb_cancel(long payloadId);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial void snb_disconnect(string endpointId);
}
