using Microsoft.UI.Dispatching;
using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Windows.ApplicationModel;
using Windows.Graphics;

namespace Project_Phantom;

/// <summary>Owns the notification-area icon and its native Windows context menu.</summary>
internal sealed class TrayIconService : IDisposable
{
    private const uint TrayIconId = 1;
    private const uint TrayCallbackMessage = 0x8001; // WM_APP + 1
    private const uint WmContextMenu = 0x007B;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmDpiChanged = 0x02E0;
    private const uint WmDisplayChange = 0x007E;
    private const uint WmSettingChange = 0x001A;
    private const uint WmNull = 0x0000;
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NifMessage = 1;
    private const uint NifIcon = 2;
    private const uint NifTip = 4;
    private const uint MfString = 0;
    private const uint MfSeparator = 0x800;
    private const uint MfDisabled = 2;
    private const uint MfGrayed = 1;
    private const uint TpmRightButton = 2;
    private const uint TpmReturnCommand = 0x0100;
    private const uint HeaderCommand = 1000;
    private const uint OpenCommand = 1001;
    private const uint ExitCommand = 1002;
    private const uint SettingsCommand = 1003;
    private const uint MiimBitmap = 0x80;
    private const uint ColorMenu = 4;
    private const int SmallIconWidthMetric = 49;
    private const int SmallIconHeightMetric = 50;
    private const int GdipUnitPixel = 2;
    private const int GdipFontStyleRegular = 0;
    private const int GdipFontStyleBold = 1;
    private const int GdipStringFormatNoWrap = 0x1000;
    private const int GdipStringFormatNoClip = 0x4000;
    private const int GdipStringFormatNoFitBlackBox = 4;
    private const int GdipTextRenderingHintAntiAliasGridFit = 4;
    private const int PixelFormat32BppArgb = 0x0026200A;
    private const uint GdiPlusVersion = 1;
    private static readonly UIntPtr SubclassId = new(1);

    private readonly IntPtr _windowHandle;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action _openAction;
    private readonly Action _exitAction;
    private readonly Action<RectInt32>? _showStyledMenu;
    private readonly Action? _settingsAction;
    private readonly SubclassProcedure _subclassProcedure;
    private readonly IntPtr _menu;
    private readonly uint _taskbarCreatedMessage;
    private readonly UIntPtr _gdiplusToken;
    private readonly IntPtr _menuLogoBitmap;
    private IntPtr _currentIcon;
    private string? _selectedGroupName;
    private int _selectedGroupVolume = 100;
    private string _selectedAccentColor = GroupTrayColors.DefaultAccent;
    private bool _disposed;

    public TrayIconService(IntPtr windowHandle, DispatcherQueue dispatcher, Action openAction, Action exitAction,
        Action<RectInt32>? showStyledMenu = null, Action? settingsAction = null)
    {
        _windowHandle = windowHandle;
        _dispatcher = dispatcher;
        _openAction = openAction;
        _exitAction = exitAction;
        _showStyledMenu = showStyledMenu;
        _settingsAction = settingsAction;
        _subclassProcedure = WindowProcedure;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        var startupInput = new GdiplusStartupInput { Version = GdiPlusVersion };
        CheckGdiPlus(GdiplusStartup(out var gdiplusToken, ref startupInput, IntPtr.Zero));
        _gdiplusToken = gdiplusToken;

        _menuLogoBitmap = LoadMenuLogo();
        _menu = CreatePopupMenu();
        if (_menu == IntPtr.Zero)
        {
            CleanupGraphicsResources();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the Project Phantom tray menu.");
        }

        AddMenuItems();
        if (!SetWindowSubclass(_windowHandle, _subclassProcedure, SubclassId, UIntPtr.Zero))
        {
            DestroyMenu(_menu);
            CleanupGraphicsResources();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not connect the Project Phantom tray icon to its window.");
        }

        _currentIcon = RenderGroupIcon(null, 100);
        if (_currentIcon == IntPtr.Zero || !AddTrayIcon())
        {
            RemoveWindowSubclass(_windowHandle, _subclassProcedure, SubclassId);
            if (_currentIcon != IntPtr.Zero)
            {
                DestroyIcon(_currentIcon);
                _currentIcon = IntPtr.Zero;
            }

            DestroyMenu(_menu);
            CleanupGraphicsResources();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not add the Project Phantom tray icon.");
        }
    }

