using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace StrmCompanion
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        public static Plugin Instance { get; private set; }

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public override string Name => "StrmCompanion";

        // Stable GUID – never change after first deployment
        public override Guid Id => new Guid("3c9a8f2e-7b41-4d5e-a1f0-82c6d9e34b17");

        public override string Description => "Audio fingerprint-based intro detection and media info scanning for .strm files in Emby";

        public static string GetFingerprintBasePath()
        {
            var configured = Instance?.Configuration?.FingerprintDataPath;
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;
            return System.IO.Path.Combine(Instance?.DataFolderPath ?? string.Empty, "fingerprints");
        }

public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public Stream GetThumbImage()
        {
            var assembly = GetType().Assembly;
            var path = assembly.GetManifestResourceNames()
                .FirstOrDefault(r => r.EndsWith("thumb.png"));
            return path != null ? assembly.GetManifestResourceStream(path) : Stream.Null;
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            var assembly = GetType().Assembly;
            var configHtml = assembly.GetManifestResourceNames().FirstOrDefault(r => r.EndsWith("configPage.html"));
            var configJs   = assembly.GetManifestResourceNames().FirstOrDefault(r => r.EndsWith("configPage.js"));

            if (configHtml == null || configJs == null)
                return new List<PluginPageInfo>();

            return new List<PluginPageInfo>
            {
                new PluginPageInfo
                {
                    Name = "StrmCompanionConfig",
                    EmbeddedResourcePath = configHtml,
                    EnableInMainMenu = true,
                    DisplayName = "StrmCompanion",
                    MenuIcon = "video_library"
                },
                new PluginPageInfo
                {
                    Name = "StrmCompanionConfigJS",
                    EmbeddedResourcePath = configJs
                }
            };
        }
    }
}
