namespace Muthur.Cli.Infrastructure;

/// <summary>
/// The write phase of <c>kit install</c>, and its undo. Every byte the command writes goes through here, so a
/// write that fails for a reason nobody predicted can be put back rather than left half-installed — which is
/// the half T-8 refused to hide when it left the write phase unguarded.
/// </summary>
/// <remarks>
/// The journal is in memory and nothing more. A killed process, a power cut or a crash of the runtime itself
/// leaves whatever was on disk: surviving that needs an on-disk journal replayed by the next install, which is
/// a larger change with its own cleanup problem and is deliberately not here.
/// </remarks>
internal sealed class InstallTransaction(string root)
{
    /// <summary>A path as it was before we touched it: its bytes, or null when it did not exist.</summary>
    private readonly record struct Undo(string Path, byte[]? Content);

    // In the order things happened, with no de-duplication by path. Two entries may write the same path — that
    // is last-one-wins and not a collision — and replaying backwards restores the intermediate state and then
    // the original, ending correct. Keying by path would need a comparer that is case-insensitive on Windows
    // and case-sensitive elsewhere, and getting that wrong loses a file's prior state entirely.
    private readonly List<Undo> files = [];
    private readonly List<string> directories = [];

    /// <summary>
    /// The path it was last asked to write, so a failure can name the file rather than the command. Settable
    /// from outside because <c>Install</c> reads <c>.gitignore</c> and the project file before handing either
    /// to <see cref="Write"/>, and a read that throws there would otherwise blame the last entry written.
    /// </summary>
    internal string? Writing { get; set; }

    /// <summary>
    /// Applies one manifest entry. Returns created | updated | unchanged | kept. <c>create</c> exists because a
    /// brief stops being MUTHUR's the moment the founder edits it, and re-running the install must never take
    /// that edit away.
    /// </summary>
    internal string Apply(string path, string content, string? mode)
    {
        Writing = path;
        return Rendered(path, content, mode) is { } bytes ? WriteFile(path, bytes) : "kept";
    }

    /// <summary>
    /// Exactly what this install would leave in <paramref name="path"/>, or null when it would not write at
    /// all. One answer to "what does this produce", so the pre-flight's question — would this put bytes here —
    /// and the write itself cannot drift apart. They did: T-56's F3 refused a read-only file whose content
    /// already matched, because the pre-flight knew the mode and not the content.
    /// </summary>
    internal static string? Rendered(string path, string content, string? mode) => mode switch
    {
        "section" => SectionDocument(path, content),
        "create" => File.Exists(path) ? null : content.ReplaceLineEndings("\n"),
        _ => content.ReplaceLineEndings("\n"),
    };

