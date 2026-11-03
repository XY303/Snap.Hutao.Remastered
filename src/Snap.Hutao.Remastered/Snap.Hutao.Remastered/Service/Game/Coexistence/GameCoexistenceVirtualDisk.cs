// Copyright (c) Snap Hutao RP. All rights reserved.
// Licensed under the MIT license.

using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.ComponentModel;

namespace Snap.Hutao.Remastered.Service.Game.Coexistence;

public sealed record GameVirtualDiskInfo(string Container, string VolumeName)
{
    public string ImagePath => Path.Combine(Container, "game.vhdx");
    public string MountPath => Path.Combine(Container, "volume");
    public string ChineseExecutable => Path.Combine(MountPath, "CN", "YuanShen.exe");
    public string OverseaExecutable => Path.Combine(MountPath, "OS", "GenshinImpact.exe");
}

public static class GameCoexistenceVirtualDisk
{
    public const string DescriptorName = "virtual-disk.json";
    private static readonly SemaphoreSlim MountGate = new(1, 1);

    public static string GetContainer(string sourceExecutable) => GameServerCoexistenceStorage.GetRoot(sourceExecutable) + ".hutao-vdisk";

    public static GameVirtualDiskInfo GetLayout(string sourceExecutable) => new(GetContainer(sourceExecutable), string.Empty);

