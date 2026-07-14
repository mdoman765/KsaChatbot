using System.Text.Json;

namespace crud_app_backend.Bot.Services
{
    /// <summary>
    /// Wraps Meta's official WhatsApp Cloud API directly (v21.0).
    ///   Send text/image/catalog:  POST https://graph.facebook.com/v21.0/{PhoneNumberId}/messages
    ///   Media lookup:             GET  https://graph.facebook.com/v21.0/{mediaId}
    ///   Auth:                     Authorization: Bearer {AccessToken}  (registered as "Meta" named client)
    /// </summary>
    public class DialogClient : IDialogClient
    {
        private readonly IHttpClientFactory _factory;
        private readonly IConfiguration _config;
        private readonly ILogger<DialogClient> _logger;

        public DialogClient(IHttpClientFactory factory, IConfiguration config,
            ILogger<DialogClient> logger)
        {
            _factory = factory;
            _config = config;
            _logger = logger;
        }

        private string PhoneNumberId => _config["Meta:PhoneNumberId"] ?? "";

        private string MessagesUrl =>
            $"https://graph.facebook.com/v21.0/{PhoneNumberId}/messages";

        // ── Send text ─────────────────────────────────────────────────────────

        public async Task SendTextAsync(string phone, string message,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            var client = _factory.CreateClient("Meta");
            var payload = new
            {
                messaging_product = "whatsapp",
                to = phone,
                type = "text",
                text = new { body = message }
            };

            var resp = await client.PostAsJsonAsync(MessagesUrl, payload, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("[Meta] SendText failed {Code} to {Phone}: {Body}",
                    (int)resp.StatusCode, phone, body.Length > 200 ? body[..200] : body);
            }
            else
            {
                var okBody = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogInformation("[Meta] Text sent to {Phone}: {Body}", phone,
                    okBody.Length > 300 ? okBody[..300] : okBody);
            }
        }

        // ── Send image with caption ───────────────────────────────────────────
        // imageUrl = public HTTPS URL of the image on your server.
        // Meta fetches the image from this URL and delivers it to WhatsApp.
        // No upload to Meta needed — served directly from your wwwroot/images/.

        public async Task SendImageAsync(string phone, string imageUrl, string caption,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                _logger.LogWarning("[Meta] SendImage — no imageUrl, falling back to text");
                await SendTextAsync(phone, caption, ct);
                return;
            }

            var client = _factory.CreateClient("Meta");
            var payload = new
            {
                messaging_product = "whatsapp",
                to = phone,
                type = "image",
                image = new
                {
                    link = imageUrl, // Meta fetches image from this public URL
                    caption = caption   // shown below the image in WhatsApp
                }
            };

            var resp = await client.PostAsJsonAsync(MessagesUrl, payload, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("[Meta] SendImage failed {Code} to {Phone}: {Body}",
                    (int)resp.StatusCode, phone, body.Length > 200 ? body[..200] : body);

                // Always fallback to text so user is never left with silence
                _logger.LogInformation("[Meta] Falling back to text for {Phone}", phone);
                await SendTextAsync(phone, caption, ct);
            }
            else
            {
                _logger.LogInformation("[Meta] Image sent to {Phone}", phone);
            }
        }

        // ── Download media ────────────────────────────────────────────────────

        public async Task<(byte[] Data, string MimeType)> DownloadMediaAsync(
            string mediaId, string fallbackMime,
            CancellationToken ct = default)
        {
            var client = _factory.CreateClient("Meta");

            // Step 1 — get the CDN download URL for this media ID
            var metaResp = await client.GetAsync(
                $"https://graph.facebook.com/v21.0/{mediaId}", ct);
            metaResp.EnsureSuccessStatusCode();

            var metaJson = await metaResp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(metaJson);

            var url = doc.RootElement.TryGetProperty("url", out var urlEl)
                ? urlEl.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException(
                    $"Meta returned no URL for mediaId={mediaId}");

            // Step 2 — download binary. The lookaside.fbsbx.com URL Meta returns
            // must be fetched with the SAME Bearer auth header (unlike 360dialog,
            // there's no separate proxy — the "Meta" client already carries it).
            var binResp = await client.GetAsync(url, ct);
            binResp.EnsureSuccessStatusCode();

            var mime = binResp.Content.Headers.ContentType?.MediaType ?? fallbackMime;
            var bytes = await binResp.Content.ReadAsByteArrayAsync(ct);

            _logger.LogInformation("[Meta] Downloaded mediaId={Id}: {Bytes}b mime={Mime}",
                mediaId, bytes.Length, mime);

            return (bytes, mime);
        }

        // ── Send WhatsApp Catalog Message ─────────────────────────────────────

        public async Task SendCatalogMessageAsync(
            string phone,
            string bodyText,
            string footerText,
            string thumbnailSku,
            CancellationToken ct = default)
        {
            var client = _factory.CreateClient("Meta");

            var payload = new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to = phone,
                type = "interactive",
                interactive = new
                {
                    type = "catalog_message",
                    body = new
                    {
                        text = bodyText
                    },
                    footer = new
                    {
                        text = footerText
                    },
                    action = new
                    {
                        name = "catalog_message",
                        parameters = new
                        {
                            thumbnail_product_retailer_id = thumbnailSku
                        }
                    }
                }
            };

            var resp = await client.PostAsJsonAsync(MessagesUrl, payload, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);

                _logger.LogWarning(
                    "[Meta] SendCatalogMessage failed {Code} to {Phone}: {Body}",
                    (int)resp.StatusCode,
                    phone,
                    body.Length > 500 ? body[..500] : body);

                throw new Exception(
                    $"Catalog message failed ({(int)resp.StatusCode}): {body}");
            }

            _logger.LogInformation(
                "[Meta] Catalog message sent to {Phone}",
                phone);
        }
    }
}