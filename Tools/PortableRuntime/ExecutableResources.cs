using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class ExecutableResources
{
    delegate bool EnumType(IntPtr module, IntPtr type, IntPtr parameter);
    delegate bool EnumName(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);
    delegate bool EnumLanguage(IntPtr module, IntPtr type, IntPtr name, ushort language, IntPtr parameter);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll")]
    static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool EnumResourceTypes(IntPtr module, EnumType callback, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumName callback, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool EnumResourceLanguages(IntPtr module, IntPtr type, IntPtr name, EnumLanguage callback, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr FindResourceEx(IntPtr module, IntPtr type, IntPtr name, ushort language);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")]
    static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr BeginUpdateResource(string path, bool deleteExisting);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool UpdateResource(IntPtr update, IntPtr type, IntPtr name, ushort language, IntPtr data, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool EndUpdateResource(IntPtr update, bool discard);

    public static void Copy(string source, string destination)
    {
        // Load as data only: preserve Unity's icons, version and DPI/UAC manifest
        // without executing the original bootstrap or changing its resources.
        IntPtr module = LoadLibraryEx(source, IntPtr.Zero, 0x22);
        if (module == IntPtr.Zero) throw new Win32Exception();
        IntPtr update = IntPtr.Zero;
        try
        {
            update = BeginUpdateResource(destination, true);
            if (update == IntPtr.Zero) throw new Win32Exception();
            int error = 0;
            bool success = EnumResourceTypes(module, (m, type, p) =>
                EnumResourceNames(m, type, (n, t, name, q) =>
                    EnumResourceLanguages(n, t, name, (l, rt, rn, language, r) =>
                    {
                        IntPtr resource = FindResourceEx(l, rt, rn, language);
                        uint size = SizeofResource(l, resource);
                        IntPtr data = LockResource(LoadResource(l, resource));
                        if (data != IntPtr.Zero && UpdateResource(update, rt, rn, language, data, size)) return true;
                        error = Marshal.GetLastWin32Error();
                        return false;
                    }, IntPtr.Zero), IntPtr.Zero), IntPtr.Zero);
            if (!success) throw new Win32Exception(error != 0 ? error : Marshal.GetLastWin32Error());
            IntPtr completed = update;
            update = IntPtr.Zero;
            if (!EndUpdateResource(completed, false)) throw new Win32Exception();
        }
        finally
        {
            if (update != IntPtr.Zero) EndUpdateResource(update, true);
            FreeLibrary(module);
        }
    }
}
