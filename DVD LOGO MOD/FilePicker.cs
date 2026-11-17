using System;
using System.Runtime.InteropServices;

namespace DVDLogoMod
{
    public static class FilePicker
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private class OpenFileName
        {
            public int structSize;
            public IntPtr owner;
            public IntPtr instance;
            public string filter;
            public string customFilter;
            public int maxCustFilter;
            public int filterIndex;
            public IntPtr file;
            public int maxFile;
            public string fileTitle;
            public int maxFileTitle;
            public string initialDir;
            public string title;
            public int flags;
            public short fileOffset;
            public short fileExtension;
            public string defExt;
            public IntPtr custData;
            public IntPtr hook;
            public string templateName;
            public IntPtr reserved;
            public int reservedInt;
            public int flagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetOpenFileNameW")]
        private static extern bool GetOpenFileName([In, Out] OpenFileName ofn);

        public static string Open()
        {
            const int size = 1024;
            IntPtr buf = Marshal.AllocHGlobal(size * 2);

            try
            {
                Marshal.WriteInt16(buf, 0);

                OpenFileName ofn = new OpenFileName
                {
                    structSize = Marshal.SizeOf(typeof(OpenFileName)),
                    filter = "Images\0*.png;*.gif\0\0",
                    file = buf,
                    maxFile = size,
                    title = "Select logo",
                    flags = 0x1808
                };

                return GetOpenFileName(ofn) ? Marshal.PtrToStringUni(buf) : null;
            }
            catch (Exception ex)
            {
                DVDLogoPlugin.Log.LogWarning($"Dialog error: {ex.Message}");
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
    }
}
