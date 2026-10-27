using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TailRemote
{
    /// <summary>
    /// Windows' own folder picker with several folders allowed at once: the same dialog Send a folder always showed
    /// (FolderBrowserDialog is this dialog too, but .NET 8's cannot pick more than one). NVDA reads it like the file
    /// picker: Shift with the arrows chooses a run of folders, Control with the arrows and Space picks them one by one,
    /// and several names can be typed in the Folder box, each in quotes. Call on an STA thread.
    /// </summary>
    internal static class FolderPicker
    {
        /// <summary>The folders chosen (at least one), or null if the picker was closed without choosing.</summary>
        public static string[]? Pick(string title)
        {
            var dialog = (IFileOpenDialog)new FileOpenDialogClass();
            try
            {
                dialog.GetOptions(out uint options);
                dialog.SetOptions(options | PickFolders | ForceFileSystem | AllowMultiSelect | PathMustExist);
                dialog.SetTitle(title);
                int hr = dialog.Show(IntPtr.Zero);
                if (hr == Cancelled) return null;
                Marshal.ThrowExceptionForHR(hr);
                dialog.GetResults(out var results);
                var paths = new List<string>();
                try
                {
                    results.GetCount(out uint count);
                    for (uint i = 0; i < count; i++)
                    {
                        results.GetItemAt(i, out var item);
                        try
                        {
                            item.GetDisplayName(FileSystemPath, out IntPtr name);
                            try { if (Marshal.PtrToStringUni(name) is { Length: > 0 } path) paths.Add(path); }
                            finally { Marshal.FreeCoTaskMem(name); }
                        }
                        catch (COMException) { } // a place with no folder on disk behind it (should not happen with ForceFileSystem)
                        finally { Marshal.ReleaseComObject(item); }
                    }
                }
                finally { Marshal.ReleaseComObject(results); }
                return paths.Count > 0 ? paths.ToArray() : null;
            }
            finally { Marshal.ReleaseComObject(dialog); }
        }

        private const uint PickFolders = 0x20, ForceFileSystem = 0x40, AllowMultiSelect = 0x200, PathMustExist = 0x800;
        private const uint FileSystemPath = 0x80058000; // SIGDN_FILESYSPATH
        private const int Cancelled = unchecked((int)0x800704C7); // HRESULT_FROM_WIN32(ERROR_CANCELLED)

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogClass { }

        // The methods in Windows' own order (IModalWindow, IFileDialog, then IFileOpenDialog's two): the order is what
        // counts, so those never called are declared with plain pointers.
        [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr owner);
            void SetFileTypes(uint count, IntPtr specs);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IntPtr item);
            void SetFolder(IntPtr item);
            void GetFolder(out IntPtr item);
            void GetCurrentSelection(out IntPtr item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName(out IntPtr name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IntPtr item);
            void AddPlace(IntPtr item, int where);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
            void GetResults(out IShellItemArray items);
            void GetSelectedItems(out IntPtr items);
        }

        [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemArray
        {
            void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
            void GetPropertyStore(int flags, ref Guid iid, out IntPtr store);
            void GetPropertyDescriptionList(IntPtr key, ref Guid iid, out IntPtr list);
            void GetAttributes(int how, uint mask, out uint attributes);
            void GetCount(out uint count);
            void GetItemAt(uint index, out IShellItem item);
            void EnumItems(out IntPtr items);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint kind, out IntPtr name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }
    }
}
