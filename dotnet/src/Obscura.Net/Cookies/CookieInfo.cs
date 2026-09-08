using System.Text.Json.Serialization;

namespace Obscura.Net;

/// <summary>
/// A cookie as it crosses the CDP and persistence boundaries. The JSON property
/// names are the persisted cookie-file format and must stay byte-compatible with
/// the Rust <c>serde</c> derive.
/// </summary>
public sealed class CookieInfo
{
    /// <summary>Cookie name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Cookie value.</summary>
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    /// <summary>Scope domain. A leading dot is ignored (RFC 6265 4.1.2.3).</summary>
    [JsonPropertyName("domain")]
    public string Domain { get; set; } = string.Empty;

    /// <summary>Scope path.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>Only sent over https when true.</summary>
    [JsonPropertyName("secure")]
    public bool Secure { get; set; }

    /// <summary>Hidden from <c>document.cookie</c> when true.</summary>
    [JsonPropertyName("httpOnly")]
    public bool HttpOnly { get; set; }

    /// <summary>Strict / Lax / None; empty means "unset" on the CDP import path.</summary>
    [JsonPropertyName("sameSite")]
    public string SameSite { get; set; } = string.Empty;

    /// <summary>Unix expiry in seconds, or null for a session cookie.</summary>
    [JsonPropertyName("expires")]
    public long? Expires { get; set; }
}
