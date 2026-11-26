using Jellyfin.Plugin.LmSubs.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LmSubs;

/// <summary>Registers plugin services into Jellyfin's DI container.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient();
        serviceCollection.AddSingleton<OpenAiClient>();
        serviceCollection.AddSingleton<AudioExtractor>();
        serviceCollection.AddSingleton<SubtitleService>();
    }
}
