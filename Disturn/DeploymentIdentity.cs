using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Disturn;

internal static class DeploymentIdentity
{
    // Match Package.appxmanifest exactly. Do not search other package families.
    internal const string PackageName = "5bd23fa4-6b40-455a-8757-d7cc9087e06a";
    internal const string PackagePublisher = "CN=Rix";

    internal static bool HasPackageIdentity
    {
        get
        {
            uint length = 0;
            var result = GetCurrentPackageFullName(ref length, IntPtr.Zero);
            return result switch
            {
                122 => true, // ERROR_INSUFFICIENT_BUFFER: a package name is available.
                15700 => false, // APPMODEL_ERROR_NO_PACKAGE.
                _ => throw new Win32Exception(result)
            };
        }
    }

    internal static string LegacyPackageFamilyName
    {
        get
        {
            var id = new PackageId { Name = PackageName, Publisher = PackagePublisher, Architecture = 11 };
            uint length = 0;
            var result = PackageFamilyNameFromId(ref id, ref length, null);
            if (result != 122) throw new Win32Exception(result);
            var name = new StringBuilder((int)length);
            result = PackageFamilyNameFromId(ref id, ref length, name);
            if (result != 0) throw new Win32Exception(result);
            return name.ToString();
        }
    }

    internal static string LegacyPackagedSavePath(string localAppData) => Path.Combine(
        localAppData, "Packages", LegacyPackageFamilyName, "LocalCache", "Local", "Project Phantom", "groups.json");

    internal static bool IsLegacyPackageRegistered()
    {
        uint count = 0, length = 0;
        var result = GetPackagesByPackageFamily(LegacyPackageFamilyName, ref count, IntPtr.Zero, ref length, IntPtr.Zero);
        if (result != 0 && result != 122) throw new Win32Exception(result);
        return count != 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PackageId
    {
        public uint Reserved, Architecture;
        public ulong Version;
        public string? Name, Publisher, ResourceId, PublisherId;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint length, IntPtr name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int PackageFamilyNameFromId(ref PackageId id, ref uint length, StringBuilder? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagesByPackageFamily(string family, ref uint count, IntPtr names, ref uint length, IntPtr buffer);
}
