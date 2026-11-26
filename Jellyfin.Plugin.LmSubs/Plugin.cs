using Jellyfin.Plugin.LmSubs.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.LmSubs;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "LMSubs";

    public override Guid Id => Guid.Parse("9b1f2a44-3c5d-4e6f-8a7b-9c0d1e2f3a4b");

    public override string Description
        => "Generate and translate subtitles with OpenAI-compatible ASR (speech-to-text) and LLM translation. Supports full-video generation and live on-demand transcription.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            },
            new PluginPageInfo
            {
                Name = "LmSubsConfigJs",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.js"
            }
        };
    }
}