    public static GameVirtualDiskInfo? Find(string executable)
    {
        // Discovery works even when the volume is offline: descriptor is on the host.
        string? directory = Path.GetDirectoryName(Path.GetFullPath(executable));
        while (directory is not null)
        {
            if (Path.GetFileName(directory).Equals("volume", StringComparison.OrdinalIgnoreCase)
                && Path.GetDirectoryName(directory) is { } container
                && container.EndsWith(".hutao-vdisk", StringComparison.OrdinalIgnoreCase))
            {
                return ReadDescriptor(container) ?? throw new InvalidDataException("共存虚拟磁盘描述文件丢失，禁止访问未挂载的目录。请恢复 virtual-disk.json 和原 VHDX。");
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    public static GameVirtualDiskInfo? ReadDescriptor(string container)
    {
        string path = Path.Combine(container, DescriptorName);
        GameServerCoexistenceStorage.EnsureOrdinaryPath(path);
        if (!File.Exists(path))
        {
            return null;
        }

        if (new FileInfo(path).Length > 16384)
        {
            throw new InvalidDataException("Virtual disk descriptor is too large.");
        }

        using FileStream stream = File.OpenRead(path);
        GameVirtualDiskInfo info = JsonSerializer.Deserialize(stream, GameServerCoexistenceJsonContext.Default.GameVirtualDiskInfo)
            ?? throw new InvalidDataException("Invalid virtual disk descriptor.");
        if (!Path.GetFullPath(container).Equals(info.Container, StringComparison.OrdinalIgnoreCase)
            || !container.EndsWith(".hutao-vdisk", StringComparison.OrdinalIgnoreCase)
            || !info.VolumeName.StartsWith("\\\\?\\Volume{", StringComparison.OrdinalIgnoreCase)
            || info.VolumeName.Length != 49 || !Guid.TryParse(info.VolumeName.Substring(11, 36), out _)
            || !info.VolumeName.EndsWith("}\\", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Invalid virtual disk identity.");
        }

        GameServerCoexistenceStorage.EnsureOrdinaryPath(info.ImagePath);
        return info;
    }

    public static async Task EnsureMountedAsync(string executable, CancellationToken token = default)
    {
        if (Find(executable) is not { } info)
        {
            return;
        }

        await EnsureMountedAsync(info, token).ConfigureAwait(false);
    }

    public static void AssertMounted(string executable)
    {
        if (Find(executable) is { } info)
        {
            if (!GameRefsBlockClone.IsVolumeMountPoint(info.MountPath)
                || !GameRefsBlockClone.GetVolume(info.MountPath + "\\").Equals(info.VolumeName, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("共存虚拟磁盘未挂载，请刷新游戏信息或使用“挂载并选择虚拟盘”。禁止向宿主挂载目录写入游戏文件。");
            }

            try
            {
                GameRefsBlockClone.EnsureReFS(info.MountPath + "\\");
            }
            catch (Win32Exception ex)
            {
                throw new IOException("共存虚拟磁盘已离线，请重新挂载后重试。", ex);
            }
        }
    }

    public static async Task EnsureMountedAsync(GameVirtualDiskInfo info, CancellationToken token = default)
    {
        await MountGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (GameRefsBlockClone.IsVolumeMountPoint(info.MountPath))
            {
                if (!GameRefsBlockClone.GetVolume(info.MountPath + "\\").Equals(info.VolumeName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("The mount directory belongs to a different volume. Refusing to use it.");
                }

                try
                {
                    GameRefsBlockClone.EnsureReFS(info.MountPath + "\\");
                    return;
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3 or 21 or 55)
                {
                    // Windows retains a volume mount-point entry after VHDX detach.
                    // Its GUID alone does not prove that the device is online.
                }

                RequireAdministrator();
                GameRefsBlockClone.RemoveOfflineMountPoint(info.MountPath);
            }

            RequireAdministrator();
            GameServerCoexistenceStorage.EnsureOrdinaryPath(info.MountPath);
            Directory.CreateDirectory(info.MountPath);
            if (Directory.EnumerateFileSystemEntries(info.MountPath).Any())
            {
                throw new IOException("The offline virtual disk mount directory is not empty.");
            }

            if (!File.Exists(info.ImagePath))
            {
                throw new FileNotFoundException("The coexistence VHDX is missing. Restore it to its original location.", info.ImagePath);
            }

            await RunStorageScriptAsync($$"""
                $image = {{Quote(info.ImagePath)}}
                $diskImage = Get-DiskImage -ImagePath $image
                if (!$diskImage.Attached) { Mount-DiskImage -ImagePath $image | Out-Null }
                $disk = Get-DiskImage -ImagePath $image | Get-Disk
                $partition = Get-Partition -DiskNumber $disk.Number | Where-Object { ($_.AccessPaths -contains {{Quote(info.VolumeName)}}) }
                if (@($partition).Count -ne 1) { throw 'The VHDX volume identity does not match the saved descriptor.' }
                $partition | Add-PartitionAccessPath -AccessPath {{Quote(info.MountPath)}}
                """).ConfigureAwait(false);
            if (!GameRefsBlockClone.GetVolume(info.MountPath + "\\").Equals(info.VolumeName, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("VHDX mount verification failed.");
            }

            GameRefsBlockClone.EnsureReFS(info.MountPath + "\\");
        }
        finally
        {
            MountGate.Release();
        }
    }

    public static async Task<GameVirtualDiskInfo> CreateAsync(string sourceExecutable, long sourceBytes, CancellationToken token)
    {
        RequireAdministrator();
        token.ThrowIfCancellationRequested();
        GameVirtualDiskInfo layout = GetLayout(sourceExecutable);
        GameServerCoexistenceStorage.EnsureOrdinaryPath(layout.Container);
        if (Directory.Exists(layout.Container) || File.Exists(layout.Container))
        {
            throw new IOException("The virtual disk destination already exists. It will never be overwritten.");
        }

        // Reserve room for an initial full copy plus a worst-case second independent
        // client and update growth. The host must still have room for the first copy.
        const long GiB = 1024L * 1024 * 1024;
        long maximumGiB = Math.Max(64, checked((sourceBytes * 3 + 32 * GiB + GiB - 1) / GiB));
        if (GameRefsBlockClone.GetAvailableBytes(Path.GetDirectoryName(layout.Container)!) < sourceBytes + 16 * GiB)
        {
            throw new IOException("Insufficient host space for a full game copy and 16 GiB of temporary space.");
        }

        Directory.CreateDirectory(layout.Container);
        Directory.CreateDirectory(layout.MountPath);
        // Formatting is confined to the just-created, exclusively owned image.
        // Existing VHDX files are never initialized or formatted by a mount operation.
        await RunStorageScriptAsync($$"""
            $image = {{Quote(layout.ImagePath)}}
            $mount = {{Quote(layout.MountPath)}}
            if (Test-Path -LiteralPath $image) { throw 'Refusing to overwrite an existing image.' }
            $commands = {{Quote(Path.Combine(layout.Container, "create-disk.txt"))}}
            $lines = @("create vdisk file=`"$image`" maximum={{maximumGiB * 1024}} type=expandable", "select vdisk file=`"$image`"", 'attach vdisk', 'exit')
            $encoding = [Text.Encoding]::GetEncoding([Text.Encoding]::Default.CodePage, [Text.EncoderFallback]::ExceptionFallback, [Text.DecoderFallback]::ExceptionFallback)
            [IO.File]::WriteAllLines($commands, [string[]]$lines, $encoding)
            try { & "$env:SystemRoot\System32\diskpart.exe" /s $commands | Out-Null } finally { Remove-Item -LiteralPath $commands -Force }
            $diskImage = Get-DiskImage -ImagePath $image
            if (!$diskImage.Attached) { throw 'The new VHDX did not attach.' }
            $disk = $diskImage | Get-Disk
            if ($disk.PartitionStyle -ne 'RAW' -or $disk.Size -ne {{maximumGiB * GiB}}) { throw 'Unexpected test disk identity/layout; refusing to format.' }
            $disk | Initialize-Disk -PartitionStyle GPT | Out-Null
            $partition = New-Partition -DiskNumber $disk.Number -UseMaximumSize
            $partition | Format-Volume -FileSystem ReFS -NewFileSystemLabel HutaoGame -Force -Confirm:$false | Out-Null
            $partition | Add-PartitionAccessPath -AccessPath $mount
            """).ConfigureAwait(false);
        GameRefsBlockClone.EnsureReFS(layout.MountPath + "\\");
        GameVirtualDiskInfo info = layout with { VolumeName = GameRefsBlockClone.GetVolume(layout.MountPath + "\\") };
        string descriptor = Path.Combine(info.Container, DescriptorName);
        using (FileStream stream = new(descriptor, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, info, GameServerCoexistenceJsonContext.Default.GameVirtualDiskInfo);
            stream.Flush(true);
        }

        token.ThrowIfCancellationRequested();

        // Probe actual FSCTL support before copying a full installation.
        string probe = Path.Combine(info.MountPath, "clone-probe.bin");
        string clone = probe + ".copy";
        try
        {
            await File.WriteAllBytesAsync(probe, new byte[1024 * 1024], token).ConfigureAwait(false);
            if (await GameRefsBlockClone.CloneAsync(probe, clone, token).ConfigureAwait(false) == 0)
            {
                throw new IOException("ReFS did not clone any blocks.");
            }
        }
        finally
        {
            File.Delete(clone);
            File.Delete(probe);
        }

        return info;
    }

    public static long GetEffectiveAvailableBytes(string path)
    {
        long available = GameRefsBlockClone.GetAvailableBytes(path);
        // Dynamic VHDX capacity is not guaranteed physical host capacity.
        if (Find(Path.Combine(path, "YuanShen.exe")) is { } info)
        {
            available = Math.Min(available, GameRefsBlockClone.GetAvailableBytes(info.Container));
        }

        return available;
    }

    private static void RequireAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new UnauthorizedAccessException("创建或重新挂载共存虚拟磁盘需要管理员权限，请以管理员身份重启胡桃。不会自动修改系统权限或安装组件。");
        }
    }

    private static string Quote(string value)
    {
        if (value.IndexOfAny(['\r', '\n', '"']) >= 0)
        {
            throw new InvalidDataException("Unsupported virtual disk path characters.");
        }

        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static async Task RunStorageScriptAsync(string body)
    {
        string script = "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; try {\n" + body + "\n} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        ProcessStartInfo start = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using Process process = Process.Start(start) ?? throw new IOException("Could not start Windows storage management.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        // Do not kill DiskPart/format halfway through on cancellation. Check the
        // cancellation token after provisioning has reached a stable boundary.
        await process.WaitForExitAsync().ConfigureAwait(false);
        await output.ConfigureAwait(false);
        string error = await errors.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new IOException("Windows 虚拟磁盘操作失败（需要系统支持 ReFS）：" + error);
        }
    }
}
