using System;
using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace TinyChaos.Gui;

/// <summary>
/// Sets the macOS Dock icon at runtime.
///
/// Why this exists: <c>Window.Icon</c> only drives the taskbar icon on Windows
/// and Linux. On macOS the Dock icon normally comes from the <c>.app</c>
/// bundle's <c>CFBundleIconFile</c> (an .icns). When the app is launched with
/// <c>dotnet run</c> (or any non-bundled exe) there is no bundle, so the Dock
/// shows the generic green "exec" host icon. The supported way to override it
/// for the running process is to hand an <c>NSImage</c> to
/// <c>[NSApplication setApplicationIconImage:]</c>, which we reach through the
/// Objective-C runtime via P/Invoke. No-op on every other platform.
/// </summary>
internal static class MacDockIcon
{
    private const string Objc = "/usr/lib/libobjc.A.dylib";

    // objc_msgSend is variadic; .NET needs one DllImport per call-site argument
    // shape, each with the concrete signature for that call.
    [DllImport(Objc, EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass(string name);

    [DllImport(Objc, EntryPoint = "sel_registerName")]
    private static extern IntPtr Sel(string name);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send_Ptr(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send_PtrUInt(
        IntPtr receiver, IntPtr selector, IntPtr bytes, nuint length);

    /// <summary>
    /// Load the app icon PNG (an Avalonia resource) and set it as the Dock
    /// icon. Best-effort: any failure is swallowed so a missing/renamed asset
    /// or an OS quirk never blocks startup. Safe to call from the UI thread.
    /// </summary>
    public static void TrySet(Uri pngAsset)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return;
        }

        try
        {
            // Pull the PNG bytes out of the bundled Avalonia resources.
            byte[] png;
            using (var stream = AssetLoader.Open(pngAsset))
            {
                using var ms = new System.IO.MemoryStream();
                stream.CopyTo(ms);
                png = ms.ToArray();
            }
            if (png.Length == 0)
            {
                return;
            }

            // NSData *data = [NSData dataWithBytes:png length:png.Length];
            IntPtr nsData = GetClass("NSData");
            IntPtr data;
            var handle = GCHandle.Alloc(png, GCHandleType.Pinned);
            try
            {
                data = Send_PtrUInt(
                    nsData,
                    Sel("dataWithBytes:length:"),
                    handle.AddrOfPinnedObject(),
                    (nuint)png.Length);
            }
            finally
            {
                handle.Free();
            }
            if (data == IntPtr.Zero)
            {
                return;
            }

            // NSImage *img = [[NSImage alloc] initWithData:data];
            IntPtr nsImageClass = GetClass("NSImage");
            IntPtr img = Send_Ptr(Send(nsImageClass, Sel("alloc")), Sel("initWithData:"), data);
            if (img == IntPtr.Zero)
            {
                return;
            }

            // [[NSApplication sharedApplication] setApplicationIconImage:img];
            IntPtr nsApp = Send(GetClass("NSApplication"), Sel("sharedApplication"));
            if (nsApp != IntPtr.Zero)
            {
                Send_Ptr(nsApp, Sel("setApplicationIconImage:"), img);
            }
        }
        catch
        {
            // Non-fatal: the app simply keeps the default Dock icon.
        }
    }
}
