using Jellyfin.Plugin.LmSubs.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Subtitles;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LmSubs;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient();
        serviceCollection.AddSingleton<OpenAiClient>();
        serviceCollection.AddSingleton<AudioExtractor>();
        serviceCollection.AddSingleton<SubtitleStore>();
        serviceCollection.AddSingleton<SubtitleService>();
        // Must register as ISubtitleProvider: SubtitleManager resolves IEnumerable<ISubtitleProvider>.
        serviceCollection.AddSingleton<ISubtitleProvider, Providers.LmSubtitleProvider>();
    }
}
