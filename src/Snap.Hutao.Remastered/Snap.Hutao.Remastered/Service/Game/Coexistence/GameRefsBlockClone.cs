// Copyright (c) Snap Hutao RP. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Snap.Hutao.Remastered.Service.Game.Coexistence;

public static partial class GameRefsBlockClone
{
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
}
