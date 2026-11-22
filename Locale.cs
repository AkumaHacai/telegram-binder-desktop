using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace TelegramBinder;

static class Locale
{
    private static readonly string[] Languages = ["ru", "en", "zh", "fa"];
    internal static readonly Dictionary<string, string[]> Messages =
        JsonSerializer.Deserialize<Dictionary<string, string[]>>(ReadResource("locales.json"))!;
    internal static string Current { get; private set; } = "ru";

    internal static bool Supports(string? language) => Array.IndexOf(Languages, language) >= 0;
    internal static string Get(string key) => Messages[key][Array.IndexOf(Languages, Current)];

    internal static void Set(string? language)
    {
        if (Supports(language)) Current = language!;
    }

    internal static void Load(string path)
    {
        try { if (File.Exists(path)) Set(File.ReadAllText(path).Trim()); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("TelegramBinder.Web." + name)
            ?? throw new FileNotFoundException(name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
