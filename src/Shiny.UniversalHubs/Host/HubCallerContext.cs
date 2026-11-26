namespace Shiny.UniversalHubs;

internal interface IHubInternal
{
    void Initialize(HubCallerContext context, HubRuntime runtime);
    Task OnConnectedAsync();
    Task OnDisconnectedAsync(HubDisconnect disconnect);
    Task OnRenamedAsync(string? previousName);
}


public sealed class HubCallerContext
{
    readonly Action<string?> abort;

    internal HubCallerContext(HubConnectedClient client, Action<string?> abort, CancellationToken connectionAborted)
    {
        this.Client = client;
        this.abort = abort;
        this.ConnectionAborted = connectionAborted;
    }

    public string ConnectionId => this.Client.Id;
    public HubConnectedClient Client { get; }

    /// <summary>
    /// Per connection state, kept for as long as the client is connected
    /// </summary>
    public IDictionary<string, object> Items => this.Client.Items;

    /// <summary>
    /// Cancelled when the client goes away
    /// </summary>
    public CancellationToken ConnectionAborted { get; }

    /// <summary>
    /// Ends this client's session (cooperative - the client library disconnects itself).
    /// Called from a hub method, it takes effect once that method's reply has been sent.
    /// </summary>
    public void Abort(string? reason = null) => this.abort(reason);
}
