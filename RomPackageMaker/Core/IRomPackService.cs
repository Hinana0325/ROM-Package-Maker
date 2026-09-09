namespace RomPackageMaker.Core;

/// <summary>
/// ROM 任务进度报告。
/// </summary>
/// <param name="Percent">进度百分比（0-100）。</param>
/// <param name="Stage">当前阶段描述（如"校验镜像"、"解压 system.img"）。</param>
/// <param name="Detail">可选的详细日志行。</param>
public sealed record RomTaskProgress(int Percent, string Stage, string? Detail = null);

/// <summary>
/// ROM 解包/打包核心服务。后续版本将提供基于 ext4/sparse/EROFS 解析的托管实现。
/// </summary>
public interface IRomPackService
{
    /// <summary>将 ROM 刷机包（zip）或分区镜像（system.img / boot.img / super.img）解包到工作目录。</summary>
    Task UnpackAsync(string sourcePath, string workspaceDir, IProgress<RomTaskProgress> progress, CancellationToken cancellationToken);

    /// <summary>将工作目录内容重新打包为可刷写的 zip 刷机包。</summary>
    /// <param name="sign">是否对输出 zip 进行签名（默认 true，使用内置测试证书）。</param>
    Task PackAsync(string workspaceDir, string outputPath, IProgress<RomTaskProgress> progress, CancellationToken cancellationToken, bool sign = true);
}
