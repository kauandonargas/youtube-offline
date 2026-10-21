using LocalTube;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

foreach (bool audio in new[] { false, true })
{
    var profiles = DownloadRecovery.Profiles(audio);
    Check(profiles.Count >= 9, "É necessário ter várias estratégias reais.");
    Check(profiles[0].Key == "padrao", "A primeira tentativa precisa respeitar o formato original.");
    Check(profiles.Select(p => p.Key).Distinct().Count() == profiles.Count, "Chaves repetidas.");
    Check(profiles.Select(p => p.FormatKind + string.Join(",", p.ExtraArgs)).Distinct().Count() == profiles.Count, "Estratégias idênticas.");
    Check(profiles.All(p => !p.ExtraArgs.Any(x => x.Contains("cookies", StringComparison.OrdinalIgnoreCase))), "Importação não solicitada de cookies.");
    Check(profiles.All(p => !p.ExtraArgs.Any(x => x.Contains("proxy", StringComparison.OrdinalIgnoreCase))), "Proxy não solicitado.");

    foreach (var profile in profiles)
    {
        var selector = DownloadRecovery.FormatSelector(audio, "720", profile.FormatKind);
        Check(!string.IsNullOrWhiteSpace(selector), "Formato vazio: " + profile.Key);
        if (!audio)
            Check(selector.Contains("height<=720"), "Qualidade escolhida não foi respeitada em: " + profile.Key);
    }
}

Check(DownloadRecovery.IsRestricted("ERROR: This video is private"), "Vídeo privado precisa parar.");
Check(DownloadRecovery.IsRestricted("Sign in to confirm you're not a bot"), "Autenticação deve parar.");
Check(DownloadRecovery.IsRestricted("DRM protected video"), "DRM deve parar.");
Check(!DownloadRecovery.IsRestricted("HTTP Error 403: Forbidden"), "403 pode ser transiente.");
Check(DownloadRecovery.IsRateLimited("HTTP Error 429: Too Many Requests"), "429 deve parar.");
Check(!DownloadRecovery.IsRateLimited("HTTP Error 503"), "503 não é um bloqueio de taxa.");
Check(DownloadRecovery.CountErrors("WARNING: something\nERROR: first\nERROR: second\n") == 2, "Contagem de falhas de playlist.");
Check(DownloadRecovery.CountErrors("WARNING: something") == 0, "Avisos não podem virar falhas.");
Console.WriteLine("Download recovery: cenários determinísticos aprovados.");
