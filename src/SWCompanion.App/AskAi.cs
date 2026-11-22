using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using SWCompanion.Core;
using SWCompanion.Core.Ask;

namespace SWCompanion.App;

/// <summary>Which AI service answers questions. "Off" until you set one up in Settings.</summary>
internal enum AiProvider { Off, Claude, OpenAI, Local }

/// <summary>An answer, or why there isn't one, in plain words.</summary>
internal sealed record AiAnswer(string? Text, string? Problem, double Seconds);

/// <summary>
/// Sends one question (already with private names hidden) to the AI service you chose, using your own key, and returns
/// the answer. No tools are given to the AI: it can only answer. Your key is never logged or shown again once saved.
/// </summary>
internal static class AskAi
{
    /// <summary>The default model for each service (you can pick another in Settings after "check connection").</summary>
    public static string DefaultModel(AiProvider p) => p switch
    {
        AiProvider.Claude => "claude-opus-5-5",
        AiProvider.OpenAI => "",   // OpenAI's model names change often: pick one from the list "check connection" shows
        AiProvider.Local => "",
        _ => "",
    };

    public static string Name(AiProvider p) => p switch
    {
        AiProvider.Claude => "Claude (Anthropic)",
        AiProvider.OpenAI => "OpenAI (ChatGPT)",
        AiProvider.Local => "a model on this PC",
        _ => "no AI",
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    public static async Task<AiAnswer> Ask(AiProvider p, string model, string? key, string localUrl, AskRequest r, CancellationToken ct)
    {
        var started = DateTime.Now;
        try
        {
            string text = p switch
            {
                AiProvider.Claude => await AskClaude(model, key!, r, ct),
                AiProvider.OpenAI => await AskChat("https://api.openai.com", model, key, r, ct),
                AiProvider.Local => await AskChat(localUrl, model, null, r, ct),
                _ => throw new InvalidOperationException("No AI is set up."),
            };
            return new AiAnswer(text, null, (DateTime.Now - started).TotalSeconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new AiAnswer(null, "Stopped.", (DateTime.Now - started).TotalSeconds);
        }
        catch (Exception ex)
        {
            return new AiAnswer(null, Plain(ex), (DateTime.Now - started).TotalSeconds);
        }
    }

    /// <summary>The models your key can use, to pick from in Settings. Also proves the key and address work.</summary>
    public static async Task<(List<string> Models, string? Problem)> ListModels(AiProvider p, string? key, string localUrl, CancellationToken ct)
    {
        try
        {
            if (p == AiProvider.Claude)
            {
                var client = new AnthropicClient { ApiKey = key };
                var page = await client.Models.List(cancellationToken: ct);
                return (page.Items.Select(m => m.ID).ToList(), null);
            }
            string baseUrl = p == AiProvider.OpenAI ? "https://api.openai.com" : localUrl;
            using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/v1/models");
            if (p == AiProvider.OpenAI) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var res = await Http.SendAsync(req, ct);
            string body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return ([], Status((int)res.StatusCode, body));
            using var doc = JsonDocument.Parse(body);
            var ids = doc.RootElement.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString() ?? "")
                         .Where(id => id.Length > 0).OrderBy(id => id).ToList();
            return (ids, null);
        }
        catch (Exception ex) { return ([], Plain(ex)); }
    }

    // ---- Claude, through Anthropic's official library ----

    private static async Task<string> AskClaude(string model, string key, AskRequest r, CancellationToken ct)
    {
        var client = new AnthropicClient { ApiKey = key };
        var messages = new List<BetaMessageParam>();
        foreach (var t in r.Earlier)
        {
            messages.Add(new BetaUserMessageParam { Content = t.Question });
            messages.Add(new BetaAssistantMessageParam { Content = t.Answer });
        }
        messages.Add(new BetaUserMessageParam { Content = r.Message });

        // Current Claude models may decline a request their safety checks flag; "default" fallbacks let the service
        // answer with a suitable other model instead of returning nothing. Only offered on the models that support it.
        bool fallbacks = model is "claude-opus-5-5" or "claude-sonnet-5-5" or "claude-fable-5-2" or "claude-fable-5-1" or "claude-opus-5";
        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = 16000,
            System = r.System,
            Messages = messages,
            OutputConfig = new BetaOutputConfig { Effort = Effort.Medium },
        };
        if (fallbacks)
            parameters = parameters with { Betas = ["server-side-fallback-2026-07-01"], Fallbacks = new Default() };

        var response = await client.Beta.Messages.Create(parameters, cancellationToken: ct);
        if (response.StopReason == "refusal")
            return "Claude declined to answer this one. Try asking it a different way.";
        var text = string.Join("\n", response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text)).Trim();
        if (response.StopReason == "max_tokens") text += "\n\n(The answer was cut short.)";
        return text.Length == 0 ? "No answer came back." : text;
    }

    // ---- OpenAI, and models running on this PC (Ollama, LM Studio...), through the common chat interface ----

    private static async Task<string> AskChat(string baseUrl, string model, string? key, AskRequest r, CancellationToken ct)
    {
        var messages = new List<object> { new { role = "system", content = r.System } };
        foreach (var t in r.Earlier)
        {
            messages.Add(new { role = "user", content = t.Question });
            messages.Add(new { role = "assistant", content = t.Answer });
        }
        messages.Add(new { role = "user", content = r.Message });
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { model, messages }), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(key)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var res = await Http.SendAsync(req, ct);
        string body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException(Status((int)res.StatusCode, body));
        using var doc = JsonDocument.Parse(body);
        var choice = doc.RootElement.GetProperty("choices")[0];
        string text = choice.GetProperty("message").GetProperty("content").GetString()?.Trim() ?? "";
        if (choice.TryGetProperty("finish_reason", out var fr) && fr.GetString() == "length") text += "\n\n(The answer was cut short.)";
        return text.Length == 0 ? "No answer came back." : text;
    }

    // ---- plain-English problems ----

    private static string Status(int code, string body) => code switch
    {
        401 or 403 => "The key wasn't accepted. Check it was copied in full, and that it's an API key (from the provider's developer site), not a chat subscription.",
        404 => "That model or address wasn't found. Press \"check connection\" and pick a model from the list.",
        429 => "The service says too many requests, or the account has run out of credit. Wait a minute, or check the account's billing.",
        >= 500 => "The AI service had a problem on its side. Try again in a minute.",
        _ => $"The service answered with an error ({code}). " + Short(body),
    };

    private static string Plain(Exception ex) => ex switch
    {
        HttpRequestException h when h.Message.Length > 0 && !h.Message.StartsWith("No connection", StringComparison.Ordinal) && h.InnerException is null => h.Message,
        HttpRequestException => "Couldn't reach the AI service. Check the internet connection (or, for a model on this PC, that it's running at the address in Settings).",
        TaskCanceledException => "The AI service took too long to answer.",
        _ when ex.GetType().Name.Contains("Unauthorized") || ex.GetType().Name.Contains("Authentication") => Status(401, ""),
        _ when ex.GetType().Name.Contains("RateLimit") => Status(429, ""),
        _ when ex.GetType().Name.Contains("NotFound") => Status(404, ""),
        _ when ex.GetType().Name.Contains("5xx") || ex.GetType().Name.Contains("InternalServer") => Status(500, ""),
        _ => "Something went wrong asking the AI: " + Short(ex.Message),
    };

    private static string Short(string s) => s.Length > 200 ? s[..200] + "..." : s;
}