    public void UpdateSelectedGroup(string? groupName, int volumePercent, string? accentColor = null)
    {
        _selectedGroupName = string.IsNullOrWhiteSpace(groupName) ? null : groupName.Trim();
        _selectedGroupVolume = Math.Clamp(volumePercent, 0, 100);
        _selectedAccentColor = GroupTrayColors.Normalize(accentColor, GroupTrayColors.DefaultAccent);
        if (_disposed)
        {
            return;
        }

        var replacementIcon = RenderGroupIcon(_selectedGroupName, _selectedGroupVolume);
        if (replacementIcon == IntPtr.Zero)
        {
            return;
        }

        var data = CreateNotifyIconData(replacementIcon, _selectedGroupName, _selectedGroupVolume);
        if (!Shell_NotifyIcon(NimModify, ref data))
        {
            DestroyIcon(replacementIcon);
            return;
        }

        var previousIcon = _currentIcon;
        _currentIcon = replacementIcon;
        if (previousIcon != IntPtr.Zero)
        {
            DestroyIcon(previousIcon);
        }
    }

    private void AddMenuItems()
    {
        AppendMenu(_menu, MfString | MfDisabled | MfGrayed, HeaderCommand, "Project Phantom");
        if (_menuLogoBitmap != IntPtr.Zero)
        {
            var item = new MenuItemInfo
            {
                Size = (uint)Marshal.SizeOf<MenuItemInfo>(),
                Mask = MiimBitmap,
                Bitmap = _menuLogoBitmap
            };
            SetMenuItemInfo(_menu, HeaderCommand, false, ref item);
        }

        AppendMenu(_menu, MfSeparator, 0, null);
        AppendMenu(_menu, MfString, OpenCommand, "Open Project Phantom");
        if (_settingsAction is not null) AppendMenu(_menu, MfString, SettingsCommand, "Settings");
        AppendMenu(_menu, MfString, ExitCommand, "Exit");
    }

    private IntPtr LoadMenuLogo()
    {
        var logoPath = ResolveBrandingAsset("Square44x44Logo.targetsize-24_altform-unplated.png");

        if (!File.Exists(logoPath) || GdipCreateBitmapFromFile(logoPath, out var image) != 0)
        {
            return IntPtr.Zero;
        }

        try
        {
            var colorRef = GetSysColor(ColorMenu);
            var menuBackgroundArgb = ColorRefToArgb(colorRef);
            if (GdipCreateHBITMAPFromBitmap(image, out var bitmap, menuBackgroundArgb) != 0)
            {
                return IntPtr.Zero;
            }

            return bitmap;
        }
        finally
        {
            GdipDisposeImage(image);
        }
    }

    private static string ResolveBrandingAsset(string fileName)
    {
        try
        {
            var installedPath = Path.Combine(Package.Current.InstalledLocation.Path, "Assets", fileName);
            if (File.Exists(installedPath))
            {
                return installedPath;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Unpackaged launches do not have a package install location.
        }

        return Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
    }

    private bool AddTrayIcon()
    {
        var data = CreateNotifyIconData(_currentIcon, _selectedGroupName, _selectedGroupVolume);
        return Shell_NotifyIcon(NimAdd, ref data);
    }

    private NotifyIconData CreateNotifyIconData(IntPtr icon, string? groupName, int volumePercent)
    {
        return new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            WindowHandle = _windowHandle,
            Id = TrayIconId,
            Flags = NifMessage | NifIcon | NifTip,
            CallbackMessage = TrayCallbackMessage,
            IconHandle = icon,
            Tip = BuildTooltip(groupName, volumePercent),
            Info = string.Empty,
            InfoTitle = string.Empty
        };
    }

    private static string BuildTooltip(string? groupName, int volumePercent)
    {
        if (string.IsNullOrWhiteSpace(groupName))
        {
            return "No group selected";
        }

        var suffix = $" — {volumePercent}%";
        var name = groupName.Trim();
        var maxNameLength = 127 - suffix.Length;
        if (name.Length > maxNameLength)
        {
            var ellipsis = "…";
            var nameLength = maxNameLength - ellipsis.Length;
            if (char.IsHighSurrogate(name[nameLength - 1]))
            {
                nameLength--;
            }

            name = name[..nameLength] + ellipsis;
        }

        return name + suffix;
    }

