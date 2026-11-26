namespace Shiny.BluetoothLE.Hubs;

/// <summary>
/// Marks a hub contract. Methods (Task, Task&lt;T&gt;, IAsyncEnumerable&lt;T&gt;) are called by clients and run on the host.
/// Events (Action or Action&lt;T1..T4&gt;) are pushed by the host to clients.
/// The source generator emits a client proxy (when Shiny.BluetoothLE.Hubs.Client is referenced) and typed push senders for hubs
/// (when Shiny.BluetoothLE.Hubs.Host is referenced).
/// </summary>
[AttributeUsage(AttributeTargets.Interface, Inherited = false)]
public sealed class BleHubClientAttribute : Attribute
{
    /// <summary>
    /// Name of the generated client proxy class. Defaults to the interface name without its leading I, plus "Client".
    /// </summary>
    public string? ProxyName { get; set; }
}