    /// <summary>
    /// Writes a file verbatim — no mode, no line-ending normalisation, no status — and the only place a write
    /// happens. <c>.gitignore</c> and the project file use this rather than <see cref="Apply"/>: <c>Apply</c>
    /// normalises line endings, and routing the <c>.gitignore</c> append through it would rewrite a CRLF file
    /// the repository owns.
    /// </summary>
    internal void Write(string path, string content)
    {
        // Set before Record, so a failure inside journaling — reading the prior bytes of a locked file — still
        // names the file rather than the command. It is never cleared; on the happy path nothing reads it.
        Writing = path;
        Record(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>Forgets the journal. Called once the install has succeeded.</summary>
    internal void Commit()
    {
        files.Clear();
        directories.Clear();
    }

    /// <summary>Puts the repository back. Returns the paths, relative to the root, it could not restore.</summary>
    internal IReadOnlyList<string> Rollback()
    {
        var failed = new List<string>();

        for (var i = files.Count - 1; i >= 0; i--)
        {
            var undo = files[i];
            try
            {
                // A path with no prior bytes was not there before; Delete is a no-op when it is not there now.
                if (undo.Content is { } content) File.WriteAllBytes(undo.Path, content);
                else File.Delete(undo.Path);
            }
            catch (Exception)
            {
                // Swallowed, which is the opposite of what a catch usually earns. It is right here because the
                // very next line asks the filesystem what actually happened, so the exception carries nothing
                // the check does not already have.
            }
            if (!IsAsRecorded(undo)) failed.Add(Path.GetRelativePath(root, undo.Path));
        }

        for (var i = directories.Count - 1; i >= 0; i--)
        {
            try
            {
                // Non-recursively, and deepest first: a directory holding something we did not put there
                // refuses rather than being destroyed.
                if (Directory.Exists(directories[i])) Directory.Delete(directories[i]);
            }
            catch (Exception)
            {
                // Swallowed for the same reason as above: the check below is the answer, not the exception.
            }
            // No predicate helper here: still being there *is* the claim. A directory we created that survives
            // is residue whatever the reason, the non-empty case included.
            if (Directory.Exists(directories[i])) failed.Add(Path.GetRelativePath(root, directories[i]));
        }

        files.Clear();
        directories.Clear();
        return failed;
    }

    /// <summary>
    /// Whether the path is in the state the journal recorded — which is the question the failure list's own
    /// sentence asks, and not the question "did the call throw". A write that failed before it landed leaves
    /// the path exactly as journaled and the restore then fails for the same reason the write did, so blaming
    /// it would be a false alarm on the commonest failure there is.
    /// </summary>
    /// <remarks>
    /// The absent case asks <c>File.Exists</c> and says nothing about a directory: a directory that was there
    /// before us is not ours to account for, and one we created is in <c>directories</c>, where the second loop
    /// owns it. A read that throws answers false, so a file that genuinely cannot be inspected is reported
    /// rather than assumed clean — reachable only as a race, since <c>Record</c> cannot journal a locked file
    /// in the first place, and the conservative answer is the right one for a race.
    /// </remarks>
    private static bool IsAsRecorded(Undo undo)
    {
        try
        {
            if (undo.Content is not { } content) return !File.Exists(undo.Path);
            return File.Exists(undo.Path) && File.ReadAllBytes(undo.Path).SequenceEqual(content);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The prior state of <paramref name="path"/>, and of every directory above it that is not there yet. Both
    /// before anything is created or written, so a <c>CreateDirectory</c> that makes <c>a</c> and then fails on
    /// <c>a\b</c> still has both recorded.
    /// </summary>
    private void Record(string path)
    {
        var created = new List<string>();
        for (var directory = Path.GetDirectoryName(path);
             directory is { Length: > 0 } && !Directory.Exists(directory);
             directory = Path.GetDirectoryName(directory))
            created.Add(directory);

        // Shallowest first, the order Directory.CreateDirectory will create them in. An ancestor an earlier
        // entry already created exists by now, so it is not recorded twice.
        created.Reverse();
        directories.AddRange(created);

        files.Add(new Undo(path, File.Exists(path) ? File.ReadAllBytes(path) : null));
    }

    private string WriteFile(string path, string content)
    {
        content = content.ReplaceLineEndings("\n");
        var existed = File.Exists(path);
        if (existed && File.ReadAllText(path).ReplaceLineEndings("\n") == content) return "unchanged";
        Write(path, content);
        return existed ? "updated" : "created";
    }

    /// <summary>
    /// The whole document a section-mode entry would produce, existing content and all. For files the
    /// repository also owns (AGENTS.md): only a marked MUTHUR section inside them is maintained.
    /// </summary>
    private static string SectionDocument(string path, string content)
    {
        const string begin = "<!-- BEGIN MUTHUR -->";
        const string end = "<!-- END MUTHUR -->";
        var section = $"{begin}\n{content.ReplaceLineEndings("\n").Trim()}\n{end}\n";
        if (!File.Exists(path)) return section;

        var existing = File.ReadAllText(path).ReplaceLineEndings("\n");
        var start = existing.IndexOf(begin, StringComparison.Ordinal);
        var stop = existing.IndexOf(end, StringComparison.Ordinal);
        return start >= 0 && stop > start
            ? existing[..start] + section + existing[(stop + end.Length)..].TrimStart('\n')
            : existing.TrimEnd('\n') + "\n\n" + section;
    }
}
