using System;
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Threading;

namespace TelegramBinder;

record Variant(string Label, ReplyText Content);
record Template(string Command, string Description, bool Favorite, bool Enabled, Variant[] Variants);
record Choice(string Title, string Detail, Template? Template, Variant? Variant);
record Draft(nint Window, AutomationElement Element, TextPattern Pattern, TextPatternRange Selection, Rect Bounds)
{
    internal TextPatternRange Before()
    {
        var before = Pattern.DocumentRange;
        before.MoveEndpointByRange(TextPatternRangeEndpoint.End, Selection, TextPatternRangeEndpoint.Start);
        return before;
    }
    internal bool Caret => Selection.GetText(1).Length == 0;
}

sealed class DesktopInput : IDisposable
{
    private readonly nint owner;
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer timer;
    private readonly SuggestionWindow popup = new();
    private readonly HookProc hookProc;
    private readonly nint hook;
    private readonly Action<string> report;
    private readonly Action<ReplyText> captured;
    private readonly CompareInfo russian = CultureInfo.GetCultureInfo("ru-RU").CompareInfo;
    private readonly System.Collections.Generic.HashSet<uint> consumed = [];
    private Template[] catalog = [];
    private Probe? current;
    private Probe? displayed;
    private Probe? dismissed;
    private Choice[] choices = [];
    private Template? variantTemplate;
    private bool reading;
    private bool busy;
    private bool disposed;
    private uint processId;
    private bool telegramProcess;
    private long lastSpace;
    private long inputRevision;

    private record Probe(Draft Draft, bool Caret, string Before, string Token, string? AfterSpace);
    private const uint Space = 0x20, Enter = 0x0D, Tab = 0x09, Escape = 0x1B, Up = 0x26, Down = 0x28;
    private const uint Ctrl = 0x11, Shift = 0x10, Alt = 0x12;
    private const uint OwnInput = 0x54424752;

