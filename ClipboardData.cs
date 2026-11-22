using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TelegramBinder;

record TextTag(int Offset, int Length, string Id);
record ReplyText(string Text, TextTag[] Tags, string? Html = null);

static class ClipboardData
{
    private const uint UnicodeText = 13;
    private static readonly uint TelegramText = RegisterClipboardFormat("application/x-td-field-text");
    private static readonly uint TelegramTags = RegisterClipboardFormat("application/x-td-field-tags");
    private static readonly uint Html = RegisterClipboardFormat("HTML Format");
    private static readonly uint ExcludeFromHistory = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly Encoding BigEndian = Encoding.BigEndianUnicode;

    internal static byte[] EncodeTags(TextTag[] tags)
    {
        using var result = new MemoryStream();
        void Write(int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            result.Write(bytes);
        }
        Write(tags.Length);
        foreach (var tag in tags)
        {
            var id = BigEndian.GetBytes(tag.Id);
            Write(tag.Offset);
            Write(tag.Length);
            Write(id.Length);
            result.Write(id);
        }
        return result.ToArray();
    }

    internal static TextTag[] DecodeTags(byte[] data, int textLength)
    {
        if (data.Length < 4) return [];
        var count = BinaryPrimitives.ReadInt32BigEndian(data);
        if (count <= 0 || count > textLength) return [];
        var tags = new List<TextTag>(count);
        var end = 0;
        var offset = 4;
        for (var i = 0; i < count; i++)
        {
            if (data.Length - offset < 12) return [];
            var start = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
            var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset + 4));
            var size = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset + 8));
            offset += 12;
            if (start < end || start > textLength || length <= 0 || length > textLength - start
                || size <= 0 || size > 8192 || size % 2 != 0 || size > data.Length - offset) return [];
            tags.Add(new(start, length, BigEndian.GetString(data, offset, size)));
            offset += size;
            end = start + length;
        }
        return tags.ToArray();
    }

    internal static ReplyText Read(nint window)
    {
        using var open = new Open(window);
        var native = ReadBytes(TelegramText, 80_000);
        var bytes = native is null ? ReadBytes(UnicodeText, 80_002) : null;
        var text = native is not null
            ? Encoding.UTF8.GetString(native).TrimEnd('\0')
            : bytes is not null ? Encoding.Unicode.GetString(bytes).Split('\0', 2)[0] : "";
        var tags = ReadBytes(TelegramTags, 8 * 1024 * 1024);
        return new(text, native is not null && tags is not null ? DecodeTags(tags, text.Length) : []);
    }

    private static byte[]? ReadBytes(uint format, int limit)
    {
        var handle = GetClipboardData(format);
        if (handle == 0) return null;
        var size = GlobalSize(handle);
        if (size > (nuint)limit) throw new InvalidOperationException(Locale.Get("clipboardTooBig"));
        var ptr = GlobalLock(handle);
        if (ptr == 0) return null;
        try
        {
            var bytes = new byte[(int)size];
            Marshal.Copy(ptr, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { GlobalUnlock(handle); }
    }

    internal static uint Write(nint window, ReplyText reply, bool html, Snapshot? backup = null)
    {
        using var data = new Snapshot();
        data.Add(UnicodeText, Encoding.Unicode.GetBytes(reply.Text.Replace("\n", "\r\n") + "\0"));
        if (reply.Tags.Length > 0)
        {
            data.Add(TelegramText, Encoding.UTF8.GetBytes(reply.Text));
            data.Add(TelegramTags, EncodeTags(reply.Tags));
        }
        if (html && !string.IsNullOrEmpty(reply.Html)) data.Add(Html, HtmlBytes(reply.Html));
        if (backup is not null) data.Add(ExcludeFromHistory, [0]);
        using (new Open(window))
        {
            if (backup is not null && GetClipboardSequenceNumber() != backup.Sequence)
                throw new InvalidOperationException(Locale.Get("clipboardChanged"));
            try { data.Publish(); }
            catch
            {
                backup?.Publish();
                throw;
            }
        }
        return GetClipboardSequenceNumber();
    }

    internal static byte[] HtmlBytes(string fragment)
    {
        const string before = "<html><body><!--StartFragment-->";
        const string after = "<!--EndFragment--></body></html>";
        const string template = "Version:1.0\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        var start = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, 0, 0, 0, 0).Length;
        var fragmentStart = start + before.Length;
        var fragmentEnd = fragmentStart + Encoding.UTF8.GetByteCount(fragment);
        var header = string.Format(System.Globalization.CultureInfo.InvariantCulture, template,
            start, fragmentEnd + after.Length, fragmentStart, fragmentEnd);
        return Encoding.UTF8.GetBytes(header + before + fragment + after + "\0");
    }

    internal sealed class Snapshot : IDisposable
    {
        private readonly List<(uint Format, nint Handle)> entries = [];

        internal static Snapshot Capture(nint window)
        {
            using var open = new Open(window);
            var snapshot = new Snapshot();
            try
            {
                uint format = 0;
                while ((format = EnumClipboardFormats(format)) != 0)
                {
                    // A private/owner-drawn object cannot be copied independently of its producer.
                    if (format == 0x0080 || format is >= 0x0200 and <= 0x03FF)
                        throw new InvalidOperationException(Locale.Get("clipboardBackup"));
                    var handle = GetClipboardData(format);
                    var copy = handle == 0 ? 0 : OleDuplicateData(handle, (ushort)format, 0);
                    if (copy == 0) throw new InvalidOperationException(Locale.Get("clipboardBackup"));
                    snapshot.entries.Add((format, copy));
                }
                snapshot.Sequence = GetClipboardSequenceNumber();
                return snapshot;
            }
            catch { snapshot.Dispose(); throw; }
        }

        internal uint Sequence { get; private set; }

        internal void Add(uint format, byte[] bytes)
        {
            var handle = GlobalAlloc(0x42, (nuint)bytes.Length);
            if (handle == 0) throw new OutOfMemoryException();
            entries.Add((format, handle));
            var ptr = GlobalLock(handle);
            if (ptr == 0) throw new OutOfMemoryException();
            try { Marshal.Copy(bytes, 0, ptr, bytes.Length); }
            finally { GlobalUnlock(handle); }
        }

        internal void Restore(nint window, uint sequence)
        {
            using var open = new Open(window);
            if (GetClipboardSequenceNumber() == sequence) Publish();
        }

        internal void Publish()
        {
            if (!EmptyClipboard()) throw new InvalidOperationException(Locale.Get("clipboardBusy"));
            for (var i = 0; i < entries.Count; i++)
            {
                var (format, handle) = entries[i];
                if (SetClipboardData(format, handle) == 0)
                    throw new InvalidOperationException(Locale.Get("clipboardWrite"));
                entries[i] = (format, 0);
            }
        }

        public void Dispose()
        {
            foreach (var (format, handle) in entries)
            {
                if (handle == 0) continue;
                if (format is 2 or 9 or 0x82) DeleteObject(handle);
                else if (format is 14 or 0x8E) DeleteEnhMetaFile(handle);
                else
                {
                    if (format is 3 or 0x83)
                    {
                        var ptr = GlobalLock(handle);
                        if (ptr != 0)
                        {
                            DeleteMetaFile(Marshal.ReadIntPtr(ptr, IntPtr.Size == 8 ? 16 : 12));
                            GlobalUnlock(handle);
                        }
                    }
                    GlobalFree(handle);
                }
            }
        }
    }

    private sealed class Open : IDisposable
    {
        internal Open(nint window)
        {
            for (var i = 0; i < 5; i++)
            {
                if (OpenClipboard(window)) return;
                Thread.Sleep(10);
            }
            throw new InvalidOperationException(Locale.Get("clipboardBusy"));
        }
        public void Dispose() => CloseClipboard();
    }

    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32")] private static extern bool OpenClipboard(nint window);
    [DllImport("user32")] private static extern bool CloseClipboard();
    [DllImport("user32")] private static extern bool EmptyClipboard();
    [DllImport("user32")] private static extern nint GetClipboardData(uint format);
    [DllImport("user32")] private static extern nint SetClipboardData(uint format, nint data);
    [DllImport("user32")] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32")] internal static extern nint GetClipboardOwner();
    [DllImport("kernel32")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32")] private static extern nuint GlobalSize(nint memory);
    [DllImport("kernel32")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32")] private static extern nint GlobalFree(nint memory);
    [DllImport("ole32")] private static extern nint OleDuplicateData(nint handle, ushort format, uint flags);
    [DllImport("gdi32")] private static extern bool DeleteObject(nint handle);
    [DllImport("gdi32")] private static extern bool DeleteEnhMetaFile(nint handle);
    [DllImport("gdi32")] private static extern bool DeleteMetaFile(nint handle);
}
