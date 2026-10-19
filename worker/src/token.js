const encoder = new TextEncoder();

function decodeBase64Url(value) {
    return Uint8Array.from(atob(value.replace(/-/g, '+').replace(/_/g, '/')), c => c.charCodeAt(0));
}

export function inScope(key, prefix) {
    return prefix === '' || (prefix.endsWith('/') ? key.startsWith(prefix) : key === prefix);
}

export async function verifyToken(token, objectKey, secret, now = Date.now()) {
    const parts = token.split('.');
    if (parts.length !== 2) return null;
    let signature;
    let grant;
    try {
        signature = decodeBase64Url(parts[1]);
        grant = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(decodeBase64Url(parts[0])));
    } catch { return null; }
    if (!grant || grant.v !== 1 || typeof grant.prefix !== 'string' || !Number.isSafeInteger(grant.exp)
        || grant.exp <= Math.floor(now / 1000) || !inScope(objectKey, grant.prefix)) return null;
    const key = await crypto.subtle.importKey('raw', encoder.encode(secret), { name: 'HMAC', hash: 'SHA-256' }, false, ['verify']);
    return await crypto.subtle.verify('HMAC', key, signature, encoder.encode(parts[0])) ? grant : null;
}
