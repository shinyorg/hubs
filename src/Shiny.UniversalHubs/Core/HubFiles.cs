namespace Shiny.UniversalHubs;

/// <summary>
/// Which way a file moves, from the client's side
/// </summary>
public enum HubTransferDirection
{
    Upload,
    Download
}


/// <summary>
/// A finished file transfer
/// </summary>
/// <param name="Direction">Upload or Download</param>
/// <param name="FileName">The file's name on the host</param>
/// <param name="BytesTransferred">How much moved</param>
/// <param name="Elapsed">How long it took</param>
public sealed record HubFileTransferResult(HubTransferDirection Direction, string FileName, long BytesTransferred, TimeSpan Elapsed);


/// <summary>
/// How far a file transfer has got
/// </summary>
/// <param name="BytesPerSecond">The average rate so far</param>
/// <param name="BytesToTransfer">The file's size, when known</param>
/// <param name="BytesTransferred">How much has moved</param>
public sealed record HubTransferProgress(long BytesPerSecond, long? BytesToTransfer, long BytesTransferred)
{
    /// <summary>
    /// 0 to 1, when the size is known
    /// </summary>
    public double? Fraction => this.BytesToTransfer is > 0 ? (double)this.BytesTransferred / this.BytesToTransfer.Value : null;
}


internal static class HubFilePaths
{
    /// <summary>
    /// The full path of <paramref name="name"/> inside the file transfer directory, or null when it is not a plain file
    /// name there (empty, a path, traversal). Every transport uses this.
    /// </summary>
    public static string? Resolve(HubFileTransferOptions ft, string? name)
    {
        if (String.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\']) >= 0 || Path.GetFileName(name) != name)
            return null;

        var root = Path.GetFullPath(ft.RootDirectory);
        var full = Path.GetFullPath(Path.Combine(root, name));
        return full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }
}


/// <summary>
/// A file transfer a client asked for, on any transport - <see cref="UniversalServerOptions.AuthorizeFile"/> decides
/// </summary>
/// <param name="Direction">Upload or Download, from the client's side</param>
/// <param name="FileName">The requested file name</param>
/// <param name="Size">The upload's length; null for downloads</param>
/// <param name="Client">The hub client asking, when the transport knows it</param>
public sealed record UniversalHubFileRequest(
    HubTransferDirection Direction,
    string FileName,
    long? Size,
    HubConnectedClient? Client
);
