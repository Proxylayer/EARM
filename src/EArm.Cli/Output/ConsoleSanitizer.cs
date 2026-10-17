using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Diagnostics;

namespace EArm.Cli.Output;

/// <summary>
/// A <see cref="TextWriter"/> that makes everything written through it safe for a terminal and for a CI log, wrapped around standard output and
/// standard error for the whole run. Text that came from a template, a parameter file or the tenant (names, values, property keys, error
/// messages) reaches the writers raw; this is the one place that guarantees it cannot do harm:
/// <list type="bullet">
/// <item>every control character other than the line break the writers themselves emit (C0, DEL, C1 and so ESC, BEL and the OSC and CSI
/// introducers), the line and paragraph separators and the bidirectional overrides and isolates are written as a visible escape (a
/// backslash and u001b for ESC, a backslash and u202e for a right-to-left override; a lone carriage return as a backslash and r), see
/// <see cref="SafeText.Escape"/>;</item>
/// <item>text a CI log parser (Azure Pipelines, GitHub Actions) would run as a command is broken, whatever produced it and wherever it sits: the
/// second <c>#</c> of a <c>##vso[</c> or <c>##[</c> anywhere in a line is written as <see cref="SafeText.TokenEscape"/> (an Azure Pipelines agent
/// finds them in the middle of a line), and a <c>::</c> that is the first thing on a line, after any whitespace (a GitHub Actions runner trims
/// it), gets a leading apostrophe (<see cref="SafeText.NeutralizingMarker"/>) in front of it. Tabs are kept.</item>
/// </list>
/// Line breaks (<c>\n</c>, <c>\r\n</c>) pass unchanged, so tables and what-if text keep their layout; a break smuggled inside a value therefore
/// still starts a new line, but that line is neutralised like any other and cannot carry a control character. Output is handed to the
/// wrapped writer one line at a time; <see cref="Flush"/> releases a partial line (a prompt).
/// </summary>
internal sealed class ConsoleSanitizer : TextWriter
{
    /// <summary>
    /// The only escape sequences passed through: the colour codes the what-if text and <c>-o jsonc</c> / <c>yamlc</c> write themselves (set a foreground
    /// colour or bold red, reset). They can only recolour text; any other sequence, including every sequence that came from a template, is shown escaped.
    /// </summary>
    private static readonly string[] AllowedSequences = ["\u001b[0m", "\u001b[31m", "\u001b[32m", "\u001b[34m", "\u001b[35m", "\u001b[36m", "\u001b[90m", "\u001b[1;31m"];

    private readonly object _gate = new();
    private readonly TextWriter _inner;
    private readonly StringBuilder _escape = new();
    private readonly StringBuilder _buffer = new();
    private readonly StringBuilder _held = new();
    private bool _atLineStart = true;
    private bool _pendingCarriageReturn;

    public ConsoleSanitizer(TextWriter inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>The writer this one sanitizes for.</summary>
    public TextWriter Inner => _inner;

    /// <summary>The writer behind any <see cref="ConsoleSanitizer"/> layers of <paramref name="writer"/> (what a check such as "is this the console" should look at).</summary>
    public static TextWriter Unwrap(TextWriter writer)
    {
        while (writer is ConsoleSanitizer sanitizer)
        {
            writer = sanitizer._inner;
        }

        return writer;
    }

    public override Encoding Encoding => _inner.Encoding;

    public override IFormatProvider FormatProvider => _inner.FormatProvider;

    [AllowNull]
    public override string NewLine
    {
        get => _inner.NewLine;
        set => _inner.NewLine = value ?? Environment.NewLine;
    }

    public override void Write(char value)
    {
        lock (_gate)
        {
            Process(value);
        }
    }

    public override void Write(string? value)
    {
        if (value is null)
        {
            return;
        }

        lock (_gate)
        {
            foreach (char c in value)
            {
                Process(c);
            }
        }
    }

    public override void Write(char[] buffer, int index, int count)
    {
        lock (_gate)
        {
            for (int i = 0; i < count; i++)
            {
                Process(buffer[index + i]);
            }
        }
    }

    public override void Write(ReadOnlySpan<char> buffer)
    {
        lock (_gate)
        {
            foreach (char c in buffer)
            {
                Process(c);
            }
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_gate)
        {
            Write(value);
            Write(NewLine);
        }
    }

    public override void WriteLine()
    {
        Write(NewLine);
    }

    public override Task WriteAsync(char value)
    {
        Write(value);
        return Task.CompletedTask;
    }

    public override Task WriteAsync(string? value)
    {
        Write(value);
        return Task.CompletedTask;
    }

    public override Task WriteAsync(char[] buffer, int index, int count)
    {
        Write(buffer, index, count);
        return Task.CompletedTask;
    }

    public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return Task.CompletedTask;
    }

