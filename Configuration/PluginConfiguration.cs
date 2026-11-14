using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.BtttrTags.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool EnableTrendingRank { get; set; } = true;

    public bool EnableTrending { get; set; } = true;

    public bool EnableNewMovie { get; set; } = true;
    public bool EnableNewSeries { get; set; } = true;
    public int NewMovieDaysThreshold { get; set; } = 90;

    public bool EnableJustAdded { get; set; } = true;
    public int JustAddedDaysThreshold { get; set; } = 30;

    public bool EnableImdbTop250 { get; set; } = true;

    public bool EnableOscarWinner { get; set; } = true;
    public bool EnableOscarNominee { get; set; } = true;

    public bool EnableEmmyWinner { get; set; } = true;
    public bool EnableEmmyNominee { get; set; } = true;

    public bool EnablePalmeDor { get; set; } = true;
    public bool EnableCannesNominee { get; set; } = true;

    public bool EnableGoldenLion { get; set; } = true;
    public bool EnableVeniceNominee { get; set; } = true;

    public bool EnableGoldenBear { get; set; } = true;
    public bool EnableBerlinNominee { get; set; } = true;

    public bool EnableBaftaWinner { get; set; } = true;
    public bool EnableBaftaNominee { get; set; } = true;

    public bool EnableGlobeWinner { get; set; } = true;
    public bool EnableGlobeNominee { get; set; } = true;

    public bool EnableNationalAward { get; set; } = true;

    public bool EnableFilmfareWinner { get; set; } = true;
    public bool EnableFilmfareNominee { get; set; } = true;

    public bool EnableReturningSeries { get; set; } = true;

    public bool EnableBingeReady { get; set; } = true;
}
