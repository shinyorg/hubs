using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Shiny.BluetoothLE.Hosting;
using Shiny.BluetoothLE.Hubs;

namespace Shiny.SwitchboardR.Tests;

public record Payload(string Text, int Number);

[BleHubClient]
public interface ITestHub
{
    Task<Payload> Echo(Payload payload);
    Task<string?> WhoAmI();
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


public class TestHub(HubLog log) : BleHub<ITestHub>
{
    public override Task OnConnectedAsync()
    {
        log.Add($"connected:{this.Context.Client.Name}");
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(string? reason)
    {
        log.Add($"disconnected:{this.Context.Client.Name}:{reason}");
        return Task.CompletedTask;
    }

    public Task<Payload> Echo(Payload payload) => Task.FromResult(payload);
    public Task<string?> WhoAmI() => Task.FromResult(this.Context.Client.Name);

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
