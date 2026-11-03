# 本地 Release 发布

Visual Studio 选择应用项目的 `FolderProfile` 配置，或在仓库根目录执行：

```powershell
dotnet publish src/Snap.Hutao.Remastered/Snap.Hutao.Remastered/Snap.Hutao.Remastered.csproj -c Release -p:Platform=x64 -p:PublishProfile=FolderProfile
```

默认输出位于应用项目的 `bin/x64/Release/net11.0-windows10.0.26100.0/win-x64/publish/`。可通过 `-p:PublishDir=D:\Relesses\Snap.Hutao\` 指定本机目录。

配置保留 .NET 与 Windows App SDK 自包含部署，并启用 ReadyToRun 和项目现有的产物校验。不要启用 `PublishSingleFile`：当前项目保留 MSIX 支持，Windows App SDK 会直接报错 `PublishSingleFile only supports unpackaged apps`。切勿仅为绕过发布错误而改变整个项目的 `WindowsPackageType`。

## EXE 安装包

安装 Inno Setup，将 `ISCC.exe` 加入 PATH，然后运行仓库已有的打包流程：

```powershell
dotnet tool restore
dotnet cake build-installer.cake
```

该流程发布 Release、自包含运行库，复制应用清单和资源至 `Installer/Publish/`，并生成 `publish/*-Setup.exe`。本地打包不会签名。安装包版本默认读取 `Installer/installer.iss` 中的 `MyAppVersion`，发布新版本前应使其与应用清单版本保持一致。

也可在已完成目录发布后手动打包。先将完整发布目录复制至 `Installer/Publish/`，再按 `build-installer.cake` 的 `Copy files` 步骤复制生成的 `AppxManifest.xml`、项目 `Assets` 与 `Resource`，最后执行：

```powershell
ISCC.exe /DMyAppVersion=2.0.2.0 Installer/installer.iss
```

目录压缩包也应使用同一份完整的 `Installer/Publish/` 内容。不要只分发主 EXE；它需要同目录中的程序集、原生库与资源。
