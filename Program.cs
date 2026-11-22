using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace TelegramBinder;

static class Program
{
    internal static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TelegramBinderDesktop");

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--self-test")) return SelfTest.Run();
        Locale.Load(Path.Combine(DataDirectory, "language.txt"));
        using var once = new Mutex(true, @"Local\Telegram Binder Desktop", out var first);
        if (!first)
        {
            MessageBox.Show(Locale.Get("alreadyRunning"), "Telegram Binder");
            return 0;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        return app.Run(new Manager());
    }
}

sealed class Manager : Window
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const string Origin = "https://binder.local/";
    private const long MaxFile = 96L * 1024 * 1024;
    private const double CompactWidth = 444, EditorWidth = 790, EditorMinimumWidth = 700;
    private static bool AcrylicAvailable => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) && !SystemParameters.HighContrast;
    private readonly string directory = Program.DataDirectory;
    private readonly WebView2CompositionControl web = new();
    private readonly Forms.NotifyIcon tray = new();
    private readonly System.Drawing.Icon trayLogo;
    private DesktopInput? input;
    private nint handle;
    private bool exiting;
    private bool acrylic;
    private bool editorOpen;
    private double compactWidth = CompactWidth;
    private CoreWebView2? core;
    private string StatePath => Path.Combine(directory, "templates.json");

    internal Manager()
    {
        Title = "Telegram Binder";
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/TelegramBinder.ico"));
        using (var stream = Application.GetResourceStream(new Uri("Assets/TelegramBinderTray.ico", UriKind.Relative)).Stream)
            trayLogo = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);

        Content = web;
        Width = CompactWidth;
        Height = 540;
        MinWidth = 420;
        MinHeight = 390;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = !AcrylicAvailable;
        Background = Brushes.Transparent;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = AcrylicAvailable ? WindowChrome.GlassFrameCompleteThickness : new Thickness(0),
            UseAeroCaptionButtons = false,
        });
        web.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        Loaded += Initialize;
        StateChanged += (_, _) => Send(new { @event = "windowState", value = WindowState == WindowState.Maximized });
        Closing += (_, e) =>
        {
            if (exiting) return;
            e.Cancel = true;
            Hide();
        };
        Closed += (_, _) =>
        {
            input?.Dispose();
            web.Dispose();
            tray.Dispose();
            trayLogo.Dispose();
            Application.Current.Shutdown();
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Locale.Get("trayOpen"), null, (_, _) => OpenManager());
        menu.Items.Add(Locale.Get("about"), null, (_, _) => OpenAbout());
        menu.Items.Add(Locale.Get("quit"), null, (_, _) => { exiting = true; Close(); });
        tray.ContextMenuStrip = menu;
        tray.Icon = trayLogo;
        tray.Text = "Telegram Binder";
        tray.DoubleClick += (_, _) => OpenManager();
        tray.Visible = true;
    }

    private void OpenManager()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OpenAbout()
    {
        OpenManager();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
        var content = new StackPanel { Margin = new Thickness(14) };
        var logo = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/brand-wordmark.png"));
        content.Children.Add(new Border
        {
            Padding = new Thickness(16),
            Background = new SolidColorBrush(Color.FromRgb(245, 248, 253)),
            CornerRadius = new CornerRadius(6),
            Child = new Image { Source = logo, Height = 96, Stretch = Stretch.Uniform },
        });
        content.Children.Add(new TextBlock
        {
            Text = Locale.Get("version").Replace("{n}", version), Foreground = Brushes.White,
            Margin = new Thickness(0, 12, 0, 12),
        });
        content.Children.Add(new Button
        {
            Content = Locale.Get("close"), IsCancel = true, MinWidth = 80, Height = 28,
            HorizontalAlignment = HorizontalAlignment.Right,
        });
        new Window
        {
            Title = Locale.Get("about") + " · Telegram Binder", Owner = this, ShowInTaskbar = false,
            FlowDirection = Locale.Current == "fa" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.Height, Width = 320,
            Background = new SolidColorBrush(Color.FromRgb(32, 38, 46)), Content = content,
        }.ShowDialog();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        handle = new WindowInteropHelper(this).Handle;
        if (AcrylicAvailable)
        {
            const int darkMode = 20, cornerPreference = 33, borderColor = 34, systemBackdropType = 38;
            const int noBorderColor = -2;
            DwmSetWindowAttribute(handle, darkMode, 1, sizeof(int));
            DwmSetWindowAttribute(handle, cornerPreference, 2, sizeof(int));
            DwmSetWindowAttribute(handle, borderColor, noBorderColor, sizeof(int));
            acrylic = DwmSetWindowAttribute(handle, systemBackdropType, 3, sizeof(int)) == 0;
            if (acrylic) KeepBackdropAppearance();
            if (!acrylic) Background = new SolidColorBrush(Color.FromRgb(22, 26, 32));
        }
        // WPF runs the most recently added hook first, so the caption can override WindowChrome.
        HwndSource.FromHwnd(handle).AddHook(CaptionHitTest);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, in int value, int size);

    private void KeepBackdropAppearance()
    {
        // shortcut: this DWM appearance flag is used until WPF exposes an inactive acrylic setting.
        const int forceActiveAppearance = 15;
        var pointer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(pointer, 1);
            SetWindowCompositionAttribute(handle, new CompositionData(forceActiveAppearance, pointer, sizeof(int)));
        }
        catch (EntryPointNotFoundException) { }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CompositionData(int Attribute, nint Data, nuint Size);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(nint window, in CompositionData data);

    private nint CaptionHitTest(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int nonClientHitTest = 0x0084, caption = 2;
        if (message != nonClientHitTest) return 0;
        var screen = new Point((short)lParam, (short)((long)lParam >> 16));
        var local = PointFromScreen(screen);
        // Title bar space left of the web buttons uses the native window move gesture.
        if (local.Y is >= 6 and < 43 && local.X >= 6 && local.X < ActualWidth - 174)
        {
            handled = true;
            return caption;
        }
        return 0;
    }

    private async void Initialize(object sender, RoutedEventArgs e)
    {
        Loaded -= Initialize;
        try
        {
            var profile = Path.Combine(directory, "WebView");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            await web.EnsureCoreWebView2Async(environment);
            core = web.CoreWebView2;
            if (acrylic)
                await core.AddScriptToExecuteOnDocumentCreatedAsync(
                    "document.addEventListener('DOMContentLoaded', () => document.documentElement.dataset.backdrop = 'acrylic');");
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, args) =>
            {
                var resource = args.Request.Uri switch
                {
                    Origin + "index.html" => ("index.html", "text/html"),
                    Origin + "app.css" => ("app.css", "text/css"),
                    Origin + "app.js" => ("app.js", "text/javascript"),
                    Origin + "brand-mark.png" => ("brand-mark.png", "image/png"),
                    _ => ("", ""),
                };
                var data = resource.Item1.Length > 0
                    ? Assembly.GetExecutingAssembly().GetManifestResourceStream("TelegramBinder.Web." + resource.Item1)
                    : null;
                var contentType = resource.Item2.StartsWith("text/", StringComparison.Ordinal)
                    ? resource.Item2 + "; charset=utf-8" : resource.Item2;
                args.Response = data is not null
                    ? environment.CreateWebResourceResponse(data, 200, "OK",
                        "Content-Type: " + contentType + "\r\nX-Content-Type-Options: nosniff")
                    : environment.CreateWebResourceResponse(Stream.Null, 403, "Forbidden", "");
            };
            core.NavigationStarting += (_, args) => args.Cancel = args.Uri != Origin + "index.html";
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.WebMessageReceived += Message;
            input = new(handle, Dispatcher, Report, reply =>
            {
                OpenManager();
                Send(new { @event = "capture", value = reply });
            });
            core.Navigate(Origin + "index.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Locale.Get("startupError") + "\n\n"
                + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            exiting = true;
            Close();
        }
    }

    private void Report(string message)
    {
        tray.BalloonTipTitle = "Telegram Binder";
        tray.BalloonTipText = message;
        tray.ShowBalloonTip(3000);
    }

    private void Send(object response) => core?.PostWebMessageAsJson(JsonSerializer.Serialize(response, Json));

    private async void Message(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (args.Source != Origin + "index.html") return;
        var id = 0;
        try
        {
            using var message = JsonDocument.Parse(args.WebMessageAsJson);
            id = message.RootElement.GetProperty("id").GetInt32();
            var method = message.RootElement.GetProperty("method").GetString();
            var data = message.RootElement.GetProperty("value");
            if (method == "editor")
            {
                await ResizeForEditor(data.GetBoolean());
                Send(new { id, value = (object?)null });
                return;
            }
            var result = method switch
            {
                "preferences" => new { locale = Locale.Current, messages = Locale.Messages },
                "language" => SetLanguage(data.GetString()),
                "help" => Locale.ReadResource($"help-{Locale.Current}.md"),
                "load" => File.Exists(StatePath) ? ReadFile(StatePath) : null,
                "ready" => SaveCatalog(data, null),
                "save" => SaveCatalog(data.GetProperty("catalog"), data.GetProperty("state").GetRawText()),
                "import" => Import(),
                "export" => Export(data),
                "copy" => Copy(data),
                "clipboard" => ClipboardData.Read(handle),
                "hide" => HideManager(),
                "minimize" => SetWindowState(WindowState.Minimized),
                "toggleMaximized" => SetWindowState(WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized),
                _ => throw new InvalidOperationException(Locale.Get("unknownMethod")),
            };
            Send(new { id, value = result });
        }
        catch (Exception e)
        {
            Send(new { id, error = e.Message });
        }
    }


    private string SetLanguage(string? language)
    {
        if (!Locale.Supports(language)) throw new InvalidOperationException(Locale.Get("unknownMethod"));
        Directory.CreateDirectory(directory);
        WriteFile(Path.Combine(directory, "language.txt"), language!);
        Locale.Set(language);
        var menu = tray.ContextMenuStrip!;
        menu.Items[0].Text = Locale.Get("trayOpen");
        menu.Items[1].Text = Locale.Get("about");
        menu.Items[2].Text = Locale.Get("quit");
        return Locale.Current;
    }

    private async Task ResizeForEditor(bool open)
    {
        if (editorOpen == open) return;
        editorOpen = open;
        var area = Forms.Screen.FromHandle(handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var room = area.Width / dpi.DpiScaleX;
        MinHeight = open ? Math.Min(540, area.Height / dpi.DpiScaleY) : 390;
        if (WindowState == WindowState.Maximized)
        {
            MinWidth = open ? Math.Min(EditorMinimumWidth, room) : 420;
            return;
        }
        if (open) compactWidth = ActualWidth;
        var from = ActualWidth;
        var target = Math.Min(room, open ? Math.Max(from, EditorWidth) : compactWidth);
        if (open)
        {
            var right = PointFromScreen(new Point(area.Right, area.Top)).X;
            if (right < target) Left -= target - right;
        }
        MinWidth = 420;
        BeginAnimation(WidthProperty, null);
        Width = target;
        if (SystemParameters.ClientAreaAnimation && Math.Abs(target - from) > 1)
        {
            var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(open ? 260 : 210))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }, FillBehavior = FillBehavior.Stop,
            };
            var completed = new TaskCompletionSource();
            animation.Completed += (_, _) => completed.TrySetResult();
            BeginAnimation(WidthProperty, animation);
            await Task.WhenAny(completed.Task, Task.Delay(370));
        }
        if (editorOpen == open) MinWidth = open ? Math.Min(EditorMinimumWidth, room) : 420;
    }

    private object? SaveCatalog(JsonElement data, string? state)
    {
        var templates = data.Deserialize<Template[]>(Json) ?? throw new InvalidDataException(Locale.Get("emptyCatalog"));
        if (state is not null)
        {
            Directory.CreateDirectory(directory);
            WriteFile(StatePath, state);
        }
        input?.SetCatalog(templates);
        return null;
    }

    private object? Copy(JsonElement data)
    {
        var reply = data.Deserialize<ReplyText>(Json) ?? throw new InvalidDataException(Locale.Get("emptyReply"));
        ClipboardData.Write(handle, reply, true);
        return null;
    }

    private object? HideManager()
    {
        Hide();
        return null;
    }

    private object? SetWindowState(WindowState state)
    {
        WindowState = state;
        return null;
    }

    private string? Import()
    {
        var dialog = new OpenFileDialog { Title = Locale.Get("import"), Filter = "JSON (*.json)|*.json" };
        return dialog.ShowDialog(this) == true ? ReadFile(dialog.FileName) : null;
    }

    private bool Export(JsonElement data)
    {
        var dialog = new SaveFileDialog
        {
            Title = Locale.Get("export"), Filter = "JSON (*.json)|*.json", AddExtension = true,
            DefaultExt = ".json", FileName = "telegram-binder-backup.json",
        };
        if (dialog.ShowDialog(this) != true) return false;
        WriteFile(dialog.FileName, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        return true;
    }

    private static string ReadFile(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxFile) throw new InvalidDataException(Locale.Get("fileTooBig"));
        using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void WriteFile(string path, string data)
    {
        if (Utf8.GetByteCount(data) > MaxFile) throw new InvalidDataException(Locale.Get("fileTooBig"));
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew))
            {
                stream.Write(Utf8.GetBytes(data));
                stream.Flush(true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { File.Delete(temp); }
    }
}
