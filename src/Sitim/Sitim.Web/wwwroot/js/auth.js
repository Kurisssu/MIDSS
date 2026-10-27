// Auth cookie helpers — called from Blazor via IJSRuntime.
// Stores BOTH the short-lived JWT access token AND the long-lived refresh token
// in HttpOnly cookies via the Web's AuthCookieController.
window.sitimAuth = {
    setCookie: async function (token, expiresInSeconds, refreshToken, refreshExpiresInSeconds) {
        await fetch('/auth/cookie/set', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                token: token,
                expiresInSeconds: expiresInSeconds,
                refreshToken: refreshToken || null,
                refreshExpiresInSeconds: refreshExpiresInSeconds || null
            })
        });
    },
    clearCookie: async function () {
        await fetch('/auth/cookie/clear', { method: 'POST' });
    }
};
