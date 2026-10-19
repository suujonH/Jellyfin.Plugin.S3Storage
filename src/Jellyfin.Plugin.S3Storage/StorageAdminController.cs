using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.S3Storage;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Plugins/S3Storage")]
public sealed class StorageAdminController(StorageSupervisor supervisor, WorkerDeployer deployer) : ControllerBase
{
    [HttpGet("Status")]
    public IActionResult Status() => Ok(supervisor.Status);

    [HttpPost("Worker/Build")]
    public async Task<IActionResult> Build(Configuration.StorageBucket bucket, CancellationToken ct)
    {
        try { await deployer.DeployAsync(bucket, true, ct); return Ok(new { Message = "Worker 构建验证完成，未发布。" }); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return BadRequest(new { Message = ex.Message }); }
    }

    [HttpPost("Worker/{index:int}/Deploy")]
    public async Task<IActionResult> Deploy(int index, bool dryRun, CancellationToken ct)
    {
        var buckets = Plugin.Instance.Configuration.Buckets;
        if (index < 0 || index >= buckets.Length) return NotFound();
        try
        {
            await deployer.DeployAsync(buckets[index], dryRun, ct);
            return Ok(new { Message = dryRun ? "Worker 构建验证完成，未发布。" : "Worker 与密钥已更新。" });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return BadRequest(new { Message = ex.Message }); }
    }
}
