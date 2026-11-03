// Copyright (c) Snap Hutao RP. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Snap.Hutao.Remastered.Service.Game.Coexistence;

public static partial class GameRefsBlockClone
{
    public sealed record Extent(long Vcn, long Lcn, long Clusters);

    public static long GetClusterSize(string path)
    {
        if (!GetDiskFreeSpace(GetVolume(path), out uint sectors, out uint bytes, out _, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return checked((long)sectors * bytes);
    }

    public static long GetFileAllocatedBytes(string path)
    {
        uint low = GetCompressedFileSize(path, out uint high);
        if (low == uint.MaxValue && Marshal.GetLastWin32Error() != 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return checked((long)(((ulong)high << 32) | low));
    }

    public static unsafe IReadOnlyList<Extent> GetExtents(string path)
    {
        // ReFS may report LCN=-1 for recently buffered writes until allocation is
        // committed. Flush writable files before taking the physical-block snapshot.
        bool writable = (File.GetAttributes(path) & FileAttributes.ReadOnly) == 0;
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, writable ? FileAccess.ReadWrite : FileAccess.Read, FileShare.Read);
        if (writable) { RandomAccess.FlushToDisk(handle); }
        List<Extent> extents = [];
        byte[] buffer = new byte[64 * 1024];
        long start = 0;
        fixed (byte* output = buffer)
        {
            while (true)
            {
                bool success = GetRetrievalPointers(handle, 0x00090073, &start, sizeof(long), output, buffer.Length, out int returned, 0);
                int error = Marshal.GetLastWin32Error();
                if (!success && error == 38) { break; } // An empty/unallocated file.
                if (!success && error != 234) { throw new Win32Exception(error); }
                if (returned < 16) { throw new IOException("Invalid file extent response."); }
                int count = *(int*)output;
                if (count < 0 || count > (returned - 16) / 16) { throw new IOException("Invalid file extent count."); }
                long vcn = *(long*)(output + 8);
                for (int index = 0; index < count; index++)
                {
                    long next = *(long*)(output + 16 + index * 16);
                    long lcn = *(long*)(output + 24 + index * 16);
                    if (next <= vcn) { throw new IOException("Invalid file extent range."); }
                    if (lcn >= 0) { extents.Add(new(vcn, lcn, next - vcn)); }
                    vcn = next;
                }

                if (success) { break; }
                if (vcn <= start) { throw new IOException("File extent query made no progress."); }
                start = vcn;
            }
        }

        return extents;
    }

    // Re-share identical data without replacing file names, attributes or ACLs.
    // Exclusive write sharing and fresh hashes prevent a stale scan from changing data.
    public static async Task<long> DeduplicateAsync(string sourcePath, string targetPath, string expectedSha256, CancellationToken token)
    {
        EnsureReFS(sourcePath);
        if (Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase)
            || !GetVolume(sourcePath).Equals(GetVolume(targetPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Optimization requires independent files on the same ReFS volume.");
        }

        using FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous);
        using FileStream target = new(targetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous);
        if (source.Length != target.Length
            || !Convert.ToHexString(await SHA256.HashDataAsync(source, token).ConfigureAwait(false)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)
            || !Convert.ToHexString(await SHA256.HashDataAsync(target, token).ConfigureAwait(false)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("资源自扫描后发生变化，已停止优化，请重新扫描。");
        }

        long cluster = GetClusterSize(sourcePath);
        long bytes = source.Length / cluster * cluster;
        for (long offset = 0; offset < bytes;)
        {
            token.ThrowIfCancellationRequested();
            long length = Math.Min(bytes - offset, 1024L * 1024 * 1024) / cluster * cluster;
            DuplicateRange range = new() { Source = source.SafeFileHandle.DangerousGetHandle(), SourceOffset = offset, TargetOffset = offset, Length = length };
            if (!DeviceIoControl(target.SafeFileHandle, 0x00098344, ref range, Marshal.SizeOf<DuplicateRange>(), 0, 0, out _, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            offset += length;
        }

        target.Flush(true);
        return bytes;
    }

    public static unsafe string GetVolume(string path)
    {
        char* mount = stackalloc char[1024];
        char* volume = stackalloc char[1024];
        if (!GetVolumePathName(Path.GetFullPath(path), mount, 1024)
            || !GetVolumeNameForVolumeMountPoint(new string(mount), volume, 1024))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new string(volume);
    }

    public static unsafe bool IsVolumeMountPoint(string path)
    {
        char* volume = stackalloc char[1024];
        return GetVolumeNameForVolumeMountPoint(Path.TrimEndingDirectorySeparator(path) + "\\", volume, 1024);
    }

    public static void RemoveOfflineMountPoint(string path)
    {
        if (!DeleteVolumeMountPoint(Path.TrimEndingDirectorySeparator(path) + "\\"))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public static long GetAvailableBytes(string path)
    {
        if (!GetDiskFreeSpaceEx(path, out ulong available, out _, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return checked((long)available);
    }

    public static unsafe void EnsureReFS(string path)
    {
        string volume = GetVolume(path);
        char* format = stackalloc char[32];
        if (!GetVolumeInformation(volume, 0, 0, out _, out _, out _, format, 32))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (!new string(format).Equals("ReFS", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Virtual disk coexistence requires a ReFS volume. No hard-link fallback is allowed.");
        }
    }

    // Distinct file objects share clusters; every later write is isolated by ReFS.
    // Only full clusters are cloned. The unaligned tail is copied normally.
    public static async Task<long> CloneAsync(string sourcePath, string targetPath, CancellationToken token = default)
    {
        if (Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase)
            || !GetVolume(sourcePath).Equals(GetVolume(Path.GetDirectoryName(targetPath)!), StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Block cloning requires distinct files on the same ReFS volume.");
        }

        EnsureReFS(sourcePath);
        string volume = GetVolume(sourcePath);
        if (!GetDiskFreeSpace(volume, out uint sectors, out uint sectorBytes, out _, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        long cluster = checked((long)sectors * sectorBytes);
        if (cluster <= 0)
        {
            throw new IOException("Invalid ReFS cluster size.");
        }

        using FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous);
        await using FileStream target = new(targetPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
        target.SetLength(source.Length);
        long cloned = source.Length / cluster * cluster;
        for (long offset = 0; offset < cloned;)
        {
            token.ThrowIfCancellationRequested();
            long length = Math.Min(cloned - offset, 1024L * 1024 * 1024);
            length = length / cluster * cluster;
            DuplicateRange range = new() { Source = source.SafeFileHandle.DangerousGetHandle(), SourceOffset = offset, TargetOffset = offset, Length = length };
            if (!DeviceIoControl(target.SafeFileHandle, 0x00098344, ref range, Marshal.SizeOf<DuplicateRange>(), 0, 0, out _, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ReFS block cloning failed; coexistence was not completed.");
            }

            offset += length;
        }

        source.Position = cloned;
        target.Position = cloned;
        await source.CopyToAsync(target, token).ConfigureAwait(false);
        target.Flush(true);
        return cloned;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DuplicateRange
    {
        public nint Source;
        public long SourceOffset;
        public long TargetOffset;
        public long Length;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true, EntryPoint = "GetVolumePathNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumePathName(string path, char* volumePath, int length);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true, EntryPoint = "GetVolumeNameForVolumeMountPointW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumeNameForVolumeMountPoint(string mount, char* volume, int length);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true, EntryPoint = "DeleteVolumeMountPointW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteVolumeMountPoint(string mount);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true, EntryPoint = "GetVolumeInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumeInformation(string root, nint name, int nameLength, out uint serial, out uint maxComponent, out uint flags, char* format, int formatLength);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true, EntryPoint = "GetDiskFreeSpaceW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpace(string root, out uint sectors, out uint bytes, out uint freeClusters, out uint totalClusters);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true, EntryPoint = "GetDiskFreeSpaceExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(string path, out ulong available, out ulong total, out ulong free);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle handle, uint code, ref DuplicateRange range, int size, nint output, int outputSize, out int returned, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true, EntryPoint = "DeviceIoControl")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetRetrievalPointers(SafeFileHandle handle, uint code, long* input, int inputSize, byte* output, int outputSize, out int returned, nint overlapped);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true, EntryPoint = "GetCompressedFileSizeW")]
    private static partial uint GetCompressedFileSize(string path, out uint high);
}
