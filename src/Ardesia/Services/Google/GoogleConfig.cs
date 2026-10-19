namespace Ardesia.Services.Google;

/// <summary>
/// Client OAuth «App desktop» di Ardesia nel progetto Google Cloud della scuola (ardesia-511918, app interna di
/// liceofoscarini.it). Per le app desktop Google non considera segreto il secret (sta comunque nell'eseguibile);
/// il repository è privato. Un file google-client.json accanto all'exe o in %AppData%\Ardesia ha la precedenza.
/// </summary>
static class GoogleConfig
{
    public const string ClientId = "774277790696-1g2mib0ps95832gutce7c931t5alb5fr.apps.googleusercontent.com";
    public const string ProjectId = "ardesia-511918";

    /// <summary>Dal file client_secret_….json scaricato dalla console («client_secret»).</summary>
    public const string ClientSecret = "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";
}
