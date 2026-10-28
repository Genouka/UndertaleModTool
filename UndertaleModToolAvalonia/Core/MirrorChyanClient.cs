using System;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UndertaleModTool.Localization;

namespace UndertaleModToolAvalonia;

/// <summary>The <c>data</c> object returned by the MirrorChyan latest-release endpoint.</summary>
public sealed record MirrorChyanRelease(
    string VersionName,
    int VersionNumber,
    string Channel,
    string Os,
    string Arch,
    string ReleaseNote,
    string? Url);

/// <summary>A parsed MirrorChyan API response.</summary>
public sealed record MirrorChyanResponse(int Code, string Msg, MirrorChyanRelease? Data)
{
    public bool IsSuccess => Code == 0;
}

/// <summary>
/// Minimal MirrorChyan API client. The CDK is only ever placed in the request URL; it is never
/// returned, logged or included in error messages.
/// </summary>
public static class MirrorChyanClient
{
    public const string DesktopResourceId = "UndertaleModToolAvalonia-Genouka";
    public const string AndroidResourceId = "UndertaleModToolAndroid-Genouka";

    /// <summary>Error code reported for transport/parse failures (no server response).</summary>
    public const int NetworkErrorCode = -1;

    public static string GetResourceId()
        => OperatingSystem.IsAndroid() ? AndroidResourceId : DesktopResourceId;

    public static string GetOs()
    {
        if (OperatingSystem.IsWindows())
            return "windows";
        if (OperatingSystem.IsLinux())
            return "linux";
        if (OperatingSystem.IsMacOS())
            return "macos";
        if (OperatingSystem.IsAndroid())
            return "android";
        return "windows";
    }

    public static string GetArch()
    {
        if (OperatingSystem.IsAndroid())
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
    }

    public static string GetUserAgent()
        => "UndertaleModToolAvalonia/" + App.VersionString;

    /// <summary>
    /// Queries the latest stable release for a resource. Throws on transport errors; a non-zero
    /// <see cref="MirrorChyanResponse.Code"/> is returned as-is.
    /// </summary>
    public static async Task<MirrorChyanResponse> QueryLatestAsync(
        HttpClient client,
        string resourceId,
        string os,
        string arch,
        string currentVersion,
        string? cdk,
        CancellationToken cancellationToken = default)
    {
        string url = $"https://mirrorchyan.com/api/resources/{Uri.EscapeDataString(resourceId)}/latest"
            + $"?os={Uri.EscapeDataString(os)}"
            + $"&arch={Uri.EscapeDataString(arch)}"
            + "&channel=stable"
            + $"&current_version={Uri.EscapeDataString(currentVersion)}"
            + $"&user_agent={Uri.EscapeDataString(GetUserAgent())}";

        if (!string.IsNullOrWhiteSpace(cdk))
            url += "&cdk=" + Uri.EscapeDataString(cdk.Trim());

        using HttpResponseMessage response = await client.GetAsync(url, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        MirrorChyanResponse parsed = ParseResponse(body);

        // Rejected requests (for example an invalid CDK) come back with an HTTP error status but
        // still carry the documented JSON error code, so prefer that code over a transport error.
        if (!response.IsSuccessStatusCode && parsed.Code == NetworkErrorCode)
        {
            throw new HttpRequestException(
                string.Format(LocalizationSource.GetString("Msg_HTTPError"), response.ReasonPhrase));
        }

        return parsed;
    }

    /// <summary>Parses a response body; invalid JSON is reported as a transport failure.</summary>
    static MirrorChyanResponse ParseResponse(string body)
    {
        JsonElement root;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new MirrorChyanResponse(NetworkErrorCode, "", null);
        }

        int code = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("code", out JsonElement codeElement)
            && codeElement.ValueKind == JsonValueKind.Number
            ? codeElement.GetInt32()
            : NetworkErrorCode;

        string msg = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("msg", out JsonElement msgElement)
            && msgElement.ValueKind == JsonValueKind.String
            ? msgElement.GetString() ?? ""
            : "";

        MirrorChyanRelease? data = null;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out JsonElement dataElement)
            && dataElement.ValueKind == JsonValueKind.Object)
        {
            data = new MirrorChyanRelease(
                VersionName: GetString(dataElement, "version_name"),
                VersionNumber: GetInt32(dataElement, "version_number"),
                Channel: GetString(dataElement, "channel"),
                Os: GetString(dataElement, "os"),
                Arch: GetString(dataElement, "arch"),
                ReleaseNote: GetString(dataElement, "release_note"),
                Url: GetNullableString(dataElement, "url"));
        }

        return new MirrorChyanResponse(code, msg, data);
    }

    /// <summary>Human-readable description for a documented MirrorChyan error code.</summary>
    public static string GetErrorDescription(int code) => code switch
    {
        1001 => "Invalid request parameters.",
        7001 => "The CDK has expired.",
        7002 => "The CDK is invalid.",
        7003 => "The CDK download quota has been used up.",
        7004 => "The CDK does not match this resource.",
        7005 => "The CDK has been blocked.",
        8001 => "The requested resource was not found.",
        _ => "",
    };

    /// <summary>
    /// Formats an API error for display. Uses the documented description for known codes and the
    /// server message otherwise. The CDK is never included.
    /// </summary>
    public static string DescribeError(int code, string message, string? cdk = null)
    {
        string description = GetErrorDescription(code);
        if (description.Length == 0)
        {
            description = string.IsNullOrWhiteSpace(message)
                ? LocalizationSource.GetString("Msg_CheckInternetConnection")
                : message;

            // Defensive: never echo the CDK even if the server reflected it back.
            string trimmedCdk = cdk?.Trim() ?? "";
            if (trimmedCdk.Length > 0)
                description = description.Replace(trimmedCdk, "[redacted]", StringComparison.Ordinal);
        }

        return string.Format(LocalizationSource.GetString("Msg_MirrorChyanError"), code, description);
    }

    /// <summary>Formats a transport failure. Deliberately discards the exception text (it may contain the request URL).</summary>
    public static string DescribeNetworkError()
        => string.Format(LocalizationSource.GetString("Msg_MirrorChyanError"),
            NetworkErrorCode, LocalizationSource.GetString("Msg_CheckInternetConnection"));

    static string GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    static string? GetNullableString(JsonElement element, string property)
        => element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    static int GetInt32(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
            return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
            return number;
        return 0;
    }
}
