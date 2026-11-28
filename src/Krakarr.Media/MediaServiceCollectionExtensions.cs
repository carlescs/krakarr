using Krakarr.Media.CustomFormats;
using Krakarr.Media.Files;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Media;

public static class MediaServiceCollectionExtensions
{
    public static IServiceCollection AddKrakarrMedia(this IServiceCollection services)
    {
        services.AddSingleton<IFileTransfer, FileTransfer>();
        services.AddSingleton<CustomFormatCache>();
        return services;
    }
}
