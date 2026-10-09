using System.Text.Json.Serialization;
using Shiny.Actors;
using Sample.Hub;

namespace Sample.Services;

/// <summary>
/// One conversation's history, kept on disk. Its id is the conversation id; every message added is also published on
/// the conversation's stream, which an open ChatView reads.
/// </summary>
public interface IConversationActor : IActor
{
    Task Open(string name, bool isVisiting);

    /// <summary>
    /// Stores a message - null when it was already there (a resend)
    /// </summary>
    Task<ChatLine?> Add(ChatLine line);

    Task<IReadOnlyList<ChatLine>> Lines();
}


public class ConversationState
{
    public string Name { get; set; } = "";
    public bool IsVisiting { get; set; }
    public List<ChatLine> Lines { get; set; } = new();
}


[AutoSave]
public class ConversationActor : Actor<ConversationState>, IConversationActor
{
    public async Task Open(string name, bool isVisiting)
    {
        var isNew = String.IsNullOrEmpty(this.State.Name);
        this.State.Name = name;
        this.State.IsVisiting = isVisiting;

        if (isNew)
            await this.Actors.Get<IChatDirectory>(ChatDirectory.Me).Remember(new ConversationInfo(this.Id, name, isVisiting));
    }


    public async Task<ChatLine?> Add(ChatLine line)
    {
        if (this.State.Lines.Any(x => x.Id == line.Id))
            return null;

        this.State.Lines.Add(line);
        await this.Actors.GetStream<ChatLine>(this.Id).PublishAsync(line);
        return line;
    }


    public Task<IReadOnlyList<ChatLine>> Lines() => Task.FromResult<IReadOnlyList<ChatLine>>(this.State.Lines.ToList());
}


/// <summary>
/// Remembers every conversation this device has had, so the inbox survives a restart
/// </summary>
public interface IChatDirectory : IActor
{
    Task Remember(ConversationInfo conversation);
    Task<IReadOnlyList<ConversationInfo>> All();
}


public record ConversationInfo(string Id, string Name, bool IsVisiting);


public class ChatDirectoryState
{
    public List<ConversationInfo> Conversations { get; set; } = new();
}


[AutoSave]
public class ChatDirectory : Actor<ChatDirectoryState>, IChatDirectory
{
    public const string Me = "me";

    public Task Remember(ConversationInfo conversation)
    {
        this.State.Conversations.RemoveAll(x => x.Id == conversation.Id);
        this.State.Conversations.Add(conversation);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ConversationInfo>> All() => Task.FromResult<IReadOnlyList<ConversationInfo>>(this.State.Conversations.ToList());
}


/// <summary>
/// The actors' stored state - the generator finds it here
/// </summary>
[JsonSerializable(typeof(ConversationState))]
[JsonSerializable(typeof(ChatDirectoryState))]
public partial class ChatActorJsonContext : JsonSerializerContext;
