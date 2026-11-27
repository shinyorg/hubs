using System.Collections.Concurrent;
using Android.App;
using Android.Gms.Common;
using Android.Gms.Extensions;
using Android.Gms.Nearby;
using Android.Gms.Nearby.Connection;
using Android.OS;
using Microsoft.Extensions.Logging;

// what Nearby Connections needs - merged into the app's manifest. The runtime ones are asked for when Nearby first starts.
[assembly: UsesPermission("android.permission.BLUETOOTH", MaxSdkVersion = 30)]
[assembly: UsesPermission("android.permission.BLUETOOTH_ADMIN", MaxSdkVersion = 30)]
[assembly: UsesPermission("android.permission.ACCESS_WIFI_STATE")]
[assembly: UsesPermission("android.permission.CHANGE_WIFI_STATE")]
[assembly: UsesPermission("android.permission.ACCESS_COARSE_LOCATION", MaxSdkVersion = 31)]
[assembly: UsesPermission("android.permission.ACCESS_FINE_LOCATION", MaxSdkVersion = 31)]
[assembly: UsesPermission("android.permission.BLUETOOTH_ADVERTISE")]
[assembly: UsesPermission("android.permission.BLUETOOTH_CONNECT")]
[assembly: UsesPermission("android.permission.BLUETOOTH_SCAN")]
[assembly: UsesPermission("android.permission.NEARBY_WIFI_DEVICES")]

namespace Shiny.UniversalHubs;

/// <summary>
/// Nearby Connections from Google Play services. Callbacks come on the main thread; the link moves them off it.
/// </summary>
internal sealed class AndroidNearbyRadio(AndroidPlatform platform, ILogger<AndroidNearbyRadio>? logger = null) : INearbyRadio
{
    // Payload.Type and PayloadTransferUpdate.Status
    const int PayloadBytes = 1;
    const int PayloadFile = 2;
    const int TransferSuccess = 1;
    const int TransferFailure = 2;
    const int TransferInProgress = 3;

    readonly IConnectionsClient client = NearbyClass.GetConnectionsClient(Application.Context);
    readonly ConcurrentDictionary<long, Payload> incomingFiles = new();
    readonly ConcurrentDictionary<long, ParcelFileDescriptor> outgoingFiles = new();
    INearbyRadioListener listener = null!;
    Lifecycle lifecycle = null!;
    Discovery discovery = null!;
    Payloads payloads = null!;


    public bool IsSupported { get; } = GoogleApiAvailability.Instance.IsGooglePlayServicesAvailable(Application.Context) == ConnectionResult.Success;


    public void Start(INearbyRadioListener listener)
    {
        this.listener = listener;
        this.lifecycle = new Lifecycle(listener);
        this.discovery = new Discovery(listener);
        this.payloads = new Payloads(this);
    }


    public async Task StartAdvertising(string serviceId, byte[] endpointInfo, NearbyStrategy strategy)
    {
        await this.RequestAccess().ConfigureAwait(false);
        await this.client.StartAdvertising(endpointInfo, serviceId, this.lifecycle, new AdvertisingOptions.Builder().SetStrategy(ToNative(strategy))!.Build()).AsAsync();
    }

    public void StopAdvertising() => this.client.StopAdvertising();

    public async Task StartDiscovery(string serviceId, NearbyStrategy strategy)
    {
        await this.RequestAccess().ConfigureAwait(false);
        await this.client.StartDiscovery(serviceId, this.discovery, new DiscoveryOptions.Builder().SetStrategy(ToNative(strategy))!.Build()).AsAsync();
    }

    public void StopDiscovery() => this.client.StopDiscovery();

    public async Task RequestConnection(string endpointId, byte[] info)
        => await this.client.RequestConnection(info, endpointId, this.lifecycle).AsAsync();

    public async Task AcceptConnection(string endpointId)
        => await this.client.AcceptConnection(endpointId, this.payloads).AsAsync();

    public void RejectConnection(string endpointId) => _ = this.Quietly(this.client.RejectConnection(endpointId), "reject");

