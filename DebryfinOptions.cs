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
        public override string EditorTitle => "Configuración de Debryfin";

        [JsonIgnore]
        public override string EditorDescription => "Configura tu proveedor de servicios Debrid para desbloquear enlaces 1fichier en archivos .strm.";

        // LISTA DESPLEGABLE DE PROVEEDORES
        [DisplayName("Proveedor Debrid")]
        [Description("Selecciona el servicio que tienes contratado.")]
        public DebridProvider Provider { get; set; }

        // CAMPO API KEY
        [DisplayName("API Key / Token")]
        [Description("Copia aquí tu API Key privada del servicio seleccionado.")]
        [IsPassword] // Esto hace que salgan asteriscos *****
        public string ApiKey { get; set; }

        // CHECKBOX PROXY
        [DisplayName("Activar Modo Proxy (Anti-Baneo)")]
        [Description("ACTIVADO: El servidor descarga el video (Seguro para compartir, consume CPU/Red). DESACTIVADO: Direct Play (Rápido, riesgo de baneo si hay múltiples IPs).")]
        public bool UseProxyMode { get; set; }
    }
}