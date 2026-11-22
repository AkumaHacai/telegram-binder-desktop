using System;
using System.Linq;
using System.Text;

namespace TelegramBinder;

static class SelfTest
{
    internal static int Run()
    {
        Require(DesktopInput.TokenAtEnd("ответ\u00a0!Сап-12") == "!Сап-12");
        Require(DesktopInput.TokenAtEnd("https://example.org/a") == "");
        Require(DesktopInput.TokenAtEnd(".x\n") == "");
        Require(DesktopInput.TokenAtEnd("." + new string('a', 33)) == "");
        Require(DesktopInput.TokenAtEnd("𐐀 .𐐀Ⅳ") == ".𐐀Ⅳ");
        Require(DesktopInput.IsComposer("class MainWidget.class HistoryView::ChatWidget.class Ui::RpWidget.class Ui::InputField.class Ui::InputField::Inner"));
        Require(!DesktopInput.IsComposer("class MainWidget.class Dialogs::Widget.class Ui::InputField.class Ui::InputField::Inner"));
        TextTag[] tags = [new(0, 2, "**\\__"), new(2, 2, "custom-emoji://12345678901")];
        var encoded = ClipboardData.EncodeTags(tags);
        Require(encoded.AsSpan(0, 20).SequenceEqual(Convert.FromHexString("0000000200000000000000020000000A002A002A")));
        Require(ClipboardData.DecodeTags(encoded, 4).SequenceEqual(tags));
        Require(ClipboardData.DecodeTags(encoded[..^1], 4).Length == 0);
        Require(ClipboardData.DecodeTags(encoded, 3).Length == 0);
        var html = ClipboardData.HtmlBytes("<b>Привет</b>");
        var offsets = Encoding.UTF8.GetString(html).Split("\r\n");
        var start = int.Parse(offsets[3].Split(':')[1]);
        var end = int.Parse(offsets[4].Split(':')[1]);
        Require(Encoding.UTF8.GetString(html, start, end - start) == "<b>Привет</b>");
        Require(Locale.Messages.Values.All(values => values.Length == 4 && values.All(value => value.Length > 0)));
        foreach (var language in new[] { "ru", "en", "zh", "fa" })
        {
            Locale.Set(language);
            Require(Locale.Current == language);
            Require(Locale.ReadResource($"help-{language}.md").Contains("Ctrl+Shift+B"));
        }
        Locale.Set("unknown");
        Require(Locale.Current == "fa");
        return 0;
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Binder self-test failed.");
    }
}
