using Amazon.S3;
using Amazon.S3.Model;
using Jellyfin.Api.Controllers;
using Jellyfin.Plugin.S3Storage.Configuration;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.S3Storage;

[ApiController]
[Route("Plugins/S3Storage")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class StorageController : ControllerBase
{
    [HttpPost("Test")]
    public async Task<ActionResult> Test([FromBody] StorageBucket bucket, CancellationToken cancellationToken)
    {
        try { StorageMapping.Validate(bucket); }
        catch (ArgumentException ex) { return BadRequest(new { Message = ex.Message }); }
        using var client = S3MediaStorage.CreateClient(bucket);
        try
        {
            await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket.Bucket, Prefix = bucket.KeyPrefix.Trim('/'), MaxKeys = 1
            }, cancellationToken).ConfigureAwait(false);
            return Ok(new { Message = "连接成功，可以访问存储桶。" });
        }
        catch (AmazonS3Exception ex)
        {
            return BadRequest(new { Message = $"S3 连接失败：{ex.ErrorCode ?? ex.StatusCode.ToString()}" });
        }
    }
}
