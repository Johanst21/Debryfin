using System.ComponentModel;
using System.Text.Json.Serialization;
using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;

namespace Debryfin.Configuration
{
    public enum DebridProvider
    {
        RealDebrid,
        AllDebrid,
        TorBox,
        Premiumize
    }

    public class DebryfinOptions : EditableOptionsBase
    {
        public DebryfinOptions()
        {
            Provider = DebridProvider.RealDebrid;
            UseProxyMode = false;
        }

        [JsonIgnore]
        public override string EditorTitle => "Debryfin Settings";

        [JsonIgnore]
        public override string EditorDescription => "Configure your Debrid service provider to unlock 1fichier links in .strm files.";

        // PROVIDER DROPDOWN LIST
        [DisplayName("Debrid Provider")]
        [Description("Select the service you have subscribed to.")]
        public DebridProvider Provider { get; set; }

        // API KEY FIELD
        [DisplayName("API Key / Token")]
        [Description("Paste your private API Key for the selected service here.")]
        [IsPassword] // Masks the value with asterisks *****
        public string ApiKey { get; set; }

        // PROXY CHECKBOX
        [DisplayName("Enable Proxy Mode (Anti-Ban)")]
        [Description("ENABLED: The server downloads the video (Safe to share, uses CPU/Network). DISABLED: Direct Play (Fast, risk of ban if there are multiple IPs).")]
        public bool UseProxyMode { get; set; }
    }
}