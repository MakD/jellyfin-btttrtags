using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.BtttrTags.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool EnableNewMovies { get; set; } = true;
    public int NewMovieDaysThreshold { get; set; } = 90;

    public bool EnableJustAdded { get; set; } = true;
    public int JustAddedDaysThreshold { get; set; } = 30;

    public bool EnableTrending { get; set; } = true;

    public bool EnableImdbTop250 { get; set; } = true;

    public bool EnableTvSeriesStatus { get; set; } = true;
}