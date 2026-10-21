namespace MediaCore;

// Diagnóstico compartilhado pelas duas interfaces. A decisão usa os erros
// observados, não tenta "contornar" bloqueios de autenticação/DRM/CAPTCHA.
public sealed record MediaFailure(string Code, string Message, bool Stop, bool BrowserSuggested);

public static class MediaDiagnostics
{
    public static string NormalizeBrowser(string? requested)
    {
        var value = requested?.Trim().ToLowerInvariant() ?? "none";
        return value is "edge" or "chrome" or "firefox" or "brave" ? value : "none";
    }

    public static MediaFailure Classify(string? details)
    {
        var text = details ?? "";
        bool Any(params string[] words) =>
            words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
        if (Any("drm protected", "protected by drm", "encrypted media", "widevine", "playready"))
            return new("drm", "O conteúdo usa proteção DRM. Não é possível extrair a mídia automaticamente.", true, false);
        if (Any("HTTP Error 429", "429: Too Many Requests", "too many requests"))
            return new("rate_limited", "O site limitou as solicitações. Aguarde antes de tentar novamente.", true, false);
        if (Any("cloudflare anti-bot", "cloudflare challenge", "generic:impersonate"))
            return new("cloudflare", "A página exige uma verificação Cloudflare. Tente a compatibilidade do extrator; se persistir, abra a página no navegador e conclua a verificação permitida.", false, true);
        if (Any("captcha", "verify you are human", "verify that you are human", "sign in to confirm you're not a bot"))
            return new("challenge", "O site exige verificação humana. Abra a página no navegador e conclua a verificação manualmente.", true, true);
        if (Any("this video is private", "private video", "members-only", "members only", "requires payment",
                "premium account required", "requires a subscription", "subscription required",
                "not available in your country", "not available in your region"))
            return new("access", "O conteúdo exige uma permissão que não foi confirmada. Use sua conta autorizada ou uma opção oferecida pelo site.", true, true);
        if (Any("login required", "sign in to verify", "login to watch", "you need to log in",
                "please log in", "sign in to view", "authentication required", "cookies are no longer valid"))
            return new("login", "O site exige uma sessão válida. Abra a página no navegador e, se possuir acesso, selecione explicitamente o navegador da sua sessão.", true, true);
        if (Any("video unavailable", "this video is unavailable", "404 not found", "HTTP Error 404",
                "removed by the uploader", "deleted video"))
            return new("unavailable", "Este vídeo não está disponível na origem.", true, false);
        if (Any("requested format is not available", "no video formats found", "format is not available"))
            return new("format", "O formato solicitado não está disponível; podemos tentar outro formato.", false, false);
        if (Any("HTTP Error 403", "403: Forbidden", "403 Forbidden"))
            return new("forbidden", "O servidor recusou o acesso à mídia (403). Uma nova extração ou sessão autorizada pode resolver, mas não há garantia.", false, true);
        if (Any("unsupported url", "no suitable extractor", "url is not supported"))
            return new("unsupported", "Nenhum extrator reconheceu esse site. Tente localizar fontes de mídia públicas declaradas na página.", false, false);
        if (Any("timed out", "connection reset", "connection aborted", "network unreachable",
                "temporarily unavailable", "HTTP Error 503", "HTTP Error 502"))
            return new("network", "Falha transitória de rede. Faz sentido tentar novamente com uma conexão conservadora.", false, false);
        return new("unknown", "Falha ainda não identificada. Consulte o erro registrado e teste uma abordagem diferente.", false, false);
    }
}