/// <summary>
/// Your AI key, kept encrypted with Windows' own data protection for your Windows account on this PC only
/// (%LOCALAPPDATA%\SWCompanion\ai-key-&lt;service&gt;.dat). Never in settings.json, the logs, the project or anything published.
/// </summary>
internal static class AiKeyStore
{
    private static string PathFor(AiProvider p) => System.IO.Path.Combine(AppPaths.LocalAppDataFolder, $"ai-key-{p.ToString().ToLowerInvariant()}.dat");

    public static bool Has(AiProvider p) => File.Exists(PathFor(p));

    public static void Save(AiProvider p, string key)
    {
        Directory.CreateDirectory(AppPaths.LocalAppDataFolder);
        var sealedKey = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor(p), sealedKey);
    }

    public static string? Load(AiProvider p)
    {
        try
        {
            return File.Exists(PathFor(p))
                ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(PathFor(p)), null, DataProtectionScope.CurrentUser))
                : null;
        }
        catch { return null; } // made by another Windows account, or damaged: treat as no key
    }

    /// <summary>Forgets the key: the file is overwritten and then removed (on your request, from Settings).</summary>
    public static void Remove(AiProvider p)
    {
        try
        {
            var path = PathFor(p);
            if (!File.Exists(path)) return;
            File.WriteAllBytes(path, new byte[new FileInfo(path).Length]);
            File.Delete(path);
        }
        catch { }
    }
}
