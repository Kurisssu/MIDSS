// This file is intentionally a stub. The handler approach was abandoned because
// HttpClientFactory caches DelegatingHandlers across Blazor Server circuits (per
// HandlerLifetime, default 2 min), which causes a scoped AuthTokenStore captured at
// handler construction to leak between circuits and serve stale tokens.
//
// Token attachment now lives in SitimApiClient.AttachToken() (correctly scoped per
// circuit), and proactive refresh lives in SitimAuthStateProvider via System.Timers
// (scheduled at login and after cookie restore).
//
// Keeping this empty file so any leftover references don't break the build.

namespace Sitim.Web.Services;
