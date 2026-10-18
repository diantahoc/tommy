namespace Tommy.App.Rendering;

/// <summary>
/// A parsed markdown block, the unit the chat renderer maps to a purpose-built
/// Avalonia control. Records give value equality so the streaming diff can keep
/// completed blocks stable and only replace the trailing one that changed.
/// </summary>
public abstract record MdBlock;

/// <summary>A paragraph; <see cref="Markdown"/> is the inline markdown source.</summary>
public sealed record MdParagraph(string Markdown) : MdBlock;

public sealed record MdHeading(int Level, string Markdown) : MdBlock;

/// <summary>A fenced or indented code block (rendered monospace with a copy button).</summary>
public sealed record MdCode(string? Language, string Code) : MdBlock;

public sealed record MdQuote(string Markdown) : MdBlock;

public sealed record MdRule : MdBlock;

/// <summary>A bullet/numbered list; each item is inline markdown.</summary>
public sealed record MdList(IReadOnlyList<string> Items, bool Ordered) : MdBlock
{
    public bool Equals(MdList? other) =>
        other is not null && Ordered == other.Ordered && Items.SequenceEqual(other.Items);

    public override int GetHashCode() => HashCode.Combine(Ordered, Items.Count);
}
