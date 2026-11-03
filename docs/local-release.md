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

## 本地签名及安装时导入根证书

使用 PowerShell 7.4 或以上版本，在已准备好完整 `Installer/Publish/` 后运行：

```powershell
.\scripts\SignLocalRelease.ps1 `
  -PayloadDirectory .\Installer\Publish `
  -InnoSetupCompiler 'C:\工具路径\Inno Setup\ISCC.exe' `
  -SignTool 'D:\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe' `
  -CreateCertificate
```

首次运行生成本仓库专用的 `XY303` 根 CA 和代码签名证书。两者都限定为代码签名用途；根 CA 最多签发叶证书。私钥不可导出，保存在当前 Windows 用户的 `My` 证书存储，公钥指纹保存在 `%LOCALAPPDATA%\SnapHutaoRemastered\LocalSigning\identity.json`。后续运行复用同一身份，不需要再次加 `-CreateCertificate`。请保留该 Windows 用户的证书和密钥；脚本不会生成可备份的 PFX，也不会使用官方根证书冒充官方签名。

脚本检查签名证书确实由待打包根证书签发，然后在独立目录中为主 EXE、主程序集、安装程序及卸载程序签名，并请求 RFC 3161 时间戳。输出默认位于 `artifacts/local-signed-<版本>/`，包含安装包、Release ZIP、两个公开 `.cer` 和 SHA256 清单；可用 `-OutputDirectory` 指定其他目录。

安装包会在复制应用文件前，将内置根证书导入本机的“受信任的根证书颁发机构”。导入前验证 DER 证书文件的指纹，导入失败则中止安装；取消或失败时尝试回滚本次新增的信任。卸载仅删除该安装包添加且有匹配所有权标记的根证书，保留原先已存在的证书。

构建脚本不会在构建电脑上添加根信任。首次运行安装包时根证书尚未安装，因此仍可能出现 Windows 发布者或 SmartScreen 提示。ZIP 本身不执行证书导入。签名和本地导入根证书不保证获得 SmartScreen 信誉。

手动编译可指定 `/DRootCertificatePath=<DER 公钥证书>` 和 `/DRootCertificateThumbprint=<40 位指纹>`；指纹使用 SHA1 作为 X.509 证书标识，文件签名使用 SHA256。不提供根证书参数时，现有 CI 的叶证书 `TrustedPeople` 流程继续生效。官方仓库历史上使用过根证书导入，目前安装脚本使用叶证书信任；本地根证书模式是本仓库单独启用的选项。

## Visual Studio 一键构建签名安装包

普通“生成 Release”只编译应用，“发布 FolderProfile”只生成发布目录。要生成带完整资源和本地签名的 EXE 安装包，在 Visual Studio 的“工具 → 外部工具 → 添加”配置以下项目：

- 标题：`构建胡桃签名安装包`
- 命令：PowerShell 7.4+ 的 `pwsh.exe` 完整路径（不是 Windows 自带的 `powershell.exe`）。
- 初始目录：`$(SolutionDir)..\..`，适用于 `src/Snap.Hutao.Remastered/Snap.Hutao.Remastered.slnx`。
- 勾选“使用输出窗口”。
- 参数如下，替换 Inno Setup 和 Windows SDK 工具路径：

```text
-NoProfile -File "$(SolutionDir)..\..\scripts\BuildInstaller.ps1" -InnoSetupCompiler "C:\工具路径\Inno Setup\ISCC.exe" -SignTool "D:\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
```

该入口使用解决方案所在仓库的源码，自动执行 Release/x64 自包含发布，将生成清单及完整 `Assets`、`Resource` 目录复制到独立打包目录，然后复用已有本地证书签名。默认输出为仓库根目录的 `artifacts/installer/`，包含安装包、ZIP、公开证书和校验清单；可追加 `-OutputDirectory "D:\Relesses\MyBuild"`。普通 F5/生成不会触发打包。

准备条件：本机安装项目所需 .NET 11 SDK（当前为预览版本，VS 中需允许使用预览 .NET SDK）、Windows SDK、PowerShell 7.4+、Inno Setup，并已按上节生成本地签名身份。应用版本取自 `Package.appxmanifest`；日后发布新版本时更新该文件中的 `Identity Version`。现有签名私钥只能由创建它的 Windows 用户使用。
