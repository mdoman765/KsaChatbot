using System.Text.Json;
using crud_app_backend.Bot.Services;
using Microsoft.AspNetCore.Mvc;

namespace crud_app_backend.Bot.Controllers
{
    /// <summary>
    /// Receives Meta WhatsApp Cloud API webhook requests.
    ///
    /// IMPORTANT: fast response required for the POST endpoint.
    /// This controller does ONE thing — write body to WebhookQueue — and
    /// returns 200 OK immediately. No DB calls, no HTTP calls, no processing.
    /// WebhookProcessorService handles all actual processing in the background.
    /// </summary>
    [ApiController]
    [Route("webhook")]
    public class WhatsAppBotController : ControllerBase
    {
        private readonly WebhookQueue _queue;
        private readonly IConfiguration _config;
        private readonly ILogger<WhatsAppBotController> _logger;

        public WhatsAppBotController(
            WebhookQueue queue,
            IConfiguration config,
            ILogger<WhatsAppBotController> logger)
        {
            _queue = queue;
            _config = config;
            _logger = logger;
        }

        /// <summary>
        /// Meta's one-time webhook verification handshake. Called when you register/
        /// change the callback URL in Meta App Dashboard → WhatsApp → Configuration.
        /// 360dialog previously handled this on your behalf — connecting directly to
        /// Meta requires this endpoint to exist and echo back hub.challenge.
        /// </summary>
        [HttpGet("whatsapp-webhook")]
        public IActionResult VerifyWebhook(
            [FromQuery(Name = "hub.mode")] string? hubMode,
            [FromQuery(Name = "hub.verify_token")] string? hubVerifyToken,
            [FromQuery(Name = "hub.challenge")] string? hubChallenge)
        {
            var expectedToken = _config["Meta:VerifyToken"];

            if (hubMode == "subscribe" &&
                !string.IsNullOrEmpty(expectedToken) &&
                hubVerifyToken == expectedToken)
            {
                _logger.LogInformation("[Webhook] Verification succeeded");
                return Content(hubChallenge ?? "", "text/plain");
            }

            _logger.LogWarning("[Webhook] Verification FAILED — token mismatch");
            return Forbid();
        }

        /// <summary>
        /// Meta calls this on every incoming WhatsApp message.
        /// Returns 200 in &lt; 1ms. All processing is done by WebhookProcessorService.
        /// </summary>
        [HttpPost("whatsapp-webhook")]
        public IActionResult Webhook([FromBody] JsonElement body)
        {
            // TryEnqueue clones the JsonElement (safe after request scope ends)
            // and writes to the Channel. O(1), never blocks.
            if (!_queue.TryEnqueue(body))
            {
                // Queue full (extremely rare — only if 1000+ messages backlogged)
                _logger.LogWarning("[Webhook] Queue full — message dropped");
            }

            // 200 OK back to Meta in < 1ms
            return Ok(new { status = "received" });
        }

        /// <summary>Health check — monitoring can ping this.</summary>
        [HttpGet("health")]
        public IActionResult Health() => Ok(new { status = "ok", time = DateTime.UtcNow });
    }
}