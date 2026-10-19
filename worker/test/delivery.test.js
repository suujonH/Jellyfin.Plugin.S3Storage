import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHmac } from 'node:crypto';
import { createHandler } from '../src/index.js';
import { verifyToken } from '../src/token.js';

const secret = '0123456789abcdef0123456789abcdef';
const env = { PLAYBACK_SECRET: secret, ALLOWED_ORIGINS: '*' };
function token(prefix, exp = Math.floor(Date.now() / 1000) + 3600) {
    const payload = Buffer.from(JSON.stringify({ v: 1, prefix, exp })).toString('base64url');
    return payload + '.' + createHmac('sha256', secret).update(payload).digest('base64url');
}
function request(key, grant, init) { return new Request(`https://watch.example/media/${grant}/${key}`, init); }

test('master, audio, initialization and segment URLs keep the grant and bypass Jellyfin', async () => {
    const grant = token('film/revision/');
    const objects = new Map([
        ['film/revision/master.m3u8', '#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="a",URI="audio/index.m3u8"\n#EXT-X-STREAM-INF:BANDWIDTH=1000000,AUDIO="a"\nvideo/index.m3u8\n'],
        ['film/revision/video/index.m3u8', '#EXTM3U\n#EXT-X-MAP:URI="init.mp4"\n#EXTINF:6,\n000.m4s\n#EXT-X-ENDLIST\n'],
        ['film/revision/video/000.m4s', 'video bytes']
    ]);
    const seen = [];
    const handle = createHandler(async (key) => { seen.push(key); return new Response(objects.get(key), { status: objects.has(key) ? 200 : 404 }); });
    const master = await (await handle(request('film/revision/master.m3u8', grant), env)).text();
    assert.match(master, new RegExp(`/media/${grant.replaceAll('.', '\\.')}/film/revision/audio/index.m3u8`));
    const videoUrl = master.trim().split('\n').at(-1);
    const media = await (await handle(new Request(videoUrl), env)).text();
    assert.ok(media.includes(`/media/${grant}/film/revision/video/init.mp4`));
    const segmentUrl = media.split('\n').find(line => line.startsWith('https:'));
    const response = await handle(new Request(segmentUrl), env);
    assert.equal(await response.text(), 'video bytes');
    assert.deepEqual(seen, ['film/revision/master.m3u8', 'film/revision/video/index.m3u8', 'film/revision/video/000.m4s']);
});

test('expired, tampered and out-of-scope grants never fetch storage', async () => {
    let calls = 0;
    const handle = createHandler(async () => { calls++; return new Response('data'); });
    const grant = token('film/revision/');
    for (const [key, value] of [['film/revision/x.m4s', token('film/revision/', 1)], ['other/x.m4s', grant], ['film/revision/x.m4s', grant + 'x']]) {
        assert.equal((await handle(request(key, value), env)).status, 403);
    }
    assert.equal(calls, 0);
    assert.equal(await verifyToken(token('film/movie.mp4'), 'film/movie.mp4.backup', secret), null);
});

test('Range and HEAD preserve object delivery semantics', async () => {
    const handle = createHandler(async (key, init) => {
        assert.equal(key, 'film/init.mp4');
        assert.equal(init.headers.get('Range'), 'bytes=0-3');
        return new Response(init.method === 'HEAD' ? null : 'data', { status: 206, headers: { 'Content-Range': 'bytes 0-3/100', 'Content-Length': '4', 'Accept-Ranges': 'bytes' } });
    });
    for (const method of ['GET', 'HEAD']) {
        const response = await handle(request('film/init.mp4', token('film/'), { method, headers: { Range: 'bytes=0-3' } }), env);
        assert.equal(response.status, 206);
        assert.equal(response.headers.get('Content-Range'), 'bytes 0-3/100');
        assert.equal(await response.text(), method === 'HEAD' ? '' : 'data');
    }
});

test('playlist references cannot escape the authorized media directory', async () => {
    const handle = createHandler(async () => new Response('#EXTM3U\n../../other/secret.m4s\n'));
    assert.equal((await handle(request('film/revision/index.m3u8', token('film/revision/')), env)).status, 502);
});
