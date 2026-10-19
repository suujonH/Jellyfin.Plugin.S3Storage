# S3 Storage for Jellyfin

[![CI](https://github.com/suujonH/Jellyfin.Plugin.S3Storage/actions/workflows/ci.yml/badge.svg)](https://github.com/suujonH/Jellyfin.Plugin.S3Storage/actions/workflows/ci.yml)

A Jellyfin server plugin that ties local directories to S3 buckets and lets clients load the files in those directories straight from the storage service. A mapping names a local directory, usually a mount of the bucket, and the bucket and key prefix behind it. Jellyfin scans and reads the directory like any other folder. When Jellyfin is about to return a file from a mapped directory to a client, the plugin answers with a redirect to a link for that file instead: through a Cloudflare Worker, as an S3 pre-signed URL or as a public bucket address. The plugin also keeps a persistent probe cache for mapped files and a metadata cache for STRM files, can run the s3fs mounts on Linux, and can deploy the Cloudflare Worker.

The plugin works on its own. It does not reference any other plugin and does not know what other plugins serve: everything it does is driven by file paths.

Version 0.2.0, plugin GUID `7637e67a-7fd1-45a2-8b34-e69170cb470f`, targetAbi `13.0.0.0`.

## Features

- **Mappings** from local directories to S3 buckets and key prefixes, for any S3-compatible service.
- **Direct delivery.** Any file response for a file in a public mapping becomes a `302` redirect to the storage, after Jellyfin has authorized the request: originals and downloads, subtitles, trickplay tiles, unprocessed images, playlists, and files returned by other plugins.
- **Four delivery modes**: a Cloudflare Worker with HMAC grants, S3 pre-signed URLs with signed copies of HLS playlists, public bucket URLs, or Private (the server delivers the file).
- **HLS-aware links.** The link of a playlist authorizes its whole directory, so the segments it references by relative URI are loaded from the storage too.
- **Hour-aligned links** that stay the same within an hour, with cacheable redirects.
- **Persistent probe cache** for mapped files, so a metadata refresh does not read every file through the mount again.
- **STRM support**: cached media information for STRM items in a mapping, and a direct redirect to the STRM target for static requests.
- **Managed s3fs mounts** on Linux, with a guard that keeps an unmounted directory from looking like an empty library.
- **Worker deployment** from the settings page with Wrangler, including the secrets.

## Requirements

- A Jellyfin server built from commit [`aec42b345e1a7da25ee5492183f12209338df688`](https://github.com/jellyfin/jellyfin/commit/aec42b345e1a7da25ee5492183f12209338df688) (targetAbi 13.0.0.0, .NET 10). The plugin is built against that source.
- An S3-compatible service that accepts path-style requests, and credentials for the bucket.
- For managed mounts:
  - Linux with FUSE and s3fs-fuse 1.90, or 1.93 and later. Versions 1.91–1.92 do not list directories that exist only as key prefixes, which hides objects uploaded directly by other tools.
  - `fusermount3` on the PATH; it is used to unmount.
  - The Jellyfin user must own the mount directories, because the plugin changes their permissions, and must be allowed to use FUSE. *Allow other users* (`MountAllowOther`) needs `user_allow_other` in `/etc/fuse.conf`.
  - On other systems, or for mounts managed by the system, map directories that are mounted by other means.
- For the Cloudflare delivery Worker: Node.js 22 or later and Wrangler (installed by `npm ci` in `worker/`), and a Cloudflare account. Deployment from the settings page runs `node` on the Jellyfin host.
- To build: the .NET 10 SDK and the Jellyfin source at the commit above as a sibling folder `../jellyfin`, or `-p:JellyfinSourceRoot=<path>`. PowerShell 7 (`pwsh`) for `scripts/Stage-Plugin.ps1`.

## Installation

1. Build the plugin with the Jellyfin source next to this repository (see [Building and testing](#building-and-testing)):

   ```sh
   dotnet build src/Jellyfin.Plugin.S3Storage -c Release
   ```

2. Stage it into Jellyfin's plugin folder (the `plugins` folder of Jellyfin's data directory, for example `/var/lib/jellyfin/plugins` with the Debian packages):

   ```sh
   pwsh ./scripts/Stage-Plugin.ps1 -PluginDirectory /var/lib/jellyfin/plugins/S3Storage_0.2.0.0
   ```

   `-PluginDirectory` is the only parameter and is required; the folder is created when missing. The script builds the Release configuration (with the Jellyfin source in `../jellyfin`; set the `JellyfinSourceRoot` environment variable for another location), copies `Jellyfin.Plugin.S3Storage.dll`, `AWSSDK.S3.dll` and `AWSSDK.Core.dll`, and writes the `meta.json` manifest. Do not copy Jellyfin's own assemblies.

3. Restart Jellyfin. The plugin appears as **S3 Storage** under Dashboard → Plugins.
4. For the *Cloudflare Worker* delivery mode, deploy the delivery Worker (see [Cloudflare delivery Worker](#cloudflare-delivery-worker)).

## Configuration

The settings page (Dashboard → Plugins → S3 Storage) and the plugin's messages are in Chinese; the label of each setting is listed below. Mappings are validated when the configuration is saved with the plugin enabled.

### Plugin settings

| Setting | Label | Default | Meaning |
|---|---|---|---|
| `Enabled` | 启用 S3 存储 | off | Turns the plugin on. While it is off nothing is mapped: no redirects, no probe or STRM caching, and managed mounts are unmounted. |
| `PlaybackTokenHours` | 播放授权有效期（小时） | 24 | 1–168. How long a link stays valid after the end of the current hour; see [Hour-aligned links](#hour-aligned-links-and-cache-control). It should cover the length of a film plus pauses. |
| `Buckets` | 存储映射 | none | The mappings, edited one at a time with *Add mapping* (添加映射) and *Remove mapping* (删除映射). |

### Mapping fields

| Field | Label | Default | Meaning |
|---|---|---|---|
| `Name` | 名称 | `S3` (the page suggests `S3 1`, `S3 2`, ...) | Display name. |
| `Endpoint` | S3 服务地址 | | Required `http` or `https` URL of the S3 service. Requests use path-style addressing. |
| `Region` | 区域 | `us-east-1` | Signing region. |
| `Bucket` | 存储桶 | | Required bucket name. |
| `KeyPrefix` | 初始路径 | empty | The key prefix that the local directory corresponds to; no `.` or `..` segments. |
| `LocalPath` | 本地映射目录 | | Required absolute local directory, usually the mount point. A file `LocalPath/a/b.mkv` is the object `KeyPrefix/a/b.mkv`. When mappings are nested, the longest `LocalPath` wins. |
| `AccessKey`, `SecretKey` | Access Key ID, Secret Access Key | | Required credentials. |
| `DeliveryMode` | 公开访问方式 | `Worker` | `Worker`, `Presigned`, `Public` or `Private`; see below. |
| `PublicBaseUrl` | Worker 或存储桶公开地址 | | For `Worker` and `Public`: the address of the delivery Worker or of the public bucket, `http` or `https` without query or fragment. |
| `SigningSecret` | Worker 签名密钥 | | For `Worker`: at least 32 bytes, identical to the Worker's `PLAYBACK_SECRET`. *Generate secret* (生成密钥) fills in 64 random hexadecimal characters. |
| `MountWithS3fs` | 由插件管理 S3FS 挂载（Linux） | off | The plugin runs s3fs for `LocalPath`; see [S3FS mount guard](#s3fs-mount-guard). Requires an `AccessKey` without `:` or line breaks and a `SecretKey` without line breaks. |
| `S3fsExecutable` | S3FS 程序路径 | `/usr/bin/s3fs` | Absolute path of s3fs, used with `MountWithS3fs`. |
| `MountAllowOther` | 允许其他系统用户访问（需要 FUSE 配置） | off | Mounts with `-o allow_other`. |
| `WorkerProjectDirectory` | Worker 项目目录 | | Absolute path of the `worker/` folder on the Jellyfin host, after `npm ci`. Used for deployment. |
| `WorkerName` | Worker 名称 | `jellyfin-s3-delivery` | Name of the Cloudflare Worker: lowercase letters, digits and `-`, at most 63 characters, not starting with `-`. |
| `AllowedOrigins` | 允许的来源（CORS） | `*` | The Worker's `ALLOWED_ORIGINS`: `*`, or a comma-separated list of origins exactly as a browser sends them (scheme, host and a non-default port, without path or trailing slash), for example `https://jellyfin.example.com,http://192.168.1.10:8096`. A change takes effect when the Worker is deployed again. |
| `CloudflareAccountId` | Cloudflare Account ID | | Account for deployment. |
| `CloudflareApiToken` | Cloudflare API Token | | API token for deployment. |
| `AutoDeployWorker` | 保存后自动部署 Worker（包括密钥变化） | off | Deploys the Worker and its secrets whenever the saved mapping has changed. Requires `WorkerProjectDirectory`, `CloudflareAccountId` and `CloudflareApiToken`. |

### Delivery modes

| Mode | Label | Link for a file |
|---|---|---|
| `Worker` | Cloudflare Worker | `{PublicBaseUrl}/media/{token}/{key}`. The token is an HMAC grant for the file, or for the directory of a playlist. The delivery Worker verifies it, fetches the object with its own credentials and rewrites playlists so that every reference keeps the grant. It never calls Jellyfin. |
| `Presigned` | S3 预签名 | A pre-signed `GET` URL. A playlist is answered with a signed copy of its playlist graph, published below `.jellyfin-delivery/` (see [Playlist scope](#playlist-scope)). |
| `Public` | 公开存储桶 | `{PublicBaseUrl}/{key}`. |
| `Private` | 不提供公开链接 | No link; Jellyfin delivers the file itself. |

For `Presigned` and `Public` delivery to the web client, the bucket's CORS rules must allow the web client's origin. The delivery Worker answers CORS itself, according to `AllowedOrigins`.

### STRM

There are no STRM-specific settings. STRM handling applies to every `.strm` item inside a mapping: its media information is cached (see [STRM cache](#strm-cache)), and static requests can be redirected to its target (see [STRM redirect](#strm-redirect)).

### Actions and status

- **Test connection** (测试连接) validates the mapping on the page and lists one object below `KeyPrefix`.
- **Validate Worker build** (验证 Worker 构建) runs a Wrangler dry run for the mapping on the page, without saving the settings and without publishing.
- **Deploy and update secrets** (部署并更新密钥) saves the settings, then deploys the Worker of the selected mapping and uploads its secrets.
- **Refresh status** (刷新状态) shows the state of the selected mapping, for example `S3FS 已挂载` (s3fs mounted), `使用外部管理的目录` (directory mounted by other means), `S3FS 已退出（n）。修改配置后可重试。` (s3fs exited with code n), or the last error. Before the settings have been applied it shows `等待同步配置`.

Administrators (or API keys) can call the same functions over HTTP:

| Method and path | Purpose |
|---|---|
| `POST /Plugins/S3Storage/Test` | Validates the mapping in the body and lists one object. |
| `GET /Plugins/S3Storage/Status` | State per `LocalPath`. |
| `POST /Plugins/S3Storage/Worker/Build` | Wrangler dry run for the mapping in the body. |
| `POST /Plugins/S3Storage/Worker/{index}/Deploy` | Deploys the saved mapping at position `index`, counted from 0 (`?dryRun=true` for a dry run). |
| `POST /Plugins/S3Storage/Metadata/{itemId}` | Imports STRM media information; see [STRM cache](#strm-cache). |

## How delivery works

### Result filter

After Jellyfin, and any result filters of other plugins, have decided to answer a `GET` or `HEAD` request with a file (a `PhysicalFileResult`, or a `FileStreamResult` over an open file), the plugin's result filter checks whether the file lies in a mapping whose delivery mode is not *Private*. If it does, the response becomes a `302` redirect to the file's link. The filter does not look at controllers or request parameters, so it covers images, playlists and any other file the server returns. Images that Jellyfin has to resize or convert are served from its image cache, which is not in a mapping, and are delivered by the server.

The filter is the innermost result filter (order `int.MaxValue - 1000`). Result filters of other plugins run before it and still see the original file response, so a plugin can refuse or rewrite a response first.

- The redirect drops the `ETag`, `Last-Modified`, `Age`, `Content-Disposition` and `Content-Type` headers that an action set for the file, so a client never renews an outdated redirect with a conditional request.
- If a link cannot be created (for example because the storage service is unreachable while a pre-signed playlist is being published), a warning is logged and the server delivers the file itself.

### Playlist scope

A file whose name ends in `.m3u8` gets a link whose authorization covers the playlist's directory and everything below it, so the files it references by relative URI stay authorized:

- **Worker**: the grant's prefix is the playlist's directory. The delivery Worker rewrites every URI of the playlist into a `/media/{token}/{key}` link with the same grant, and refuses references outside the prefix.
- **Presigned**: the plugin reads the playlist from S3 and resolves every URI (plain lines and `URI="..."` attributes) relative to it. Each reference must stay inside the playlist's directory. Media files are replaced by pre-signed URLs and nested playlists by signed copies, recursively. The copies are published to `{KeyPrefix}/.jellyfin-delivery/{expiry}/{hash}/` (`.jellyfin-delivery/` at the bucket root when `KeyPrefix` is empty), and the redirect points to the pre-signed copy of the requested playlist. Requests for the same playlist within the same five-minute window of expiry times share one copy; publishing times out after 2 minutes. Once an hour, copies whose expiry has passed are deleted. The credentials therefore need write and delete access to that folder.
- **Public**: the link is the playlist's public address, and relative URIs resolve against the public bucket.

### Hour-aligned links and Cache-Control

A link expires at the end of the current hour (UTC) plus `PlaybackTokenHours`, but at most seven days minus one minute after it was issued, the longest lifetime of an S3 pre-signed URL. Within one hour every request for a file therefore receives the same link, and clients may cache the redirect itself:

- redirects for playlists are sent with `Cache-Control: private, no-store`;
- redirects for all other files with `Cache-Control: private, max-age=<seconds until the end of the hour>`.

### STRM redirect

A static request (`static=true`) to `/Videos/{id}/stream` or `/Audio/{id}/stream` for a `.strm` item whose `.strm` file lies in a mapping that is not *Private* is answered with a `302` redirect to the STRM target before Jellyfin opens the remote stream, with `Cache-Control: private, no-store`. This happens when the target is an HTTP URL that needs no custom request headers and the user is not set to *Force remote source transcoding*. A user without playback permission is refused. Everything else is left to Jellyfin and to the result filter above. Whoever produces the STRM files is responsible for the target URL and its validity.

## Probe cache and STRM cache

Both caches live in Jellyfin's data folder (`DataPath`, for example `/var/lib/jellyfin/data`), not in its cache folder, which Jellyfin purges periodically.

### Probe cache

For an existing local file inside a mapping, Jellyfin's probe requests (`IMediaEncoder.GetMediaInfo`) are answered from the cache while the file is unchanged. Everything else, such as remote sources, files outside the mappings, disc folders and every other encoder function, goes to Jellyfin's own encoder.

- Location: `DataPath/s3-storage/probe/{first two characters of the key}/{key}.json`. The key is the SHA-256 of the full path, the chapter extraction flag and the media type.
- Invalidation: an entry is used only while its stored path, size and modification time match the file. A changed file is probed again and its entry replaced. Corrupt entries are deleted; a failed write is logged as a warning and never fails the probe.
- There is no eviction: entries of deleted files stay until you remove them. Deleting the `probe` folder is always safe and makes the next refresh probe again.

### STRM cache

The media information of a video item whose path ends in `.strm`, that lies in a mapping and has a target, is stored after the first successful playback probe that found a video stream, and reused when the item is opened later.

- Location: `DataPath/s3-storage/strm/{item id}.json`.
- Invalidation: an entry is used only while the `.strm` path, its target and the item's modification date are unchanged; a corrupt entry is deleted.
- Only the media information is cached. The source list, the user's default tracks and the user's transcoding and remuxing permissions are applied fresh for each request.
- `POST /Plugins/S3Storage/Metadata/{itemId}` with a `MediaSourceInfo` in the body stores media information in advance, so that the first playback needs no probe. It requires a run time and a video stream with width and height. Jellyfin's library scans and explicit metadata refreshes are not changed.

## S3FS mount guard

With `MountWithS3fs`, the plugin runs s3fs for the mapping's `LocalPath` on Linux. A background loop (every 10 seconds) mounts a mapping when it is added, enabled or changed, and unmounts it when it is removed, disabled or changed, and on shutdown.

- **Mounting.** The directory must be empty or not exist yet. The credentials are written to `DataPath/s3-storage/mounts/{SHA-256 of the directory}.passwd` with mode `0600`, and s3fs runs in the foreground: `<S3fsExecutable> <Bucket>[:/<KeyPrefix>] <LocalPath> -f -o passwd_file=<file> -o url=<Endpoint> -o use_path_request_style`, plus `-o allow_other` with `MountAllowOther`. The mapping's `Region` is not passed to s3fs. Unmounting uses `fusermount3 -u`.
- **Mount detection.** The plugin reads `/proc/self/mountinfo` and waits up to 30 seconds for a `fuse.s3fs` mount at exactly that path; the state then reads mounted, failed to start (with the exit code), or not mounted within 30 seconds (s3fs is then ended). Because the path is compared with the mount table, enter the real path of the directory: a path through a symbolic link is never found mounted.
- **Existing mounts.** A FUSE mount found on the directory, for example one left behind by a crash, is unmounted first, with a lazy unmount if it is still in use. A directory mounted with another file system is refused.
- **Permissions 0750 and 000.** If a managed directory is not served by a mount, a library scan would see an empty folder and Jellyfin would remove every item below it. To prevent that, the plugin sets the directory's mode to `000` whenever it is not mounted: when starting fails, when s3fs exits, and after unmounting. The scan then fails to read the folder, and Jellyfin keeps its library. Right before the next mount attempt the mode is set to `0750` (`rwxr-x---`) again; a directory that turns out not to be empty is closed again and reported (`S3FS 挂载目录必须为空或不存在。`).
- **Root.** Permissions do not apply to root. If Jellyfin runs as root, this guard has no effect.

### Limitations

- If s3fs exits, the mount directory is locked (mode `000`; a mount left behind by a crashed s3fs is not readable either) and the plugin does not remount automatically. To remount, change a setting of that mapping and save, which makes the plugin process the mapping again (saving unchanged settings does not), or restart Jellyfin.
- Managed mounts are available on Linux only; on other systems the state reports that the mount needs Linux and FUSE.
- Mounting, unmounting and the directory guard have not yet been verified against a real s3fs mount.

## Cloudflare delivery Worker

The `worker/` folder contains the Cloudflare Worker for the *Worker* delivery mode.

- It answers `GET` and `HEAD` for `/media/{token}/{key}` and `OPTIONS` for CORS preflight; other methods get `405`, other paths `404`, and without `PLAYBACK_SECRET` every request gets `503`.
- The token is checked before the storage is contacted: version 1, not expired, the key within the grant's prefix (an exact key, a directory prefix ending in `/`, or empty for the whole bucket), and a valid HMAC-SHA256 signature. A missing or invalid grant gets `403`. Keys with `.` or `..` segments, backslashes or NUL characters get `400`.
- The object is fetched from `{S3_ENDPOINT}/{S3_BUCKET}/{key}` with SigV4 credentials. `Range` is forwarded for media files. A playlist is fetched whole and rewritten: every URI is resolved relative to it and must stay within the grant (otherwise `502`), and becomes a `/media/{token}/{key}` link on the Worker.
- Responses carry the object's `Content-Type`, `Content-Length`, `Content-Range`, `Accept-Ranges`, `ETag` and `Last-Modified` (the content type is set from the extension for `.m3u8`, `.mp4`, `.m4s`, `.vtt`, `.jpg` and `.png`), with `Cache-Control: private, no-store` for playlists and `private, max-age=60` otherwise. A missing object gives `404`, an unsatisfiable range `416`, other storage errors `502`.
- CORS: with `ALLOWED_ORIGINS` set to `*` every origin is allowed; otherwise the request's `Origin` is echoed when it is in the list. Methods `GET, HEAD, OPTIONS`, request headers `Range, Content-Type`; `Content-Length`, `Content-Range`, `Accept-Ranges` and `ETag` are exposed.

### Variables

| Name | Kind | Meaning |
|---|---|---|
| `S3_ENDPOINT` | variable | S3 endpoint URL; objects are addressed path-style. |
| `S3_BUCKET` | variable | Bucket name. |
| `S3_REGION` | variable | Signing region, `us-east-1` when unset. |
| `ALLOWED_ORIGINS` | variable | `*` (when unset) or a comma-separated list of origins. |
| `PLAYBACK_SECRET` | secret | HMAC key of the grants, the mapping's `SigningSecret`. |
| `S3_ACCESS_KEY` | secret | Access key with read access to the bucket. |
| `S3_SECRET_KEY` | secret | Secret key. |

`worker/wrangler.jsonc` holds placeholder values for the variables and no account id or routes. For local development, copy `worker/.dev.vars.example` to `worker/.dev.vars` (ignored by git), fill in the three secrets and run `npm run dev`.

### Deployment by hand

```sh
cd worker
npm ci
# set S3_ENDPOINT, S3_REGION, S3_BUCKET and ALLOWED_ORIGINS in wrangler.jsonc
npx wrangler secret put PLAYBACK_SECRET
npx wrangler secret put S3_ACCESS_KEY
npx wrangler secret put S3_SECRET_KEY
npx wrangler deploy
```

Then set the mapping's delivery mode to *Cloudflare Worker*, `PublicBaseUrl` to the Worker's address and `SigningSecret` to the value of `PLAYBACK_SECRET`.

### Deployment from the settings page

Copy the `worker/` folder to the Jellyfin host, run `npm ci` in it, and set `WorkerProjectDirectory` to its absolute path. The plugin writes a temporary Wrangler configuration to `DataPath/s3-storage/deploy/` (name `WorkerName`, entry point `src/index.js` of the project, compatibility date `2026-10-01`, and the variables `S3_ENDPOINT`, `S3_BUCKET`, `S3_REGION` and `ALLOWED_ORIGINS` from the mapping), runs the project's Wrangler (`node <WorkerProjectDirectory>/node_modules/wrangler/bin/wrangler.js deploy --config <file> --keep-vars`) with `CLOUDFLARE_API_TOKEN` and `CLOUDFLARE_ACCOUNT_ID` set and metrics disabled, and then uploads `PLAYBACK_SECRET`, `S3_ACCESS_KEY` and `S3_SECRET_KEY` from the mapping with `wrangler secret bulk`. A state file in the same folder records what was deployed, so automatic deployment only runs when the mapping has changed. A dry run (*Validate Worker build*) writes its output to `DataPath/s3-storage/deploy/dry-run` and needs no Cloudflare credentials. Deployment to a real Cloudflare account has not yet been verified.

### Tests

`npm test` runs the Worker's tests with `node --test`: grants carried through rewritten master and media playlists without contacting Jellyfin, expired, tampered and out-of-scope grants refused before the storage is contacted, `Range` and `HEAD` handling, and playlist references that try to leave the authorized directory. `npm run build` runs a Wrangler dry run into `../artifacts/worker`.

## Building and testing

```sh
git clone https://github.com/jellyfin/jellyfin.git
git -C jellyfin checkout aec42b345e1a7da25ee5492183f12209338df688
git clone https://github.com/suujonH/Jellyfin.Plugin.S3Storage.git
cd Jellyfin.Plugin.S3Storage
dotnet build src/Jellyfin.Plugin.S3Storage -c Release
dotnet test tests/Jellyfin.Plugin.S3Storage.Tests -c Release
cd worker
npm ci
npm test
```

The plugin is built against the Jellyfin server source at commit `aec42b345e1a7da25ee5492183f12209338df688` (targetAbi 13.0.0.0, .NET 10). The build expects that source as the sibling folder `../jellyfin`; pass `-p:JellyfinSourceRoot=<path>` when it is elsewhere. Warnings are treated as errors. The CI workflow in `.github/workflows/ci.yml` runs the same commands on Ubuntu with Node.js 22.

## Related projects

- [Jellyfin.Plugin.DistributedHls](https://github.com/suujonH/Jellyfin.Plugin.DistributedHls): pre-encoded HLS versions, encoded ahead of time by Workers. When its output directory lies in a public mapping of this plugin, its playlists and segments are delivered from the storage.
- [Jellyfin.DistributedHls.Worker](https://github.com/suujonH/Jellyfin.DistributedHls.Worker): the encoding Worker of Distributed HLS.
- [Jellyfin.Plugin.S3Storage](https://github.com/suujonH/Jellyfin.Plugin.S3Storage): this plugin.
- [jellyfin-web, branch `distributed-hls`](https://github.com/suujonH/jellyfin-web/tree/distributed-hls): optional changes to the legacy web item details page for Distributed HLS.

The two plugins are independent: each works without the other, and neither references the other.

## License

Licensed under the GNU General Public License v3.0. See [LICENSE](LICENSE).
