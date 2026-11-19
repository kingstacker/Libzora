using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// Conversation turn and stream chunk models.
/// </summary>
public sealed record AiConversationTurn(string Role, string Content)
{
    public IReadOnlyList<AiImageAttachment>? Images { get; init; }
}

public sealed record AiStreamChunk(string Text, string Reasoning);