    private IntPtr RenderGroupIcon(string? groupName, int volumePercent)
    {
        // The notification area belongs to the taskbar, which may be on a
        // different-DPI monitor from Phantom's visible (or hidden) window.
        var taskbarWindow = FindWindow("Shell_TrayWnd", null);
        var dpi = GetDpiForWindow(taskbarWindow != IntPtr.Zero ? taskbarWindow : _windowHandle);
        if (dpi == 0)
        {
            dpi = 96;
        }

        // Use the largest square that fits the system's small-icon dimensions.
        // The background fills this canvas edge to edge; no transparent inset.
        var size = Math.Max(16, Math.Min(
            GetSystemMetricsForDpi(SmallIconWidthMetric, dpi),
            GetSystemMetricsForDpi(SmallIconHeightMetric, dpi)));
        var width = size;
        var height = size;
        var bitmapStatus = GdipCreateBitmapFromScan0(width, height, 0, PixelFormat32BppArgb, IntPtr.Zero, out var bitmap);
        CheckGdiPlus(bitmapStatus);

        IntPtr graphics = IntPtr.Zero;
        IntPtr fontFamily = IntPtr.Zero;
        IntPtr stringFormat = IntPtr.Zero;
        IntPtr brush = IntPtr.Zero;

        try
        {
            CheckGdiPlus(GdipGetImageGraphicsContext(bitmap, out graphics));
            CheckGdiPlus(GdipSetPageUnit(graphics, GdipUnitPixel));
            CheckGdiPlus(GdipGraphicsClear(graphics, 0));
            CheckGdiPlus(GdipSetInterpolationMode(graphics, 5)); // Nearest neighbor; copies glyph pixels 1:1.
            var pillHeight = Math.Max(3, (int)MathF.Round(height * 3f / 16f));
            var bottomMargin = Math.Max(1, (int)MathF.Round(height / 16f));
            var separation = bottomMargin;
            var pillTop = height - bottomMargin - pillHeight;
            CheckGdiPlus(GdipSetCompositingMode(graphics, 0)); // SourceOver preserves transparent glyph edges.
            CheckGdiPlus(GdipCreateFontFamilyFromName("Segoe UI Semibold", IntPtr.Zero, out fontFamily));
            CheckGdiPlus(GdipStringFormatGetGenericTypographic(out stringFormat));
            CheckGdiPlus(GdipSetStringFormatFlags(stringFormat,
                GdipStringFormatNoWrap | GdipStringFormatNoClip | GdipStringFormatNoFitBlackBox));
            CheckGdiPlus(GdipSetStringFormatAlign(stringFormat, 0));
            CheckGdiPlus(GdipSetStringFormatLineAlign(stringFormat, 0));
            CheckGdiPlus(GdipCreateSolidFill(0xFFFFFFFF, out brush));

            var percentText = Math.Clamp(volumePercent, 0, 100).ToString();
            // Fit against the original area so spacing cannot shrink the font.
            var originalNumberHeight = height - bottomMargin -
                Math.Max(2, (int)MathF.Round(height * 2f / 16f)) - separation;
            using var number = FitIconLine(percentText, fontFamily, GdipFontStyleRegular,
                stringFormat, brush, width, originalNumberHeight, truncate: false);
            var requiredGap = Math.Max(2, (int)MathF.Round(height * 2f / 16f));
            var numberY = (originalNumberHeight - number.Height) / 2;
            // A tall glyph may need the pill's bottom margin to fit the gap.
            // Keep the existing font even if the thicker pill leaves less room.
            // Move the number up first; a 12-row glyph can leave only one clear
            // row beside a 3-row pill within the 16-row canvas.
            pillTop = Math.Min(height - pillHeight, Math.Max(pillTop, number.Height + requiredGap));
            numberY = Math.Max(0, Math.Min(numberY, pillTop - requiredGap - number.Height));
            PaintTrayAccent(bitmap, width, height, pillTop, pillHeight, _selectedAccentColor);
            CheckGdiPlus(GdipDrawImageRectRectI(graphics, number.Bitmap,
                (width - number.Width) / 2, numberY, number.Width, number.Height,
                number.Left, number.Top, number.Width, number.Height,
                GdipUnitPixel, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
            return CreateNativeIcon(bitmap, width, height);
        }
        finally
        {
            if (brush != IntPtr.Zero) GdipDeleteBrush(brush);
            if (stringFormat != IntPtr.Zero) GdipDeleteStringFormat(stringFormat);
            if (fontFamily != IntPtr.Zero) GdipDeleteFontFamily(fontFamily);
            if (graphics != IntPtr.Zero) GdipDeleteGraphics(graphics);
            GdipDisposeImage(bitmap);
        }
    }

    private static void PaintTrayAccent(IntPtr bitmap, int width, int height, int pillTop, int pillHeight, string accentColor)
    {
        // A centered 10x3 capsule at 96 DPI. Everything outside it is transparent.
        // Supersample coverage, retaining straight RGB for native alpha conversion.
        var pillWidth = Math.Max(pillHeight, (int)MathF.Round(width * 10f / 16f));
        var left = (width - pillWidth) / 2f;
        var right = left + pillWidth;
        var radius = pillHeight / 2f;
        var centerY = pillTop + radius;
        var rgb = uint.Parse(accentColor.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        var pixels = new byte[checked(width * height * 4)];
        const int samples = 4;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var coverage = 0;
            for (var sy = 0; sy < samples; sy++)
            for (var sx = 0; sx < samples; sx++)
            {
                var px = x + (sx + 0.5f) / samples;
                var py = y + (sy + 0.5f) / samples;
                var dx = px - Math.Clamp(px, left + radius, right - radius);
                var dy = py - centerY;
                if (dx * dx + dy * dy <= radius * radius) coverage++;
            }
            var index = (y * width + x) * 4;
            pixels[index] = (byte)rgb;
            pixels[index + 1] = (byte)(rgb >> 8);
            pixels[index + 2] = (byte)(rgb >> 16);
            pixels[index + 3] = (byte)((coverage * 255 + samples * samples / 2) / (samples * samples));
        }

        var rectangle = new PixelRect { Width = width, Height = height };
        var data = new BitmapData();
        CheckGdiPlus(GdipBitmapLockBits(bitmap, ref rectangle, 2, PixelFormat32BppArgb, ref data));
        try
        {
            for (var y = 0; y < height; y++)
                Marshal.Copy(pixels, y * width * 4, IntPtr.Add(data.Scan0, y * data.Stride), width * 4);
        }
        finally { CheckGdiPlus(GdipBitmapUnlockBits(bitmap, ref data)); }
    }

    private static IntPtr CreateNativeIcon(IntPtr bitmap, int width, int height)
    {
        // Read straight BGRA, keeping RGB brightness separate from coverage.
        // GdipCreateHICONFromBitmap attenuated RGB before our native drawing
        // path applied alpha again, making antialiased glyphs unusually dark.
        var rectangle = new PixelRect { Width = width, Height = height };
        var data = new BitmapData();
        var pixels = new byte[checked(width * height * 4)];
        CheckGdiPlus(GdipBitmapLockBits(bitmap, ref rectangle, 1, PixelFormat32BppArgb, ref data));
        try
        {
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * width * 4, width * 4);
            }
        }
        finally
        {
            CheckGdiPlus(GdipBitmapUnlockBits(bitmap, ref data));
        }

        // Invisible pixels carry no color, avoiding residual edge/background data.
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 0)
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 0;
        }

        var header = new IconBitmapHeader
        {
            Size = (uint)Marshal.SizeOf<IconBitmapHeader>(),
            Width = width,
            Height = -height, // Top-down rows, matching the renderer.
            Planes = 1,
            BitsPerPixel = 32,
            Compression = 0 // BI_RGB: explicit BGRA bytes, no color conversion.
        };
        IntPtr colorBitmap = IntPtr.Zero;
        IntPtr maskBitmap = IntPtr.Zero;
        try
        {
            colorBitmap = NativeCreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (colorBitmap == IntPtr.Zero || bits == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the tray icon color bitmap.");
            Marshal.Copy(pixels, 0, bits, pixels.Length);

            // A valid, initialized mask is required by CreateIconIndirect. The
            // 32-bit color bitmap supplies per-pixel alpha on supported Windows.
            var maskStride = checked(((width + 15) / 16) * 2);
            maskBitmap = NativeCreateBitmap(width, height, 1, 1, new byte[checked(maskStride * height)]);
            if (maskBitmap == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the tray icon mask.");

            var iconInfo = new NativeIconInfo { IsIcon = true, ColorBitmap = colorBitmap, MaskBitmap = maskBitmap };
            var icon = NativeCreateIconIndirect(ref iconInfo);
            if (icon == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the tray icon.");
            return icon;
        }
        finally
        {
            // CreateIconIndirect copies both bitmaps; the returned HICON is
            // independently owned and released by the existing tray lifecycle.
            if (maskBitmap != IntPtr.Zero) DeleteObject(maskBitmap);
            if (colorBitmap != IntPtr.Zero) DeleteObject(colorBitmap);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconBitmapHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitsPerPixel;
        public uint Compression, ImageSize;
        public int XPixelsPerMeter, YPixelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeIconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
        public uint XHotspot, YHotspot;
        public IntPtr MaskBitmap, ColorBitmap;
    }

    private static void DrawIconLine(
        IntPtr graphics, string text, IntPtr family, int style, IntPtr format, IntPtr brush,
        int width, int rowTop, int rowHeight, bool truncate)
    {
        using var line = FitIconLine(text, family, style, format, brush, width, rowHeight, truncate);
        if (line.Width == 0 || line.Height == 0) return;

        var pixels = line;

        // Crop unused font bearings, then copy at native resolution. Font size
        // is chosen from all visible alpha pixels, including accents/descenders.
        var x = (width - pixels.Width) / 2;
        var y = rowTop + (rowHeight - pixels.Height) / 2;
        CheckGdiPlus(GdipDrawImageRectRectI(graphics, pixels.Bitmap,
            x, y, pixels.Width, pixels.Height, pixels.Left, pixels.Top, pixels.Width, pixels.Height,
            GdipUnitPixel, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
    }

    private static GlyphRaster FitIconLine(
        string text, IntPtr family, int style, IntPtr format, IntPtr brush,
        int maxWidth, int maxHeight, bool truncate)
    {
        // Bound scratch allocations even for exceptionally long saved names.
        if (truncate)
        {
            var elements = StringInfo.GetTextElementEnumerator(text);
            var length = 0;
            while (elements.MoveNext())
            {
                var nextLength = elements.ElementIndex + elements.GetTextElement().Length;
                if (nextLength > 128) break;
                length = nextLength;
            }
            if (length < text.Length) text = text[..length] + "\u2026";

            // Keep short labels whole when a modest font reduction suffices.
            // Do not shrink long labels indefinitely just to avoid an ellipsis.
            for (var size = maxHeight * 2; size >= Math.Max(3, maxHeight); size--)
            {
                var complete = RasterizeGlyphs(text, family, size, style, format, brush);
                if (complete.Width <= maxWidth && complete.Height <= maxHeight) return complete;
                complete.Dispose();
            }
        }

        for (var size = maxHeight * 2; size >= 3; size--)
        {
            var raster = RasterizeGlyphs(text, family, size, style, format, brush);
            if (raster.Height <= maxHeight)
            {
                if (raster.Width <= maxWidth) return raster;
                if (truncate)
                {
                    raster.Dispose();
                    // Find the longest whole-text-element prefix that fits at
                    // this readable font size. Never cut a surrogate/combining sequence.
                    var starts = StringInfo.ParseCombiningCharacters(text);
                    var low = 0;
                    var high = starts.Length - 1;
                    GlyphRaster? best = null;
                    try
                    {
                        while (low <= high)
                        {
                            var count = (low + high) / 2;
                            var candidate = RasterizeGlyphs(text[..starts[count]] + "\u2026",
                                family, size, style, format, brush);
                            if (candidate.Width <= maxWidth && candidate.Height <= maxHeight)
                            {
                                best?.Dispose();
                                best = candidate;
                                low = count + 1;
                            }
                            else
                            {
                                candidate.Dispose();
                                high = count - 1;
                            }
                        }
                        if (best is not null)
                        {
                            var result = best;
                            best = null;
                            return result;
                        }
                    }
                    finally { best?.Dispose(); }
                    continue;
                }
            }
            raster.Dispose();
        }
        return RasterizeGlyphs(string.Empty, family, 3, style, format, brush);
    }

    private static GlyphRaster RasterizeGlyphs(
        string text, IntPtr family, int size, int style, IntPtr format, IntPtr brush,
        int renderingHint = GdipTextRenderingHintAntiAliasGridFit)
    {
        var padding = size * 2;
        var width = Math.Max(1, text.Length) * size * 2 + padding * 2;
        var height = size * 6;
        CheckGdiPlus(GdipCreateBitmapFromScan0(width, height, 0, PixelFormat32BppArgb, IntPtr.Zero, out var bitmap));
        IntPtr graphics = IntPtr.Zero;
        IntPtr font = IntPtr.Zero;
        try
        {
            CheckGdiPlus(GdipGetImageGraphicsContext(bitmap, out graphics));
            CheckGdiPlus(GdipSetPageUnit(graphics, GdipUnitPixel));
            CheckGdiPlus(GdipGraphicsClear(graphics, 0));
            CheckGdiPlus(GdipSetTextRenderingHint(graphics, renderingHint));
            CheckGdiPlus(GdipCreateFont(family, size, style, GdipUnitPixel, out font));
            var layout = new RectF(padding, padding, width - padding * 2, height - padding * 2);
            if (text.Length > 0)
                CheckGdiPlus(GdipDrawString(graphics, text, text.Length, font, ref layout, format, brush));

            var lockRectangle = new PixelRect { Width = width, Height = height };
            var data = new BitmapData();
            CheckGdiPlus(GdipBitmapLockBits(bitmap, ref lockRectangle, 1, PixelFormat32BppArgb, ref data));
            var left = width;
            var top = height;
            var right = -1;
            var bottom = -1;
            try
            {
                var row = new byte[width * 4];
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    for (var x = 0; x < width; x++)
                    {
                        if (row[x * 4 + 3] == 0) continue;
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                        top = Math.Min(top, y);
                        bottom = Math.Max(bottom, y);
                    }
                }
            }
            finally { GdipBitmapUnlockBits(bitmap, ref data); }

            var result = new GlyphRaster(bitmap, left, top,
                right < 0 ? 0 : right - left + 1, bottom < 0 ? 0 : bottom - top + 1, text, size);
            bitmap = IntPtr.Zero; // Ownership passes to the returned raster.
            return result;
        }
        finally
        {
            if (font != IntPtr.Zero) GdipDeleteFont(font);
            if (graphics != IntPtr.Zero) GdipDeleteGraphics(graphics);
            if (bitmap != IntPtr.Zero) GdipDisposeImage(bitmap);
        }
    }

    private sealed class GlyphRaster : IDisposable
    {
        public IntPtr Bitmap { get; private set; }
        public int Left { get; }
        public int Top { get; }
        public int Width { get; }
        public int Height { get; }
        public string Text { get; }
        public int FontSize { get; }

        public GlyphRaster(IntPtr bitmap, int left, int top, int width, int height, string text, int fontSize)
        {
            Bitmap = bitmap; Left = left; Top = top; Width = width; Height = height;
            Text = text; FontSize = fontSize;
        }

        public void Dispose()
        {
            if (Bitmap == IntPtr.Zero) return;
            GdipDisposeImage(Bitmap);
            Bitmap = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PixelRect { public int X, Y, Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapData
    {
        public uint Width, Height;
        public int Stride, PixelFormat;
        public IntPtr Scan0;
        public UIntPtr Reserved;
    }

    private IntPtr WindowProcedure(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData)
    {
        if (message == _taskbarCreatedMessage)
        {
            AddTrayIcon();
            return IntPtr.Zero;
        }

        if (message == WmDpiChanged || message == WmDisplayChange || message == WmSettingChange)
        {
            _dispatcher.TryEnqueue(() => UpdateSelectedGroup(_selectedGroupName, _selectedGroupVolume, _selectedAccentColor));
        }

        if (message == TrayCallbackMessage)
        {
            var mouseMessage = unchecked((uint)lParam.ToInt64());
            if (mouseMessage == WmRButtonUp || mouseMessage == WmContextMenu)
            {
                ShowContextMenu();
                return IntPtr.Zero;
            }

            if (mouseMessage == WmLButtonUp)
            {
                _dispatcher.TryEnqueue(() => _openAction());
                return IntPtr.Zero;
            }
        }

        return DefSubclassProc(windowHandle, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        if (!GetCursorPos(out var cursorPosition))
        {
            return;
        }

        var anchor = new RectInt32(cursorPosition.X, cursorPosition.Y, 1, 1);
        var identifier = new NotifyIconIdentifier
        {
            Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
            WindowHandle = _windowHandle,
            Id = TrayIconId
        };
        if (Shell_NotifyIconGetRect(ref identifier, out var iconBounds) == 0 &&
            iconBounds.Right > iconBounds.Left && iconBounds.Bottom > iconBounds.Top)
        {
            anchor = new RectInt32(iconBounds.Left, iconBounds.Top,
                iconBounds.Right - iconBounds.Left, iconBounds.Bottom - iconBounds.Top);
        }

        // A separate lightweight popup can be shown even while MainWindow is hidden.
        // Retain the existing native menu as a fallback if no UI callback is supplied.
        if (_showStyledMenu is not null && _dispatcher.TryEnqueue(() =>
        {
            if (_disposed) return;
            try { _showStyledMenu(anchor); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Could not show the styled tray menu: " + ex);
                ShowNativeContextMenu(cursorPosition);
            }
        })) return;

        ShowNativeContextMenu(cursorPosition);
    }

    private void ShowNativeContextMenu(Point cursorPosition)
    {
        SetForegroundWindow(_windowHandle);
        var command = TrackPopupMenu(
            _menu,
            TpmRightButton | TpmReturnCommand,
            cursorPosition.X,
            cursorPosition.Y,
            0,
            _windowHandle,
            IntPtr.Zero);
        PostMessage(_windowHandle, WmNull, IntPtr.Zero, IntPtr.Zero);

        if (command == OpenCommand)
        {
            _dispatcher.TryEnqueue(() => _openAction());
        }
        else if (command == SettingsCommand && _settingsAction is not null)
        {
            _dispatcher.TryEnqueue(() => _settingsAction());
        }
        else if (command == ExitCommand)
        {
            _dispatcher.TryEnqueue(() => _exitAction());
        }
    }

    private void CleanupGraphicsResources()
    {
        if (_menuLogoBitmap != IntPtr.Zero)
        {
            DeleteObject(_menuLogoBitmap);
        }

        GdiplusShutdown(_gdiplusToken);
    }

    private static uint ColorRefToArgb(uint colorRef)
    {
        return 0xFF000000u |
            ((colorRef & 0x000000FFu) << 16) |
            (colorRef & 0x0000FF00u) |
            ((colorRef & 0x00FF0000u) >> 16);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var data = CreateNotifyIconData(_currentIcon, _selectedGroupName, _selectedGroupVolume);
        Shell_NotifyIcon(NimDelete, ref data);
        RemoveWindowSubclass(_windowHandle, _subclassProcedure, SubclassId);
        DestroyMenu(_menu);
        CleanupGraphicsResources();

        if (_currentIcon != IntPtr.Zero)
        {
            DestroyIcon(_currentIcon);
            _currentIcon = IntPtr.Zero;
        }
    }

    private static void CheckGdiPlus(int status)
    {
        if (status != 0)
        {
            throw new InvalidOperationException($"Windows drawing failed with status {status}.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Id;
        public Guid ItemGuid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconBounds
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out IconBounds bounds);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr IconHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MenuItemInfo
    {
        public uint Size;
        public uint Mask;
        public uint Type;
        public uint State;
        public uint Id;
        public IntPtr SubMenu;
        public IntPtr CheckedBitmap;
        public IntPtr UncheckedBitmap;
        public UIntPtr ItemData;
        public IntPtr TypeData;
        public uint CharCount;
        public IntPtr Bitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectF
    {
        public float X;
        public float Y;
        public float Width;
        public float Height;

        public RectF(float x, float y, float width, float height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GdiplusStartupInput
    {
        public uint Version;
        public IntPtr DebugEventCallback;
        [MarshalAs(UnmanagedType.Bool)] public bool SuppressBackgroundThread;
        [MarshalAs(UnmanagedType.Bool)] public bool SuppressExternalCodecs;
    }

    private delegate IntPtr SubclassProcedure(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "AppendMenuW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint itemId, string? itemText);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMenuItemInfo(IntPtr menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition, ref MenuItemInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr ownerWindow, IntPtr rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetSysColor(uint index);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr windowHandle, SubclassProcedure callback, UIntPtr subclassId, UIntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr windowHandle, SubclassProcedure callback, UIntPtr subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr objectHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdiplusStartup(out UIntPtr token, ref GdiplusStartupInput input, IntPtr output);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern void GdiplusShutdown(UIntPtr token);

    [DllImport("gdiplus.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GdipCreateBitmapFromFile(string fileName, out IntPtr bitmap);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipCreateHBITMAPFromBitmap(IntPtr bitmap, out IntPtr hbitmap, uint backgroundArgb);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipCreateBitmapFromScan0(int width, int height, int stride, int pixelFormat, IntPtr scan0, out IntPtr bitmap);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipGetImageGraphicsContext(IntPtr image, out IntPtr graphics);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipGraphicsClear(IntPtr graphics, uint color);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipSetInterpolationMode(IntPtr graphics, int interpolationMode);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipSetCompositingMode(IntPtr graphics, int compositingMode);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipSetTextRenderingHint(IntPtr graphics, int renderingHint);

    [DllImport("gdiplus.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GdipCreateFontFamilyFromName(string name, IntPtr fontCollection, out IntPtr fontFamily);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipCreateFont(IntPtr fontFamily, float emSize, int style, int unit, out IntPtr font);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipStringFormatGetGenericTypographic(out IntPtr stringFormat);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipSetStringFormatFlags(IntPtr stringFormat, int flags);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipSetPageUnit(IntPtr graphics, int unit);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipBitmapLockBits(IntPtr bitmap, ref PixelRect rectangle, uint flags, int pixelFormat, ref BitmapData data);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipBitmapUnlockBits(IntPtr bitmap, ref BitmapData data);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDrawImageRectRectI(IntPtr graphics, IntPtr image,
        int destX, int destY, int destWidth, int destHeight,
        int sourceX, int sourceY, int sourceWidth, int sourceHeight,
        int unit, IntPtr imageAttributes, IntPtr callback, IntPtr callbackData);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipSetStringFormatAlign(IntPtr stringFormat, int alignment);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipSetStringFormatLineAlign(IntPtr stringFormat, int alignment);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipCreateSolidFill(uint color, out IntPtr brush);

    [DllImport("gdiplus.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GdipDrawString(IntPtr graphics, string text, int length, IntPtr font, ref RectF layoutRect, IntPtr stringFormat, IntPtr brush);

    [DllImport("gdi32.dll", EntryPoint = "CreateDIBSection", SetLastError = true)]
    private static extern IntPtr NativeCreateDIBSection(IntPtr dc, ref IconBitmapHeader header,
        uint usage, out IntPtr pixels, IntPtr section, uint offset);

    [DllImport("gdi32.dll", EntryPoint = "CreateBitmap", SetLastError = true)]
    private static extern IntPtr NativeCreateBitmap(int width, int height, uint planes, uint bitsPerPixel, [In] byte[] pixels);

    [DllImport("user32.dll", EntryPoint = "CreateIconIndirect", SetLastError = true)]
    private static extern IntPtr NativeCreateIconIndirect(ref NativeIconInfo info);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDisposeImage(IntPtr image);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDeleteGraphics(IntPtr graphics);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDeleteFontFamily(IntPtr fontFamily);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDeleteFont(IntPtr font);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDeleteStringFormat(IntPtr stringFormat);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDeleteBrush(IntPtr brush);
}
