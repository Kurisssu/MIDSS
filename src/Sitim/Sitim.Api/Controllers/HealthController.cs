using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Sitim.Api.Controllers
{
    [AllowAnonymous]
    [ApiController]
    [Route("api/health")]
    public sealed class HealthController : ControllerBase
    {
        [HttpGet]
        public IActionResult Get() => Ok(new { status = "ok" });
    }
}
