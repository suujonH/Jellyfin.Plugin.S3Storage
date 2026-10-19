import { AwsClient } from 'aws4fetch';
import { verifyToken } from './token.js';
import { escapeKey, readKey, rewritePlaylist } from './playlist.js';

const mimeTypes = { m3u8: 'application/vnd.apple.mpegurl', mp4: 'video/mp4', m4s: 'video/iso.segment', vtt: 'text/vtt', jpg: 'image/jpeg', png: 'image/png' };

function cors(request, env) {
    const headers = new Headers();
    const origin = request.headers.get('Origin');
    const allowed = (env.ALLOWED_ORIGINS || '*').split(',').map(s => s.trim());
    if (allowed.includes('*')) headers.set('Access-Control-Allow-Origin', '*');
    else if (origin && allowed.includes(origin)) headers.set('Access-Control-Allow-Origin', origin);
    headers.set('Vary', 'Origin');
    headers.set('Access-Control-Allow-Methods', 'GET, HEAD, OPTIONS');
    headers.set('Access-Control-Allow-Headers', 'Range, Content-Type');
    headers.set('Access-Control-Expose-Headers', 'Content-Length, Content-Range, Accept-Ranges, ETag');
    return headers;
}

export function createHandler(fetchObject) {
    return async function handle(request, env) {
        const responseHeaders = cors(request, env);
        const error = (status, message) => new Response(message, { status, headers: responseHeaders });
        if (request.method === 'OPTIONS') return new Response(null, { status: 204, headers: responseHeaders });
        if (request.method !== 'GET' && request.method !== 'HEAD') return error(405, 'Method not allowed');
        if (!env.PLAYBACK_SECRET) return error(503, 'Delivery is not configured');
        const url = new URL(request.url);
        const match = /^\/media\/([^/]+)\/(.+)$/.exec(url.pathname);
        if (!match) return error(404, 'Not found');
        let objectKey;
        try { objectKey = readKey(match[2]); }
        catch (e) { if (e instanceof URIError) return error(400, 'Invalid object key'); throw e; }
        const grant = await verifyToken(match[1], objectKey, env.PLAYBACK_SECRET);
        if (!grant) return error(403, 'Playback authorization expired or invalid');

        const isPlaylist = objectKey.toLowerCase().endsWith('.m3u8');
        const headers = new Headers();
        if (!isPlaylist && request.headers.has('Range')) headers.set('Range', request.headers.get('Range'));
        let upstream;
        try { upstream = await fetchObject(objectKey, { method: isPlaylist ? 'GET' : request.method, headers }, env); }
        catch { return error(502, 'Storage is unavailable'); }
        if (!upstream.ok) {
            await upstream.body?.cancel();
            return error(upstream.status === 404 ? 404 : upstream.status === 416 ? 416 : 502, 'Object is unavailable');
        }
        for (const name of ['Content-Type', 'Content-Length', 'Content-Range', 'Accept-Ranges', 'ETag', 'Last-Modified']) {
            if (upstream.headers.has(name)) responseHeaders.set(name, upstream.headers.get(name));
        }
        const extension = objectKey.split('.').pop().toLowerCase();
        if (mimeTypes[extension]) responseHeaders.set('Content-Type', mimeTypes[extension]);
        responseHeaders.set('Cache-Control', isPlaylist ? 'private, no-store' : 'private, max-age=60');
        if (isPlaylist) {
            let body;
            try { body = rewritePlaylist(await upstream.text(), objectKey, grant.prefix, `${url.origin}/media/${match[1]}/`); }
            catch (e) { if (e instanceof URIError || e instanceof TypeError) return error(502, 'Invalid playlist resource'); throw e; }
            responseHeaders.delete('Content-Length');
            responseHeaders.delete('Content-Range');
            responseHeaders.delete('ETag');
            return new Response(request.method === 'HEAD' ? null : body, { headers: responseHeaders });
        }
        return new Response(request.method === 'HEAD' ? null : upstream.body, { status: upstream.status, headers: responseHeaders });
    };
}

async function fetchS3(key, init, env) {
    const client = new AwsClient({ accessKeyId: env.S3_ACCESS_KEY, secretAccessKey: env.S3_SECRET_KEY, region: env.S3_REGION || 'us-east-1', service: 's3' });
    const target = `${env.S3_ENDPOINT.replace(/\/$/, '')}/${encodeURIComponent(env.S3_BUCKET)}/${escapeKey(key)}`;
    return client.fetch(target, init);
}

export default { fetch: createHandler(fetchS3) };
