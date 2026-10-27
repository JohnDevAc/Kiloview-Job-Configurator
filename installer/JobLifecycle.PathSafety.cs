using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NdiLifecycle {
    public static class CloudPath {
        [StructLayout(LayoutKind.Sequential)]
        private struct AttributeTag { public uint Attributes; public uint Tag; }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError=true)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out AttributeTag value, uint size);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
        public static bool IsCloudTag(uint tag) {
            // IO_REPARSE_TAG_CLOUD and CLOUD_1..CLOUD_F are not name surrogates.
            return (tag & 0xFFFF0FFFu) == 0x9000001Au;
        }
        public static bool IsAllowed(string path) {
            using (var file=CreateFile(path,0,7,IntPtr.Zero,3,0x02200000,IntPtr.Zero)) {
                if(file.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                AttributeTag value;
                if(!GetFileInformationByHandleEx(file,9,out value,8)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if(!IsCloudTag(value.Tag)) return false;
                var finalPath=new StringBuilder(32768);
                var count=GetFinalPathNameByHandle(file,finalPath,(uint)finalPath.Capacity,0);
                if(count==0 || count>=finalPath.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
                var resolved=finalPath.ToString();
                if(resolved.StartsWith(@"\\?\",StringComparison.Ordinal)) resolved=resolved.Substring(4);
                return string.Equals(Path.GetFullPath(path).TrimEnd('\\'),resolved.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
