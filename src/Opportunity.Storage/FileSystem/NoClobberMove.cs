using System.Runtime.InteropServices;

namespace Opportunity.Storage.FileSystem;

/// <summary>
/// Atomically publishes a staged file at a path that must not exist yet. <see cref="File.Move(string, string, bool)"/>
/// without overwrite checks existence and then renames on Unix, which lets two concurrent writers both succeed; link(2)
/// fails with <c>EEXIST</c> atomically. On Windows, MoveFileEx without REPLACE_EXISTING is already atomic.
/// </summary>
internal static class NoClobberMove
{
    private const int EEXIST = 17; // Linux and macOS.

    /// <returns>False when <paramref name="destination"/> already exists; the source is left in place.</returns>
    public static bool TryMove(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                File.Move(source, destination, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(destination))
            {
                return false;
            }
        }

        if (Link(source, destination) == 0)
        {
            File.Delete(source);
            return true;
        }

        var errno = Marshal.GetLastPInvokeError();
        if (errno == EEXIST)
        {
            return false;
        }

        throw new IOException($"Could not publish the staged object (errno {errno}). The store root must support hard links.");
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link([MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);
}
