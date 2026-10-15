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
        Pixels("Fixture", index, "Dark", false);
    }
    var gdiAfter = Native.GetGuiResources(process.Handle, 0);
    var userAfter = Native.GetGuiResources(process.Handle, 1);
    Check(gdiAfter == gdiBefore && userAfter == userBefore, "Native object count increased across transitions");
    Console.WriteLine($"PASS: 100 logo/percentage cycles, GDI objects {gdiBefore}->{gdiAfter}, USER objects {userBefore}->{userAfter}.");
    Console.WriteLine($"Completed {checks} checks. No live tray icon, app services, or user data used.");
}
finally { Native.GdiplusShutdown(token); }

static class Native
{
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

