# 本地构建速度

应用项目的 Debug 和 Release 均默认使用快速本地构建：跳过构建时 Roslyn 诊断分析和代码风格检查，不生成 XML 文档。编译器错误、源码生成器、XAML 编译及打包／ReadyToRun 校验继续工作。Visual Studio 编辑器中的实时分析仍由原有设置控制。

Release 保留 C# 优化、SelfContained 和 Windows App SDK 随应用部署。普通本地“生成”不再额外执行将全部 ReadyToRun 产物复制回 bin 的发布流水线，但 Visual Studio 的 MSIX 工具自身仍可能预编译全部程序集。下方本地构建脚本默认明确传入 /p:PublishReadyToRun=false，跳过这部分预编译，输出由 JIT 编译的 IL；首次运行可能稍慢。“发布”仍默认启用 ReadyToRun 并验证产物，发布包的优化不变。需要本地 bin / F5 也预编译时使用脚本的 -ReadyToRun。

CI=true、GITHUB_ACTIONS=true、ContinuousIntegrationBuild=true 或 AotAnalysis=true 默认启用完整分析及生成时 ReadyToRun。需要本地执行完整检查时传入 /p:FullAnalysis=true；显式设置 RunAnalyzersDuringBuild、GenerateDocumentationFile 等 MSBuild 命令行属性仍优先。只调整 RunAnalyzersDuringBuild，不全局关闭 RunAnalyzers，因此编辑器实时诊断仍可使用；源码生成器继续运行。

## 构建命令

Visual Studio 直接选择 Debug / Release 构建即可应用新设置。首次恢复 NuGet 包后，日常使用“生成”而非“重新生成”，保留 obj 和 bin 的增量缓存。

仓库根目录运行：

```powershell
.\scripts\BuildLocal.ps1 -Configuration Debug
.\scripts\BuildLocal.ps1 -Configuration Release
.\scripts\BuildLocal.ps1 -Configuration Release -FullAnalysis
# 只开启生成时 ReadyToRun，仍跳过构建诊断分析。
.\scripts\BuildLocal.ps1 -Configuration Release -ReadyToRun
# 已恢复依赖时跳过恢复检查；可自行限制并行数。
.\scripts\BuildLocal.ps1 -Configuration Release -NoRestore -MaxCpuCount 8
```

脚本优先使用 Visual Studio MSBuild，找不到时使用 dotnet msbuild；默认按逻辑处理器数启用 MSBuild 并行和节点复用，并输出目标／任务耗时及总用时。命令行并行开关影响可并行的项目／任务，不会让单个 WinUI 项目中有先后依赖的 XAML 和 C# 阶段全部同时运行。因此总 CPU 利用率低不等于内存或线程配置不足。

也可直接运行 MSBuild，传入 /m /nr:true /clp:PerformanceSummary。该项目大量使用源生成代码和 WinUI 的多阶段编译，源码生成仍有固定成本。若需分析更细的等待时间，可自行添加 /bl:build.binlog，将日志保留在本机。

## 本机测量

本机验证均成功：Release 原配置生成约 294 秒，调整后 Visual Studio MSBuild 生成约 125 秒；使用最终本地脚本时 Release 约 73 秒、Debug 约 74 秒，日志中无 RunReadyToRunCompiler 任务。后续构建利用了已有缓存，因此这些差值不能视作纯配置收益，也不代表干净构建必然提速相同比例。主要固定成本仍在 C# 源码生成和 WinUI XAML 多阶段编译。
