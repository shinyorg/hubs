using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Shiny.BluetoothLE.Hosting;
using Shiny.BluetoothLE.Hubs;

namespace Shiny.UniversalHubs.Tests;

public record Payload(string Text, int Number);

[BleHubClient]
public interface ITestHub
{
    Task<Payload> Echo(Payload payload);
    Task<string?> WhoAmI();
    Task<string> MyConnection();
    Task<string> Inspect();
    Task Record(string value);
    Task Fail();
    IAsyncEnumerable<int> Count(int to, CancellationToken cancellationToken);
    IAsyncEnumerable<int> CountThenFail(int to, CancellationToken cancellationToken);
    Task JoinGroup(string group);
    Task SayToGroup(string group, string message);
    Task PushSequence(int count);
    Task Kick(string reason);

    event Action<string> Message;
    event Action<string, int> Numbered;
}


[BleHubClient]
public interface IOtherHub
{
    Task<string> Ping();
}


public class OtherHub : UniversalHub<IOtherHub>
{
    public Task<string> Ping() => Task.FromResult("pong");
}


[JsonSerializable(typeof(Payload))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
public partial class TestJsonContext : JsonSerializerContext;


public class HubLog
{
    readonly List<string> entries = new();

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (this.entries)
                return this.entries.ToList();
        }
    }

    public void Add(string entry)
    {
        lock (this.entries)
            this.entries.Add(entry);
    }
}


public class TestHub(HubLog log) : UniversalHub<ITestHub>
{
    public override Task OnConnectedAsync(HubConnection connection)
    {
        log.Add($"connected:{connection.Client.Name}");
        log.Add($"connected-request:{connection.Http?.HttpContext?.Request.Method}");
        connection.Properties["greeting"] = $"hello {connection.Client.Name}";
        log.Add($"connected-via:{connection.Transport}:{connection.Id}");
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(HubConnection connection, HubDisconnect disconnect)
    {
        log.Add($"disconnected:{connection.Client.Name}:{disconnect.Description}");
        log.Add($"reason:{connection.Client.Name}:{disconnect.Reason}");
        log.Add($"disconnected-via:{connection.Transport}:{connection.Id}");
        log.Add($"disconnected-http:{(connection.Http == null ? "none" : "set")}");
        return Task.CompletedTask;
    }

    public Task<Payload> Echo(Payload payload) => Task.FromResult(payload);
    public Task<string?> WhoAmI() => Task.FromResult(this.Context.Client.Name);
    public Task<string> Inspect() => Task.FromResult(String.Join("|",
        this.Connection.Properties["greeting"],
        $"line:{this.Connection.Id == HubConnection.HttpPrefix + this.Connection.Http?.LineId}",
        $"request:{this.Connection.Http?.HttpContext?.Request.Method}",
        $"ble:{(this.Connection.Ble == null ? "none" : "set")}"
    ));

    public Task<string> MyConnection() => Task.FromResult($"{this.Connection.Transport}:{this.Connection.Id}");

    public Task Record(string value)
    {
        log.Add($"record:{value}");
        return Task.CompletedTask;
    }

    public Task Fail() => throw new InvalidOperationException("boom");

    public async IAsyncEnumerable<int> Count(int to, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 1; i <= to; i++)
        {
            await Task.Yield();
            yield return i;
        }
    }

    public async IAsyncEnumerable<int> CountThenFail(int to, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 1; i <= to; i++)
        {
            await Task.Yield();
            yield return i;
        }
        throw new ArgumentException("ran out");
    }

    public Task JoinGroup(string group) => this.Groups.AddToGroupAsync(this.Context.ConnectionId, group);
    public Task SayToGroup(string group, string message) => this.Clients.Group(group).Message(message);

    public async Task PushSequence(int count)
    {
        for (var i = 0; i < count; i++)
            await this.Clients.Caller.Numbered("seq", i);
    }

    public Task Kick(string reason)
    {
        this.Context.Abort(reason);
        return Task.CompletedTask;
    }
}


/// <summary>
/// A device that can't host over Bluetooth - every call fails, so only Wi-Fi hosting starts
/// </summary>
public class NoBluetoothHosting : DispatchProxy
{
    public static IBleHostingManager Create() => DispatchProxy.Create<IBleHostingManager, NoBluetoothHosting>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => throw new InvalidOperationException("Bluetooth hosting is not available in tests");
}
