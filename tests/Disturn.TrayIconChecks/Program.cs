using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

// Exercise the published presentation code offscreen: no MainWindow, NotifyIcon,
// audio controller, startup registration or real persistence is constructed.
var publish = Path.GetFullPath(args[0]);
// ResolveBrandingAsset uses the executable's base directory, just as in Disturn.
// Supply the real published ICO in this isolated runner's ignored output folder.
var fixtureAssets = Path.Combine(AppContext.BaseDirectory, "Assets");
Directory.CreateDirectory(fixtureAssets);
File.Copy(Path.Combine(publish, "Assets", "Disturn.ico"), Path.Combine(fixtureAssets, "Disturn.ico"), overwrite: true);
AssemblyLoadContext.Default.Resolving += (_, name) => {
    var file = Path.Combine(publish, name.Name + ".dll");
    return File.Exists(file) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(file) : null;
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(publish, "Disturn.dll"));
var type = assembly.GetType("Disturn.TrayIconService", true)!;
var service = RuntimeHelpers.GetUninitializedObject(type);
void Field(string name, object value) => type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, value);
Field("_selectedAccentColor", "#4B9FFF");
var create = type.GetMethod("CreateCurrentIcon", BindingFlags.Instance | BindingFlags.NonPublic)!;
var size = (int)type.GetMethod("GetTrayIconSize", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, null)!;
var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}
byte[] ReadAndDestroy(IntPtr icon)
{
    Check(icon != IntPtr.Zero, "Icon creation failed");
    try
    {
        if (!Native.GetIconInfo(icon, out var info)) throw new Exception("GetIconInfo failed");
        try
        {
            Native.GetObjectW(info.Color, Marshal.SizeOf<Native.Bitmap>(), out var bitmap);
            Check(bitmap.Width == size && bitmap.Height == size, "Unexpected icon dimensions");
            var header = new Native.Header { Size = 40, Width = size, Height = -size, Planes = 1, Bits = 32 };
            var pixels = new byte[size * size * 4];
            var dc = Native.GetDC(IntPtr.Zero);
            try { if (Native.GetDIBits(dc, info.Color, 0, (uint)size, pixels, ref header, 0) == 0) throw new Exception("GetDIBits failed"); }
            finally { Native.ReleaseDC(IntPtr.Zero, dc); }
            return pixels;
        }
        finally { Native.DeleteObject(info.Color); Native.DeleteObject(info.Mask); }
    }
    finally { Native.DestroyIcon(icon); }
}
byte[] Pixels(string group, int volume, string mode, bool systemLight)
{
    Field("_selectedGroupName", group);
    Field("_selectedGroupVolume", volume);
    Field("_appearance", mode);
    Field("_systemUsesLightTheme", systemLight);
    return ReadAndDestroy((IntPtr)create.Invoke(service, null)!);
}
var startup = new Native.Startup { Version = 1 };
if (Native.GdiplusStartup(out var token, ref startup, IntPtr.Zero) != 0) throw new Exception("GDI+ startup failed");
try
{
    if (args.Contains("--capture-baseline"))
    {
        Directory.CreateDirectory("artifacts/verification/tray-mute");
        foreach (var volume in new[] { 0, 64, 100 })
            File.WriteAllBytes($"artifacts/verification/tray-mute/baseline-{size}-{volume}.bgra",
                Pixels("Fixture", volume, "Dark", false));
        Console.WriteLine("Captured existing unmuted icon pixels before rebuilding.");
        return;
    }
    var original = ReadAndDestroy(Native.LoadImageW(IntPtr.Zero, Path.Combine(publish, "Assets", "Disturn.ico"), 1, size, size, 0x10));
    Check(ReadAndDestroy((IntPtr)create.Invoke(service, null)!).SequenceEqual(original), "Initial empty selection must use ICO");
    foreach (var mode in new[] { "Automatic", "Light", "Dark" })
    foreach (var systemLight in new[] { false, true })
    {
        Check(Pixels(null, 100, mode, systemLight).SequenceEqual(original), "Empty selection was recolored or rendered as percentage");
        Check(Pixels("", 64, mode, systemLight).SequenceEqual(original), "Empty group name did not use logo");
        Check(Pixels(" ", 0, mode, systemLight).SequenceEqual(original), "Invalid group name did not use logo");
    }
    Console.WriteLine($"PASS: {size}x{size} original ICO pixels preserved for empty selection in all modes/system themes.");
    foreach (var volume in new[] { 0, 64, 100 })
    {
        var white = Pixels("Fixture", volume, "Dark", false);
        var baselinePath = $"artifacts/verification/tray-mute/baseline-{size}-{volume}.bgra";
        if (File.Exists(baselinePath))
            Check(white.SequenceEqual(File.ReadAllBytes(baselinePath)), "Unmuted rendering changed from the pre-change publish");
        var black = Pixels("Fixture", volume, "Light", true);
        Check(!white.SequenceEqual(original), "Selected group still shows logo");
        var glyphs = 0;
        for (var i = 0; i < white.Length; i += 4)
        {
            Check(white[i+3] == black[i+3], "Alpha coverage/layout changed");
            if (white[i+3] > 0 && white[i] > 0 && white[i] == white[i+1] && white[i+1] == white[i+2])
            {
                Check(black[i] == 0 && black[i+1] == 0 && black[i+2] == 0, "Light glyph is not black");
                glyphs++;
            }
            else Check(white.AsSpan(i,4).SequenceEqual(black.AsSpan(i,4)), "Accent/transparency changed");
        }
        Check(glyphs > 0, "No percentage pixels");
        Check(Pixels("Fixture", volume, "Automatic", true).SequenceEqual(black), "Automatic/light mismatch");
        Check(Pixels("Fixture", volume, "Automatic", false).SequenceEqual(white), "Automatic/dark mismatch");
        Check(Pixels(null, volume, "Automatic", false).SequenceEqual(original), "Clearing/deleting selection did not return to logo");
        Console.WriteLine($"PASS: select -> percentage {volume} -> clear uses logo; appearance and alpha/pill preserved.");
    }
    Check(!Pixels("Fixture", 64, "Dark", false).SequenceEqual(Pixels("Fixture", 100, "Dark", false)), "Group volume update did not change icon");
    using var process = Process.GetCurrentProcess();
    var gdiBefore = Native.GetGuiResources(process.Handle, 0);
    var userBefore = Native.GetGuiResources(process.Handle, 1);
    for (var index = 0; index < 100; index++)
    {
        Pixels(null, index, "Light", true);
        Field("_selectedGroupMuted", true);
        Pixels("Fixture", index, "Dark", false);
        Field("_selectedGroupMuted", false);
        Pixels("Fixture", index, "Dark", false);
    }
    var gdiAfter = Native.GetGuiResources(process.Handle, 0);
    var userAfter = Native.GetGuiResources(process.Handle, 1);
    Check(gdiAfter == gdiBefore && userAfter == userBefore, "Native object count increased across transitions");
    Console.WriteLine($"PASS: 100 logo/percentage cycles, GDI objects {gdiBefore}->{gdiAfter}, USER objects {userBefore}->{userAfter}.");
    var muteTooltip = type.GetMethod("BuildTooltipText", BindingFlags.Static | BindingFlags.NonPublic)!;
    var mutedText = (string)muteTooltip.Invoke(null, new object[] { "Fixture Media", 64, true })!;
    Check(mutedText == "Disturn \u2014 Fixture Media | Muted | Volume: 64%", "Muted tooltip loses volume or mute state");
    var longMuted = (string)muteTooltip.Invoke(null, new object[] { new string('M', 200), 100, true })!;
    Check(longMuted.Length <= 127 && longMuted.Contains(" | Muted | Volume: 100%"),
        "Long muted tooltip exceeds the tray limit or loses volume");
    Check((string)muteTooltip.Invoke(null, new object[] { null, 100, true })! == "No group selected",
        "No-selection tooltip must not show a stale mute state");
    Check((string)muteTooltip.Invoke(null, new object[] { "Media", 75, false })! ==
        "Disturn \u2014 Media | Volume: 75%", "Unmuted tooltip format mismatch");
    var renderAtSize = type.GetMethod("RenderGroupIconAtSize", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var actualSize = size;
    var retainedVolume = (int)type.GetField("_selectedGroupVolume", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
    foreach (var canvasSize in new[] { 16, 20, 24, 32, 48 })
    {
        size = canvasSize;
        var mutedByAppearance = new Dictionary<string, byte[]>();
        foreach (var volume in new[] { 0, 50, 64, 100 })
        foreach (var mode in new[] { "Dark", "Light" })
        {
            Field("_appearance", mode);
            Field("_selectedGroupMuted", false);
            var numberPixels = ReadAndDestroy((IntPtr)renderAtSize.Invoke(service, new object[] { volume, size })!);
            Field("_selectedGroupMuted", true);
            var mutedPixels = ReadAndDestroy((IntPtr)renderAtSize.Invoke(service, new object[] { volume, size })!);
            Check(!numberPixels.SequenceEqual(mutedPixels), "Mute glyph did not replace the number");
            if (mutedByAppearance.TryGetValue(mode, out var priorMute))
                Check(MuteGlyphPixels(mutedPixels, size).SequenceEqual(MuteGlyphPixels(priorMute, size)),
                    "Mute glyph dimensions or pixels depend on retained volume");
            else mutedByAppearance.Add(mode, mutedPixels);
            var glyphPixels = 0;
            var pillPixels = 0;
            for (var i = 0; i < mutedPixels.Length; i += 4)
            {
                var accent = numberPixels[i+3] > 0 && numberPixels[i] != numberPixels[i+1];
                if (accent)
                {
                    Check(numberPixels.AsSpan(i,4).SequenceEqual(mutedPixels.AsSpan(i,4)),
                        "Mute changed pill geometry, color, or alpha");
                    pillPixels++;
                }
                if (mutedPixels[i+3] > 0 && mutedPixels[i] == mutedPixels[i+1] && mutedPixels[i+1] == mutedPixels[i+2])
                {
                    Check((mode == "Dark" ? mutedPixels[i] >= 250 : mutedPixels[i] == 0), $"Mute glyph ignores {mode} mode: {mutedPixels[i]},{mutedPixels[i+1]},{mutedPixels[i+2]},{mutedPixels[i+3]} at {i/4} ({size}px)");
                    glyphPixels++;
                }
            }
            Check(glyphPixels > 0 && pillPixels > 0, "Mute symbol or accent is missing");
            Check((int)type.GetField("_selectedGroupVolume", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)! == retainedVolume,
                "Rendering mute changed the stored volume");
            if (volume == 64 && mode == "Dark")
                SavePreview(mutedPixels, size, $"artifacts/verification/tray-mute/muted-{size}.png");
        }
        Console.WriteLine($"PASS: {size}x{size} mute glyph, original pill, light/dark colors, retained volume.");
    }
    size = actualSize;
    Field("_selectedGroupMuted", true);
    Check(Pixels(null, 64, "Light", true).SequenceEqual(original), "Stale muted state replaced the no-selection logo");
    Field("_selectedGroupMuted", false);
    var groupType = assembly.GetType("Disturn.GroupItem", true)!;
    var group = Activator.CreateInstance(groupType, new object[] { "Fixture", 64, null, false })!;
    groupType.GetProperty("IsMuted")!.SetValue(group, true);
    Check((int)groupType.GetProperty("VolumePercent")!.GetValue(group)! == 64 &&
        (string)groupType.GetProperty("MuteLabel")!.GetValue(group)! == "Unmute",
        "Muting the model changes volume or fails to update the button label");
    groupType.GetProperty("IsMuted")!.SetValue(group, false);
    Check((int)groupType.GetProperty("VolumePercent")!.GetValue(group)! == 64 &&
        (string)groupType.GetProperty("MuteLabel")!.GetValue(group)! == "Mute",
        "Unmuting the model changes volume or fails to update the button label");
    var changes = new HashSet<string>();
    ((System.ComponentModel.INotifyPropertyChanged)group).PropertyChanged += (_, args) => changes.Add(args.PropertyName);
    groupType.GetProperty("IsSelected")!.SetValue(group, true);
    Check((double)groupType.GetProperty("SelectionIndicatorOpacity")!.GetValue(group) == 1 &&
        ((string)groupType.GetProperty("SelectionAccessibleName")!.GetValue(group)).Contains("selected") &&
        changes.Contains("SelectionIndicatorOpacity") && changes.Contains("SelectionToolTip"),
        "Selection indicator or accessible state does not update with the existing selected property");
    groupType.GetProperty("IsSelected")!.SetValue(group, false);
    Check((double)groupType.GetProperty("SelectionIndicatorOpacity")!.GetValue(group) == 0,
        "Unselected indicator must have an empty center");
    groupType.GetProperty("IsMuted")!.SetValue(group, true);
    Check((string)groupType.GetProperty("MuteGlyph")!.GetValue(group) == "\uE74F" &&
        (string)groupType.GetProperty("MuteToolTip")!.GetValue(group) == "Unmute group" && changes.Contains("MuteGlyph"),
        "Muted speaker glyph or tooltip does not update with mute state");
    groupType.GetProperty("IsMuted")!.SetValue(group, false);
    Check((string)groupType.GetProperty("MuteGlyph")!.GetValue(group) == "\uE767" &&
        (string)groupType.GetProperty("MuteToolTip")!.GetValue(group) == "Mute group" &&
        !(bool)groupType.GetProperty("IsSelected")!.GetValue(group) && (int)groupType.GetProperty("VolumePercent")!.GetValue(group) == 64,
        "Unmute glyph change must preserve group volume and selection");
    Console.WriteLine($"Completed {checks} checks. No live tray icon, app services, or user data used.");
}
finally { Native.GdiplusShutdown(token); }

// Compare just the visible monochrome symbol, allowing the existing pill layout.
byte[] MuteGlyphPixels(byte[] pixels, int canvas)
{
    var points = Enumerable.Range(0, canvas * canvas).Where(i => pixels[i*4+3] > 0 &&
        pixels[i*4] == pixels[i*4+1] && pixels[i*4+1] == pixels[i*4+2]).ToArray();
    var left = points.Min(i => i % canvas);
    var right = points.Max(i => i % canvas);
    var top = points.Min(i => i / canvas);
    var bottom = points.Max(i => i / canvas);
    var cropped = new List<byte> { (byte)(right-left+1), (byte)(bottom-top+1) };
    for (var y = top; y <= bottom; y++)
        cropped.AddRange(pixels.AsSpan((y*canvas+left)*4, (right-left+1)*4).ToArray());
    return cropped.ToArray();
}

// Nearest-neighbor preview; the real tray still receives its native-size HICON.
void SavePreview(byte[] pixels, int canvas, string path)
{
    const int zoom = 12;
    var width = canvas * zoom;
    var enlarged = new byte[width * width * 4];
    for (var y = 0; y < width; y++)
    for (var x = 0; x < width; x++)
    {
        var source = ((y / zoom) * canvas + x / zoom) * 4;
        var dest = (y * width + x) * 4;
        // Composite straight RGB/alpha over a dark taskbar-like backdrop.
        var alpha = pixels[source + 3];
        for (var channel = 0; channel < 3; channel++)
            enlarged[dest + channel] = (byte)((pixels[source + channel] * alpha + 32 * (255 - alpha) + 127) / 255);
        enlarged[dest + 3] = 255;
    }
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var pinned = GCHandle.Alloc(enlarged, GCHandleType.Pinned);
    IntPtr bitmap = IntPtr.Zero;
    try
    {
        if (Native.GdipCreateBitmapFromScan0(width, width, width * 4, 0x26200A, pinned.AddrOfPinnedObject(), out bitmap) != 0)
            throw new Exception("Preview bitmap failed");
        var encoder = new Guid("557cf406-1a04-11d3-9a73-0000f81ef32e");
        if (Native.GdipSaveImageToFile(bitmap, Path.GetFullPath(path), ref encoder, IntPtr.Zero) != 0)
            throw new Exception("Preview PNG failed");
    }
    finally { if (bitmap != IntPtr.Zero) Native.GdipDisposeImage(bitmap); pinned.Free(); }
}

static class Native
{
    [DllImport("gdiplus.dll")] public static extern int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, IntPtr pixels, out IntPtr bitmap);
    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)] public static extern int GdipSaveImageToFile(IntPtr image, string file, ref Guid encoder, IntPtr parameters);
    [DllImport("gdiplus.dll")] public static extern int GdipDisposeImage(IntPtr image);
    [StructLayout(LayoutKind.Sequential)] public struct Startup { public uint Version; public IntPtr Callback; public uint SuppressThread, SuppressCodecs; }
    [StructLayout(LayoutKind.Sequential)] public struct IconInfo { public int Icon; public uint X, Y; public IntPtr Mask, Color; }
    [StructLayout(LayoutKind.Sequential)] public struct Bitmap { public int Type, Width, Height, WidthBytes; public ushort Planes, Bits; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] public struct Header { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize; public int Xppm, Yppm; public uint Used, Important; }
    [DllImport("gdiplus.dll")] public static extern int GdiplusStartup(out UIntPtr token, ref Startup input, IntPtr output);
    [DllImport("gdiplus.dll")] public static extern void GdiplusShutdown(UIntPtr token);
    [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
    [DllImport("gdi32.dll")] public static extern int GetObjectW(IntPtr obj, int count, out Bitmap bitmap);
    [DllImport("gdi32.dll")] public static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref Header info, uint usage);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr LoadImageW(IntPtr instance, string path, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr process, uint flags);
}

