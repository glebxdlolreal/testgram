using System.Net.Http.Headers;
using System.Text.Json;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Payments;

internal static class StripeHelper
{
    private static readonly HttpClient Http = new();

    /// <summary>Creates a Stripe PaymentIntent and returns its client_secret and id.</summary>
    public static async Task<(string ClientSecret, string PaymentIntentId)> CreatePaymentIntentAsync(
        string secretKey, long amountCents, string currency, string description)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.stripe.com/v1/payment_intents");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["amount"] = amountCents.ToString(),
            ["currency"] = currency.ToLower(),
            ["description"] = description,
            ["payment_method_types[]"] = "card",
        });

        var response = await Http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json).RootElement;

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Stripe error: {doc.GetProperty("error").GetProperty("message").GetString()}");

        return (
            doc.GetProperty("client_secret").GetString()!,
            doc.GetProperty("id").GetString()!
        );
    }

    /// <summary>
    /// Confirms a PaymentIntent with the card token the client's Stripe SDK produced and returns the
    /// PaymentMethod id it was charged to.
    /// </summary>
    public static async Task<string> ConfirmPaymentIntentAsync(string secretKey, string paymentIntentId, string tokenId)
    {
        var paymentMethodId = await CreatePaymentMethodAsync(secretKey, tokenId);
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.stripe.com/v1/payment_intents/{paymentIntentId}/confirm");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["payment_method"] = paymentMethodId });
        var response = await Http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json).RootElement;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Stripe confirm error: {doc.GetProperty("error").GetProperty("message").GetString()}");
        return paymentMethodId;
    }

    /// <summary>Creates a Customer to keep a card on for later off-session charges; returns its id.</summary>
    public static async Task<string> CreateCustomerAsync(string secretKey, string description)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.stripe.com/v1/customers");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["description"] = description });
        var response = await Http.SendAsync(request);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Stripe error: {doc.GetProperty("error").GetProperty("message").GetString()}");
        return doc.GetProperty("id").GetString()!;
    }

    /// <summary>
    /// Marks a not yet confirmed PaymentIntent to save its card on <paramref name="customerId"/> for
    /// off-session charges; the confirmation then attaches the PaymentMethod to the customer.
    /// </summary>
    public static async Task SaveCardForFutureUsageAsync(string secretKey, string paymentIntentId, string customerId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.stripe.com/v1/payment_intents/{paymentIntentId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["customer"] = customerId,
            ["setup_future_usage"] = "off_session",
        });
        var response = await Http.SendAsync(request);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Stripe error: {doc.GetProperty("error").GetProperty("message").GetString()}");
    }

    /// <summary>
    /// Charges a saved card without the customer present (a renewal). Returns the PaymentIntent id and
    /// status; a decline or a card that needs authentication throws.
    /// </summary>
    public static async Task<(string PaymentIntentId, string Status)> ChargeOffSessionAsync(string secretKey,
        string customerId, string paymentMethodId, long amountCents, string currency, string description, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.stripe.com/v1/payment_intents");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
        // One renewal of one term is charged once, however often the worker retries it.
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["amount"] = amountCents.ToString(),
            ["currency"] = currency.ToLower(),
            ["description"] = description,
            ["customer"] = customerId,
            ["payment_method"] = paymentMethodId,
            ["off_session"] = "true",
            ["confirm"] = "true",
        });
        var response = await Http.SendAsync(request);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Stripe renewal error: {doc.GetProperty("error").GetProperty("message").GetString()}");
        return (doc.GetProperty("id").GetString()!, doc.GetProperty("status").GetString()!);
    }

    /// <summary>Creates a PaymentMethod from a card token and returns its id.</summary>
    public static async Task<string> CreatePaymentMethodAsync(string secretKey, string tokenId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.stripe.com/v1/payment_methods");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["type"] = "card",
            ["card[token]"] = tokenId,
        });
        var response = await Http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json).RootElement;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Stripe error: {doc.GetProperty("error").GetProperty("message").GetString()}");
        return doc.GetProperty("id").GetString()!;
    }

    /// <summary>Retrieves a PaymentIntent and returns its status.</summary>
    public static async Task<(string Status, long Amount, string Currency)> GetPaymentIntentAsync(
        string secretKey, string paymentIntentId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.stripe.com/v1/payment_intents/{paymentIntentId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);

        var response = await Http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json).RootElement;

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Stripe error: {doc.GetProperty("error").GetProperty("message").GetString()}");

        return (
            doc.GetProperty("status").GetString()!,
            doc.GetProperty("amount").GetInt64(),
            doc.GetProperty("currency").GetString()!
        );
    }

    /// <summary>Gets card brand and last4 from a PaymentMethod, returns display title like "Visa •••• 4242".</summary>
    public static async Task<string?> GetPaymentMethodTitleAsync(string secretKey, string paymentMethodId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.stripe.com/v1/payment_methods/{paymentMethodId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
        var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        if (!doc.TryGetProperty("card", out var card)) return null;
        var brand = card.TryGetProperty("brand", out var b) ? b.GetString() ?? "Card" : "Card";
        var last4 = card.TryGetProperty("last4", out var l) ? l.GetString() ?? "????" : "????";
        return $"{char.ToUpper(brand[0])}{brand[1..]} •••• {last4}";
    }
}