    public void Disconnect(string endpointId) => this.client.DisconnectFromEndpoint(endpointId);

    public void CancelFile(long payloadId) => _ = this.Quietly(this.client.CancelPayload(payloadId), "cancel a file");


    public void SendBytes(string endpointId, byte[] data)
    {
        // Nearby sends an endpoint's payloads in the order they are handed over - a failure means the connection is going
        _ = this.Quietly(this.client.SendPayload(endpointId, Payload.FromBytes(data)), "send");
    }


    public long SendFile(string endpointId, string path)
    {
        var pfd = ParcelFileDescriptor.Open(new Java.IO.File(path), ParcelFileMode.ReadOnly)
            ?? throw new IOException($"Could not open {path}");
        var payload = Payload.FromFile(pfd);
        var id = payload.Id;
        this.outgoingFiles[id] = pfd;
        _ = this.SendFilePayload(endpointId, id, payload);
        return id;
    }


    async Task SendFilePayload(string endpointId, long id, Payload payload)
    {
        try
        {
            await this.client.SendPayload(endpointId, payload).AsAsync();
        }
        catch (Exception ex)
        {
            // no transfer update follows a payload Nearby refused - say it failed, or the send waits forever
            logger?.LogDebug(ex, "Nearby could not send a file");
            if (this.outgoingFiles.TryRemove(id, out var pfd))
            {
                CloseQuietly(pfd);
                this.listener.FileTransfer(endpointId, id, 0, 0, NearbyTransferStatus.Failure, null);
            }
        }
    }


    /// <summary>
    /// The runtime permissions Nearby needs on this version of Android - asked for once, then checked
    /// </summary>
    async Task RequestAccess()
    {
        string[] needed = OperatingSystem.IsAndroidVersionAtLeast(33)
            ? ["android.permission.BLUETOOTH_ADVERTISE", "android.permission.BLUETOOTH_CONNECT", "android.permission.BLUETOOTH_SCAN", "android.permission.NEARBY_WIFI_DEVICES"]
            : OperatingSystem.IsAndroidVersionAtLeast(31)
                ? ["android.permission.BLUETOOTH_ADVERTISE", "android.permission.BLUETOOTH_CONNECT", "android.permission.BLUETOOTH_SCAN", "android.permission.ACCESS_FINE_LOCATION"]
                : ["android.permission.ACCESS_FINE_LOCATION"];

        var missing = needed.Where(x => platform.GetCurrentPermissionStatus(x) != AccessState.Available).ToArray();
        if (missing.Length == 0)
            return;

        await platform.RequestPermissions(missing).ConfigureAwait(false);
        var denied = missing.Where(x => platform.GetCurrentPermissionStatus(x) != AccessState.Available).ToList();
        if (denied.Count > 0)
            throw new InvalidOperationException($"Nearby needs {String.Join(", ", denied.Select(x => x["android.permission.".Length..]))}");
    }


    void Received(string endpointId, Payload payload)
    {
        switch (payload.PayloadType)
        {
            case PayloadBytes:
                if (payload.AsBytes() is { } bytes)
                    this.listener.BytesReceived(endpointId, bytes);
                break;

            case PayloadFile:
                // it arrives over time - its transfer updates say when it's done
                this.incomingFiles[payload.Id] = payload;
                break;
        }
    }


