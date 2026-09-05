using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace PalworldServerManager.Platform.Windows;

internal static class NativeTlsKeys
{
    internal const uint Machine = 0x20, Silent = 0x40;
    internal const int NotFound = unchecked((int)0x80090016), NoMoreItems = unchecked((int)0x8009002A);
    internal static SafeNCryptProviderHandle Provider()
    {
        var status = NCryptOpenStorageProvider(out var provider, "Microsoft Software Key Storage Provider", 0);
        try { Check(status); return provider; } catch { provider.Dispose(); throw; }
    }
    internal static SafeNCryptKeyHandle? Open(SafeNCryptProviderHandle provider, string name)
    {
        var status = NCryptOpenKey(provider, out var key, name, 0, Machine | Silent);
        if (status == NotFound) { key.Dispose(); return null; }
        try { Check(status); return key; } catch { key.Dispose(); throw; }
    }
    internal static void Import(SafeNCryptProviderHandle provider, string name, byte[] pkcs8, byte[] security)
    {
        var namePointer = Marshal.StringToHGlobalUni(name);
        var bufferPointer = Marshal.AllocHGlobal(Marshal.SizeOf<Buffer>());
        try
        {
            Marshal.StructureToPtr(new Buffer { Size = (name.Length + 1) * 2, Type = 45, Data = namePointer }, bufferPointer, false);
            var descriptor = new Buffers { Version = 0, Count = 1, Data = bufferPointer };
            var imported = NCryptImportKey(provider, IntPtr.Zero, "PKCS8_PRIVATEKEY", ref descriptor, out var key, pkcs8, pkcs8.Length, Machine | Silent | 0x400);
            using (key)
            {
                Check(imported);
                try
                {
                    // Set the boundary before finalization persists private material. Never overwrite/adopt.
                    Check(NCryptSetProperty(key, "Security Descr", security, security.Length, 5));
                    Check(NCryptSetProperty(key, "Export Policy", BitConverter.GetBytes(0), sizeof(int), 0));
                    Check(NCryptFinalizeKey(key, Silent));
                }
                catch { if (NCryptDeleteKey(key, Silent) == 0) key.SetHandleAsInvalid(); throw; }
            }
        }
        finally { Marshal.FreeHGlobal(bufferPointer); Marshal.FreeHGlobal(namePointer); }
    }
    internal static byte[] Security(SafeNCryptKeyHandle key)
    {
        Check(NCryptGetProperty(key, "Security Descr", null, 0, out var length, 5));
        if (length is <= 0 or > 65536) throw new CryptographicException("Invalid native key security descriptor size.");
        var bytes = new byte[length]; Check(NCryptGetProperty(key, "Security Descr", bytes, bytes.Length, out _, 5)); return bytes;
    }
    internal static List<string> Names(SafeNCryptProviderHandle provider, string prefix)
    {
        var names = new List<string>(); var state = IntPtr.Zero;
        try
        {
            while (true)
            {
                var status = NCryptEnumKeys(provider, null, out var pointer, ref state, Machine | Silent);
                try
                {
                    if (status == NoMoreItems) break;
                    Check(status);
                    var native = Marshal.PtrToStructure<KeyName>(pointer);
                    var name = Marshal.PtrToStringUni(native.Name);
                    if (name is not null && name.StartsWith(prefix, StringComparison.Ordinal)) names.Add(name);
                }
                finally { if (pointer != IntPtr.Zero) NCryptFreeBuffer(pointer); }
            }
        }
        finally { if (state != IntPtr.Zero) NCryptFreeBuffer(state); }
        return names;
    }
    internal static void Delete(SafeNCryptKeyHandle key)
    { Check(NCryptDeleteKey(key, Silent)); key.SetHandleAsInvalid(); }
    private static void Check(int status) { if (status != 0) throw new CryptographicException($"Native TLS cache operation failed (0x{status:X8})."); }
    [StructLayout(LayoutKind.Sequential)] private struct Buffer { internal int Size, Type; internal IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] private struct Buffers { internal int Version, Count; internal IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyName { internal IntPtr Name, Algorithm; internal int Legacy, Flags; }
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)] private static extern int NCryptOpenStorageProvider(out SafeNCryptProviderHandle provider, string name, uint flags);
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)] private static extern int NCryptOpenKey(SafeNCryptProviderHandle provider, out SafeNCryptKeyHandle key, string name, int legacy, uint flags);
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)] private static extern int NCryptImportKey(SafeNCryptProviderHandle provider, IntPtr importKey, string type, ref Buffers parameters, out SafeNCryptKeyHandle key, byte[] data, int length, uint flags);
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)] private static extern int NCryptSetProperty(SafeNCryptKeyHandle key, string property, byte[] data, int length, uint flags);
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)] private static extern int NCryptGetProperty(SafeNCryptKeyHandle key, string property, byte[]? data, int length, out int result, uint flags);
    [DllImport("ncrypt.dll")] private static extern int NCryptFinalizeKey(SafeNCryptKeyHandle key, uint flags);
    [DllImport("ncrypt.dll")] private static extern int NCryptDeleteKey(SafeNCryptKeyHandle key, uint flags);
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)] private static extern int NCryptEnumKeys(SafeNCryptProviderHandle provider, string? scope, out IntPtr name, ref IntPtr state, uint flags);
    [DllImport("ncrypt.dll")] private static extern int NCryptFreeBuffer(IntPtr buffer);
}
