using System.Text.Json;

namespace NekoSharp.Core.Models;

 
 
 
 
public sealed class CloudflareCredentials
{
    public string Domain { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public Dictionary<string, string> AllCookies { get; set; } = new();
    public DateTime ObtainedAtUtc { get; set; } = DateTime.UtcNow;

     
    public bool IsExpired => DateTime.UtcNow - ObtainedAtUtc > TimeSpan.FromMinutes(25);

     

     
    public string CookiesJson
    {
        get => JsonSerializer.Serialize(AllCookies);
        set => AllCookies = JsonSerializer.Deserialize<Dictionary<string, string>>(value) ?? new();
    }
}