    void Updated(string endpointId, PayloadTransferUpdate update)
    {
        var id = update.PayloadId;
        var outgoing = this.outgoingFiles.ContainsKey(id);
        if (!outgoing && !this.incomingFiles.ContainsKey(id))
            return;   // a BYTES payload

        var status = update.TransferStatus switch
        {
            TransferSuccess => NearbyTransferStatus.Success,
            TransferFailure => NearbyTransferStatus.Failure,
            TransferInProgress => NearbyTransferStatus.InProgress,
            _ => NearbyTransferStatus.Canceled
        };

        var transferred = update.BytesTransferred;
        var total = update.TotalBytes;
        if (status == NearbyTransferStatus.InProgress)
        {
            this.listener.FileTransfer(endpointId, id, transferred, total, status, null);
        }
        else if (outgoing)
        {
            if (this.outgoingFiles.TryRemove(id, out var pfd))
                CloseQuietly(pfd);
            this.listener.FileTransfer(endpointId, id, transferred, total, status, null);
        }
        else if (this.incomingFiles.TryRemove(id, out var payload) && status == NearbyTransferStatus.Success)
        {
            // this runs on the main thread - copying a big file here would freeze the app. The link matches a file to its
            // name by payload id, so the success landing after later messages changes nothing.
            _ = Task.Run(() =>
            {
                var path = this.TakeFile(payload);
                this.listener.FileTransfer(endpointId, id, transferred, total, path == null ? NearbyTransferStatus.Failure : NearbyTransferStatus.Success, path);
            });
        }
        else
        {
            this.listener.FileTransfer(endpointId, id, transferred, total, status == NearbyTransferStatus.Success ? NearbyTransferStatus.Failure : status, null);
        }
    }


    void CloseQuietly(ParcelFileDescriptor pfd)
    {
        try
        {
            pfd.Close();
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Could not close a sent Nearby file");
        }
    }


    /// <summary>
    /// Moves a received file out of Nearby's folder into one the link owns
    /// </summary>
    string? TakeFile(Payload payload)
    {
        var target = Path.Combine(Path.GetTempPath(), "shiny-nearby-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = payload.AsFile();
            if (file?.AsUri() is { } uri)
            {
                var resolver = Application.Context.ContentResolver!;
                using (var input = resolver.OpenInputStream(uri) ?? throw new IOException("The received file can't be read"))
                using (var output = File.Create(target))
                    input.CopyTo(output);

                try
                {
                    resolver.Delete(uri, null, null);
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "Could not delete Nearby's copy of a received file");
                }
                return target;
            }
            if (file?.AsParcelFileDescriptor() is { } pfd)
            {
                using (var input = new Android.Runtime.InputStreamInvoker(new ParcelFileDescriptor.AutoCloseInputStream(pfd)))
                using (var output = File.Create(target))
                    input.CopyTo(output);
                return target;
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not take a received Nearby file");
        }
        return null;
    }


    async Task Quietly(Android.Gms.Tasks.Task task, string what)
    {
        try
        {
            await task.AsAsync();
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Nearby could not {What}", what);
        }
    }


    static Strategy ToNative(NearbyStrategy strategy) => strategy == NearbyStrategy.Star ? Strategy.P2pStar! : Strategy.P2pCluster!;


    sealed class Lifecycle(INearbyRadioListener listener) : ConnectionLifecycleCallback
    {
        public override void OnConnectionInitiated(string endpointId, ConnectionInfo info)
            => listener.ConnectionInitiated(endpointId, info.GetEndpointInfo() ?? [], info.IsIncomingConnection);

        public override void OnConnectionResult(string endpointId, ConnectionResolution resolution)
            => listener.ConnectionResult(
                endpointId,
                resolution.Status.IsSuccess,
                resolution.Status.IsSuccess ? null : ConnectionsStatusCodes.GetStatusCodeString(resolution.Status.StatusCode)
            );

        public override void OnDisconnected(string endpointId) => listener.Disconnected(endpointId);
    }


    sealed class Discovery(INearbyRadioListener listener) : EndpointDiscoveryCallback
    {
        public override void OnEndpointFound(string endpointId, DiscoveredEndpointInfo info)
            => listener.EndpointFound(endpointId, info.GetEndpointInfo() ?? []);

        public override void OnEndpointLost(string endpointId) => listener.EndpointLost(endpointId);
    }


    sealed class Payloads(AndroidNearbyRadio radio) : PayloadCallback
    {
        public override void OnPayloadReceived(string endpointId, Payload payload) => radio.Received(endpointId, payload);
        public override void OnPayloadTransferUpdate(string endpointId, PayloadTransferUpdate update) => radio.Updated(endpointId, update);
    }
}
