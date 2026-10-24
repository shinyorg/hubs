using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Shiny.BluetoothLE.Hubs.Tests;

public record Payload(string Text, int Number);

[BleHubClient]
public interface ITestHub
{
    Task<Payload> Echo(Payload payload);
    Task<string> Concat(string a, int b, Payload c);
    Task<string?> WhoAmI();
    Task Record(string value);
    Task Fail();
    Task<string> Slow(int delayMs, CancellationToken cancellationToken);
    IAsyncEnumerable<int> Count(int to, CancellationToken cancellationToken);
    Task JoinGroup(string group);
    Task SayToGroup(string group, string message);
    Task SayToOthers(string message);
    Task SayToCaller(string message);
    Task PushSequence(int count);
    Task Kick(string reason);

    event Action<string> Message;
    event Action<string, int> Numbered;
    event Action Pinged;
}


[JsonSerializable(typeof(Payload))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
public partial class TestJsonContext : JsonSerializerContext;


public class HubLog
{
    public List<string> Entries { get; } = new();
    public TaskCompletionSource<bool> SlowCancelled { get; set; } = new();

    public void Add(string entry)
    {
        lock (this.Entries)
            this.Entries.Add(entry);
    }
}


public class TestHub(HubLog log) : BleHub<ITestHub>
{
    public override Task OnConnectedAsync()
    {
        log.Add($"connected:{this.Context.Client.Name}");
        return this.Groups.AddToGroupAsync(this.Context.ConnectionId, "everyone");
    }

    public override Task OnDisconnectedAsync(HubDisconnect disconnect)
    {
        log.Add($"disconnected:{this.Context.Client.Name}:{disconnect.Description}");
        log.Add($"reason:{this.Context.Client.Name}:{disconnect.Reason}");
        return Task.CompletedTask;
    }

    public override Task OnRenamedAsync(string? previousName)
    {
        if (this.Context.Client.Name == "Boom")
            throw new InvalidOperationException("Not that name");

        log.Add($"renamed:{previousName}->{this.Context.Client.Name}");
        return Task.CompletedTask;
    }

    public Task<Payload> Echo(Payload payload) => Task.FromResult(payload);
    public Task<string> Concat(string a, int b, Payload c) => Task.FromResult($"{a}{b}{c.Text}{c.Number}");
    public Task<string?> WhoAmI() => Task.FromResult(this.Context.Client.Name);

    public Task Record(string value)
    {
        log.Add($"record:{value}");
        return Task.CompletedTask;
    }

    public Task Fail() => throw new InvalidOperationException("boom");

    // the hub may omit the contract's CancellationToken parameter ... or take it, like here
    public async Task<string> Slow(int delayMs, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delayMs, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            log.SlowCancelled.TrySetResult(true);
            throw;
        }
        return "slow";
    }

    public async IAsyncEnumerable<int> Count(int to, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 1; i <= to; i++)
        {
            await Task.Yield();
            yield return i;
        }
    }

    public Task JoinGroup(string group) => this.Groups.AddToGroupAsync(this.Context.ConnectionId, group);
    public Task SayToGroup(string group, string message) => this.Clients.Group(group).Message(message);
    public Task SayToOthers(string message) => this.Clients.Others.Message(message);

    public async Task SayToCaller(string message)
    {
        await this.Clients.Caller.Message(message);
        await this.Clients.Caller.Numbered(message, message.Length);
        await this.Clients.Caller.Pinged();
    }

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


[BleHubClient]
public interface ISecondHub
{
    Task<string> Ping();
}


public class SecondHub : BleHub<ISecondHub>
{
    public Task<string> Ping() => Task.FromResult("pong");
}
