using System;
using System.IO;
using Debryfin.Configuration;
using MediaBrowser.Common; // Necesario para IApplicationHost
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Debryfin
{
    public class Plugin : BasePluginSimpleUI<DebryfinOptions>, IHasThumbImage
    {
        public static Plugin Instance { get; private set; }
        public static DebridService DebridServiceInstance { get; private set; }
        private readonly ILogger _logger;

        // --- CORRECCIÓN AQUÍ: Usamos IApplicationHost ---
        public Plugin(IApplicationHost applicationHost, ILogManager logManager) : base(applicationHost)
        {
            Instance = this;
            _logger = logManager.GetLogger(GetType().Name);
            DebridServiceInstance = new DebridService(logManager.GetLogger("DebridService"));
            _logger.Info("Debryfin ha sido cargado correctamente.");
        }

        public override Guid Id => Guid.Parse("3448308b-4818-471a-b3d2-a40107e8583a");
        public override string Name => "Debryfin";
        public override string Description => "Desbloqueador universal de enlaces para Emby (Real-Debrid, TorBox, etc).";

        private DebryfinOptions _cachedOptions;
        internal DebryfinOptions SafeGetOptions()
        {
            try { return GetOptions() ?? (_cachedOptions ??= new DebryfinOptions()); }
            catch (Exception ex)
            {
                _logger?.ErrorException("Debryfin: error reading configuration, using defaults", ex);
                return _cachedOptions ??= new DebryfinOptions();
            }
        }

        public DebryfinOptions Configuration => SafeGetOptions();

        public ImageFormat ThumbImageFormat => ImageFormat.Jpg;

        public Stream GetThumbImage()
        {
            var type = GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".thumb.jpg");
        }
    }
}