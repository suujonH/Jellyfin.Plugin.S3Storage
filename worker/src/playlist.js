import { inScope } from './token.js';

export function escapeKey(key) {
    return key.split('/').map(encodeURIComponent).join('/');
}

export function readKey(path) {
    const parts = path.split('/').map(decodeURIComponent);
    if (parts.some(p => p === '.' || p === '..' || p.includes('\\') || p.includes('/') || p.includes('\0')))
        throw new URIError('Invalid object key');
    return parts.join('/');
}

export function rewritePlaylist(text, objectKey, prefix, deliveryBase) {
    const base = new URL('https://storage.invalid/' + escapeKey(objectKey));
    function rewrite(uri) {
        const target = new URL(uri, base);
        const key = readKey(target.pathname.slice(1));
        if (target.origin !== base.origin || !inScope(key, prefix))
            throw new URIError('Playlist resource is outside the playback grant');
        return deliveryBase + escapeKey(key);
    }
    return text.split(/\r?\n/).map(line => {
        if (!line.trim()) return line;
        if (line.startsWith('#')) return line.replace(/\bURI="([^"]*)"/g, (_, uri) => `URI="${rewrite(uri)}"`);
        return rewrite(line.trim());
    }).join('\n');
}