    internal DesktopInput(nint owner, Dispatcher dispatcher, Action<string> report, Action<ReplyText> captured)
    {
        this.owner = owner;
        this.dispatcher = dispatcher;
        this.report = report;
        this.captured = captured;
        popup.Chosen += index => _ = Choose(index);
        hookProc = Keyboard;
        hook = SetWindowsHookEx(13, hookProc, GetModuleHandle(null), 0);
        if (hook == 0) throw new InvalidOperationException(Locale.Get("hookError"));
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(65), DispatcherPriority.Background,
            (_, _) => _ = Refresh(), dispatcher);
    }

    internal void SetCatalog(Template[] value)
    {
        catalog = value;
        dismissed = null;
        Hide();
    }

    private static bool Enabled(nint window, out uint pid)
    {
        GetWindowThreadProcessId(window, out pid);
        return pid != 0;
    }

    private bool IsTelegram(nint window)
    {
        if (!Enabled(window, out var pid)) return false;
        if (processId == pid) return telegramProcess;
        processId = pid;
        telegramProcess = false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            telegramProcess = process.ProcessName.Equals("Telegram", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { }
        return telegramProcess;
    }

    internal static bool IsComposer(string id) =>
        id.EndsWith("class Ui::InputField::Inner", StringComparison.Ordinal)
        && (id.Contains(".class HistoryWidget.", StringComparison.Ordinal)
            || id.Contains(".class HistoryView::ChatWidget.", StringComparison.Ordinal)
            || id.Contains(".class HistoryView::RepliesWidget.", StringComparison.Ordinal)
            || id.Contains(".class HistoryView::ScheduledWidget.", StringComparison.Ordinal))
        && !id.Contains(".class Dialogs::", StringComparison.Ordinal);

    private static Draft? ReadDraft(nint window)
    {
        try
        {
            if (GetForegroundWindow() != window || !Enabled(window, out var pid)) return null;
            var element = AutomationElement.FocusedElement;
            if (element is null || element.Current.ProcessId != pid || !element.Current.HasKeyboardFocus
                || !IsComposer(element.Current.AutomationId)
                || !element.TryGetCurrentPattern(TextPattern.Pattern, out var value)) return null;
            var pattern = (TextPattern)value;
            var selection = pattern.GetSelection();
            var bounds = element.Current.BoundingRectangle;
            if (selection.Length != 1 || bounds.IsEmpty || bounds.Width < 10) return null;
            return new(window, element, pattern, selection[0], bounds);
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or COMException)
        { return null; }
    }

    internal static string TokenAtEnd(string text)
    {
        var start = text.Length;
        var size = 0;
        while (start > 0 && Rune.DecodeLastFromUtf16(text.AsSpan(0, start), out var rune, out var length) == OperationStatus.Done
            && (Rune.IsLetter(rune) || Rune.IsNumber(rune) || rune.Value is '_' or '-'))
        {
            if (++size > 32) return "";
            start -= length;
        }
        return start > 0 && text[start - 1] is '.' or '/' or '!' && (start == 1 || char.IsWhiteSpace(text[start - 2]))
            ? text[(start - 1)..] : "";
    }

    private static Probe? Inspect(nint window)
    {
        var draft = ReadDraft(window);
        if (draft is null) return null;
        var caret = draft.Caret;
        var before = caret ? draft.Before().GetText(-1) : "";
        var afterSpace = before.EndsWith(' ') ? TokenAtEnd(before[..^1]) : null;
        return new(draft, caret, before, TokenAtEnd(before), afterSpace);
    }

    private async Task Refresh()
    {
        if (reading || busy || disposed) return;
        var window = GetForegroundWindow();
        if (!IsTelegram(window))
        {
            current = null;
            dismissed = null;
            lastSpace = 0;
            Hide();
            return;
        }
        reading = true;
        try
        {
            var probe = await Task.Run(() => Inspect(window));
            if (disposed || busy || GetForegroundWindow() != window) return;
            current = probe;
            if (!Same(probe, dismissed)) dismissed = null;
            if (lastSpace != 0 && probe?.AfterSpace is { Length: > 1 } after
                && Environment.TickCount64 - lastSpace < 450)
            {
                lastSpace = 0;
                var exact = Exact(after);
                if (exact is not null)
                {
                    await Activate(probe with { Token = after + " " }, exact);
                    return;
                }
            }
            if (probe is null || (!Same(probe, displayed) && probe.Token.Length == 0) || Same(probe, dismissed))
            {
                Hide();
                return;
            }
            if (Same(probe, displayed))
            {
                popup.Place(probe.Draft.Bounds, window);
                return;
            }
            ShowCommands(probe);
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or COMException) { Hide(); }
        finally { reading = false; }
    }

    private static bool Same(Probe? first, Probe? second) => first is not null && second is not null
        && first.Draft.Window == second.Draft.Window && first.Draft.Element.Equals(second.Draft.Element)
        && first.Before == second.Before;

    private static string Name(string token) => token[1..].ToLowerInvariant();
    private Template? Exact(string token) => catalog.FirstOrDefault(t => t.Enabled && Name(t.Command) == Name(token));
    private static string Preview(ReplyText content) => content.Text[..Math.Min(90, content.Text.Length)].Replace('\n', ' ');

    private void ShowCommands(Probe probe)
    {
        if (probe.Token.Length == 0) { Hide(); return; }
        var name = Name(probe.Token);
        var matches = catalog.Where(t => t.Enabled && (Name(t.Command).StartsWith(name, StringComparison.Ordinal)
            || (name.Length > 1 && t.Description.Contains(name, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(t => t.Favorite)
            .ThenBy(t => t.Command, Comparer()).Take(8)
            .Select(t => new Choice(probe.Token[0] + t.Command[1..],
                t.Description.Length > 0 ? t.Description : Preview(t.Variants[0].Content), t, null))
            .ToArray();
        if (matches.Length == 0) { Hide(); return; }
        Show(probe, matches, null);
    }

    private System.Collections.Generic.Comparer<string> Comparer() =>
        System.Collections.Generic.Comparer<string>.Create((a, b) => russian.Compare(a, b, CompareOptions.IgnoreCase));

    private void Show(Probe probe, Choice[] items, Template? template)
    {
        displayed = probe;
        choices = items;
        variantTemplate = template;
        popup.Present(items, probe.Draft.Bounds, probe.Draft.Window);
    }

    private void Hide()
    {
        displayed = null;
        choices = [];
        variantTemplate = null;
        popup.Hide();
    }

    private async Task Choose(int index)
    {
        var probe = displayed;
        if (busy || probe is null || index < 0 || index >= choices.Length) return;
        var choice = choices[index];
        if (choice.Template is { } template) await Activate(probe, template);
        else if (choice.Variant is { } variant) await Replace(probe, variant.Content);
    }

    private async Task Activate(Probe probe, Template template)
    {
        if (busy) return;
        if (template.Variants.Length == 1) { await Replace(probe, template.Variants[0].Content); return; }
        busy = true;
        try
        {
            var latest = await Task.Run(() => Inspect(probe.Draft.Window));
            if (Same(latest, probe))
            {
                Show(probe, template.Variants.Select(v => new Choice(v.Label, Preview(v.Content), null, v)).ToArray(), template);
            }
            else Hide();
        }
        catch (Exception e) { report(e.Message); }
        finally { busy = false; }
    }

    private async Task Replace(Probe probe, ReplyText content)
    {
        busy = true;
        Hide();
        var revision = inputRevision;
        try
        {
            var latest = await Task.Run(() => Inspect(probe.Draft.Window));
            if (latest is null || !Same(latest, probe) || !latest.Caret) return;
            var draft = latest.Draft;
            using var backup = ClipboardData.Snapshot.Capture(owner);
            uint? pasted = null;
            var sent = false;
            try
            {
                // The selection is checked again after creating the clipboard backup.
                var verified = await Task.Run(() =>
                {
                    var fresh = Inspect(draft.Window);
                    if (fresh is null || !Same(fresh, probe) || !fresh.Caret) return false;
                    var range = fresh.Draft.Selection.Clone();
                    range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -probe.Token.Length);
                    if (range.GetText(-1) != probe.Token) return false;
                    range.Select();
                    var selection = fresh.Draft.Pattern.GetSelection();
                    return selection.Length == 1 && selection[0].GetText(-1) == probe.Token;
                });
                if (!verified || GetForegroundWindow() != draft.Window || revision != inputRevision) return;
                pasted = ClipboardData.Write(owner, content, false, backup);
                var stillSelected = await Task.Run(() =>
                {
                    var target = ReadDraft(draft.Window);
                    return target is not null && target.Element.Equals(draft.Element)
                        && target.Selection.GetText(-1) == probe.Token;
                });
                if (stillSelected && GetForegroundWindow() == draft.Window && revision == inputRevision
                    && !Modifiers() && ClipboardData.GetClipboardSequenceNumber() == pasted)
                {
                    SendChord(0x56);
                    sent = true;
                    await Task.Delay(220);
                }
            }
            finally
            {
                try { if (pasted is uint sequence) backup.Restore(owner, sequence); }
                finally
                {
                    if (!sent && revision == inputRevision)
                    {
                        await Task.Run(() =>
                        {
                            var target = ReadDraft(draft.Window);
                            if (target is not null && target.Element.Equals(draft.Element)
                                && target.Selection.GetText(-1) == probe.Token) draft.Selection.Select();
                        });
                    }
                }
            }
        }
        catch (Exception e) { report(e.Message); }
        finally { busy = false; }
    }

    private async Task Capture(nint window)
    {
        if (busy) return;
        busy = true;
        Hide();
        try
        {
            var draft = await Task.Run(() => ReadDraft(window));
            if (draft is null) return;
            for (var i = 0; i < 60 && Modifiers(); i++) await Task.Delay(12);
            if (Modifiers() || GetForegroundWindow() != window) return;
            using var backup = ClipboardData.Snapshot.Capture(owner);
            var selected = await Task.Run(() =>
            {
                var fresh = ReadDraft(window);
                if (fresh is null || !fresh.Element.Equals(draft.Element)) return null;
                fresh.Pattern.DocumentRange.Select();
                var actual = fresh.Pattern.GetSelection();
                return actual.Length == 1 && actual[0].GetText(1).Length > 0 ? fresh : null;
            });
            if (selected is null || GetForegroundWindow() != window) return;
            ReplyText? result = null;
            uint? copied = null;
            try
            {
                if (ClipboardData.GetClipboardSequenceNumber() != backup.Sequence) return;
                SendChord(0x43);
                for (var i = 0; i < 30 && ClipboardData.GetClipboardSequenceNumber() == backup.Sequence; i++)
                    await Task.Delay(15);
                if (ClipboardData.GetClipboardSequenceNumber() != backup.Sequence
                    && Enabled(ClipboardData.GetClipboardOwner(), out var clipboardPid)
                    && Enabled(window, out var targetPid) && clipboardPid == targetPid)
                {
                    copied = ClipboardData.GetClipboardSequenceNumber();
                    result = ClipboardData.Read(owner);
                }
            }
            finally
            {
                if (copied is uint sequence) backup.Restore(owner, sequence);
                await Task.Run(() =>
                {
                    var fresh = ReadDraft(window);
                    if (fresh is not null && fresh.Element.Equals(selected.Element)) selected.Selection.Select();
                });
            }
            if (result is not null && !string.IsNullOrWhiteSpace(result.Text)) captured(result);
            else report(Locale.Get("captureFailed"));
        }
        catch (Exception e) { report(e.Message); }
        finally { busy = false; }
    }

    private nint Keyboard(int code, nint message, nint data)
    {
        if (code < 0) return CallNextHookEx(hook, code, message, data);
        var key = Marshal.PtrToStructure<KeyHook>(data);
        if (key.Extra == OwnInput) return CallNextHookEx(hook, code, message, data);
        if (message is 0x101 or 0x105 && consumed.Remove(key.Key)) return 1;
        if (message != 0x100) return CallNextHookEx(hook, code, message, data);
        var window = GetForegroundWindow();
        var cached = current;
        if (cached is not null && cached.Draft.Window == window)
        {
            if (key.Key == 0x42 && DownKey(Ctrl) && DownKey(Shift) && !DownKey(Alt) && !WindowsKey())
                return Consume(key.Key, () => _ = Capture(window));
            if (!Modifiers())
            {
                if ((busy || lastSpace != 0 && Environment.TickCount64 - lastSpace < 180)
                    && key.Key is Enter or Tab && (busy || cached.Token.Length > 0))
                    return Consume(key.Key, () => { });
                if (key.Key == Space && cached.Caret)
                {
                    // Read the actual token after Telegram inserts the physical space.
                    if (!busy && variantTemplate is null && catalog.Any(t => t.Enabled)) lastSpace = Environment.TickCount64;
                }
                if (displayed is { } hint && hint.Draft.Window == window)
                {
                    if (key.Key is Up or Down) return Consume(key.Key, () => popup.Step(key.Key == Down ? 1 : -1), true);
                    if (key.Key is Enter or Tab) return Consume(key.Key, () => _ = Choose(popup.Selected));
                    if (key.Key == Escape) return Consume(key.Key, () => { dismissed = hint; Hide(); });
                    if (key.Key is not (0x10 or 0x11 or 0x12)) dispatcher.BeginInvoke(Hide, DispatcherPriority.Input);
                }
            }
        }
        if (key.Key is not (Ctrl or Shift or Alt or 0xA0 or 0xA1 or 0xA2 or 0xA3))
        {
            inputRevision++;
            if (key.Key != Space) lastSpace = 0;
        }
        return CallNextHookEx(hook, code, message, data);
    }

    private nint Consume(uint key, Action action, bool repeats = false)
    {
        if (consumed.Add(key) || repeats) dispatcher.BeginInvoke(action, DispatcherPriority.Input);
        return 1;
    }

    private static bool DownKey(uint key) => (GetAsyncKeyState((int)key) & 0x8000) != 0;
    private static bool WindowsKey() => DownKey(0x5B) || DownKey(0x5C);
    private static bool Modifiers() => DownKey(Ctrl) || DownKey(Shift) || DownKey(Alt) || WindowsKey();

    private static void SendChord(ushort key)
    {
        static Input Key(ushort key, bool up = false) => new()
            { Type = 1, Data = new InputUnion { Keyboard = new KeyInput { Key = key, Flags = up ? 2u : 0u, Extra = OwnInput } } };
        Input[] inputs = [Key((ushort)Ctrl), Key(key), Key(key, true), Key((ushort)Ctrl, true)];
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new InvalidOperationException(Locale.Get("inputBlocked"));
    }

    public void Dispose()
    {
        disposed = true;
        timer.Stop();
        UnhookWindowsHookEx(hook);
        popup.Close();
    }

    [StructLayout(LayoutKind.Sequential)] private struct KeyHook
        { public uint Key, Scan, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Input
        { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public KeyInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyInput
        { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
        { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    private delegate nint HookProc(int code, nint message, nint data);
    [DllImport("user32")] private static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint thread);
    [DllImport("user32")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32")] private static extern nint GetForegroundWindow();
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
