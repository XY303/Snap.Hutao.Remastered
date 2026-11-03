// Copyright (c) DGP Studio. All rights reserved.
// Licensed under the MIT license.

namespace Snap.Hutao.Remastered.Service.Game.Package;

public interface IPackageConverter
{
    ValueTask<(int Files, long Bytes)> CreateCoexistingResourceAsync(PackageConverterContext context, string targetExecutable, Func<Task> finishInstallation, IProgress<string> progress, CancellationToken token);

    ValueTask<bool> EnsureGameResourceAsync(PackageConverterContext context);

    ValueTask EnsureDeprecatedFilesAndSDKAsync(PackageConverterDeprecationContext context);
}
