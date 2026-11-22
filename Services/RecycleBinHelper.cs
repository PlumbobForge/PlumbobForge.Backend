using System;
using System.Runtime.InteropServices;

namespace PlumbobForge.Backend.Services
{
    public static class RecycleBinHelper
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT FileOp);

        private const uint FO_DELETE = 0x0003;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_SILENT = 0x0004;

        public static bool SendToRecycleBin(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (PlumbobForge.Backend.Configuration.AppPaths.IsSandboxed) return MoveToSandboxBin(path);

            try
            {
                var shf = new SHFILEOPSTRUCT
                {
                    wFunc = FO_DELETE,
                    // pFrom must be double null-terminated
                    pFrom = path + '\0' + '\0',
                    fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT
                };

                int result = SHFileOperation(ref shf);
                return result == 0 && !shf.fAnyOperationsAborted;
            }
            catch
            {
                return false;
            }
        }

        // Sandbox mode (tests, copied databases) keeps "recycled" files in the sandbox, out of the user's Recycle Bin.
        private static bool MoveToSandboxBin(string path)
        {
            try
            {
                string bin = System.IO.Path.Combine(PlumbobForge.Backend.Configuration.AppPaths.AppDataDir, "RecycleBin", Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(bin);
                string target = System.IO.Path.Combine(bin, System.IO.Path.GetFileName(path.TrimEnd('\\', '/')));
                if (System.IO.Directory.Exists(path)) System.IO.Directory.Move(path, target);
                else System.IO.File.Move(path, target);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
