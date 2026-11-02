using System;
using System.Collections.Generic;
using Jellyfin.Plugin.BtttrTags.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.BtttrTags;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public override string Name => "BtttrTags";
    public override Guid Id => Guid.Parse("3f82b7c1-d419-4a92-95e3-6a9b4e7a2b10");
    public override string Description => "Adds btttr.cc style hanging tags to original movie & series posters across all clients while keeping original artwork safe.";

    public static Plugin? Instance { get; private set; }

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = this.Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
            }
        };
    }
}