    public override Task WriteLineAsync(string? value)
    {
        WriteLine(value);
        return Task.CompletedTask;
    }

    public override Task WriteLineAsync()
    {
        WriteLine();
        return Task.CompletedTask;
    }

    public override void Flush()
    {
        lock (_gate)
        {
            // A prompt such as "(y/n): " has no line break: whatever is held back as a possible command prefix is released first.
            FlushEscape();
            ReleaseHeld();
            if (_pendingCarriageReturn)
            {
                _pendingCarriageReturn = false;
                _buffer.Append("\\r");
            }

            Drain();
            _inner.Flush();
        }
    }

    public override Task FlushAsync()
    {
        Flush();
        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Flush();
        }

        base.Dispose(disposing);
    }

    private void Process(char c)
    {
        if (_escape.Length > 0 || c == (char)0x1b)
        {
            ProcessEscape(c);
            return;
        }

        if (_pendingCarriageReturn)
        {
            _pendingCarriageReturn = false;
            if (c == '\n')
            {
                _buffer.Append("\r\n");
                Drain();
                _atLineStart = true;
                return;
            }

            // A carriage return that does not belong to a line break moves the cursor back and overwrites what was shown.
            Emit("\\r");
        }

        if (c == '\r')
        {
            ReleaseHeld();
            _pendingCarriageReturn = true;
            return;
        }

        if (c == '\n')
        {
            ReleaseHeld();
            _buffer.Append('\n');
            Drain();
            _atLineStart = true;
            return;
        }

        Emit(c == '\t' || !SafeText.NeedsEscape(c) ? c.ToString() : SafeText.EscapeOf(c));
    }

    /// <summary>
    /// Collects the characters after an ESC while they can still become one of the <see cref="AllowedSequences"/>; a complete one is written as
    /// it is, anything else becomes the visible escape of the ESC followed by the collected characters, processed as ordinary text.
    /// </summary>
    private void ProcessEscape(char c)
    {
        _escape.Append(c);
        string collected = _escape.ToString();
        if (Array.IndexOf(AllowedSequences, collected) >= 0)
        {
            if (_held.Length > 0)
            {
                // A colour code in the middle of what might become a log command (a "##" and then a colour code and then "vso[") is not output of the
                // what-if writer; it is shown as an escape, so it cannot split a token and hide it.
                RejectEscape(collected);
                return;
            }

            _escape.Clear();
            _buffer.Append(collected);
            return;
        }

        foreach (string allowed in AllowedSequences)
        {
            if (allowed.StartsWith(collected, StringComparison.Ordinal))
            {
                return;
            }
        }

        RejectEscape(collected);
    }

    private void FlushEscape()
    {
        if (_escape.Length > 0)
        {
            RejectEscape(_escape.ToString());
        }
    }

    private void RejectEscape(string collected)
    {
        _escape.Clear();
        Emit(SafeText.EscapeOf((char)0x1b));
        for (int i = 1; i < collected.Length; i++)
        {
            Process(collected[i]);
        }
    }

    /// <summary>
    /// Writes text that is not a line break. Every character goes through a small matcher for the CI command tokens (see
    /// <see cref="SafeText.MatchCommandToken"/>): while the characters held back can still become one they wait, a complete token is written
    /// neutralised, anything else is released unchanged.
    /// </summary>
    private void Emit(string text)
    {
        foreach (char c in text)
        {
            EmitChar(c);
        }
    }

    private void EmitChar(char c)
    {
        _held.Append(c);
        while (_held.Length > 0)
        {
            string held = _held.ToString();
            switch (SafeText.MatchCommandToken(held, _atLineStart))
            {
                case TokenMatch.Complete:
                    _buffer.Append(SafeText.NeutralizedToken(held));
                    _held.Clear();
                    _atLineStart = false;
                    return;
                case TokenMatch.Partial:
                    return;
                default:
                    // The first held character cannot start a token (or the rest would have matched): write it and look again at the others.
                    _buffer.Append(held[0]);
                    if (!char.IsWhiteSpace(held[0]))
                    {
                        _atLineStart = false;
                    }

                    _held.Remove(0, 1);
                    break;
            }
        }
    }

    private void ReleaseHeld()
    {
        if (_held.Length > 0)
        {
            string held = _held.ToString();
            _buffer.Append(held);
            _held.Clear();
            if (held.AsSpan().IndexOfAnyExcept(' ', '\t') >= 0)
            {
                _atLineStart = false;
            }
        }
    }
    /// <summary>Hands the buffered text to the underlying writer (one write per line instead of one per character).</summary>
    private void Drain()
    {
        if (_buffer.Length > 0)
        {
            _inner.Write(_buffer.ToString());
            _buffer.Clear();
        }
    }
}
