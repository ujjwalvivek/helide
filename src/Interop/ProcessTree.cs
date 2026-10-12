using System.Runtime.InteropServices;

namespace Helide.Interop;

internal static class ProcessTree
{
    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly nint InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    /// <summary>
    /// The process and everything it started, to any depth. Just the process itself when
    /// the list cannot be read.
    /// </summary>
    /// <remarks>
    /// A pane runs what it was given directly when that is an executable, and through
    /// <c>cmd</c> and <c>node</c> when it is an npm shim. The thing that holds a
    /// conversation is the last link of that chain, so the whole chain is searched.
    /// </remarks>
    public static HashSet<int> SelfAndDescendants(int root)
    {
        var result = new HashSet<int> { root };

        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandle || snapshot == 0)
            return result;

        try
        {
            var children = new Dictionary<int, List<int>>();
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };

            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            {
                var parent = (int)entry.th32ParentProcessID;
                if (!children.TryGetValue(parent, out var list))
                    children[parent] = list = [];

                list.Add((int)entry.th32ProcessID);
            }

            var pending = new Stack<int>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                if (!children.TryGetValue(pending.Pop(), out var list))
                    continue;

                // A process id can be reused, so a "child" may name an ancestor; the set
                // is what stops that from being followed round in circles.
                foreach (var child in list)
                    if (result.Add(child))
                        pending.Push(child);
            }
        }
        catch
        {
            // Whatever was gathered is still the right answer for the root.
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }
}
