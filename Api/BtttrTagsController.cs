using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.BtttrTags.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BtttrTags.Api;

[ApiController]
[Route("BtttrTags")]
public class BtttrTagsController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IApplicationPaths _applicationPaths;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BtttrTagsRefreshTask> _taskLogger;

    public BtttrTagsController(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IApplicationPaths applicationPaths,
        IHttpClientFactory httpClientFactory,
        ILogger<BtttrTagsRefreshTask> taskLogger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _applicationPaths = applicationPaths;
        _httpClientFactory = httpClientFactory;
        _taskLogger = taskLogger;
    }

    [HttpPost("RestoreAll")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> RestoreAll(CancellationToken cancellationToken)
    {
        var task = new BtttrTagsRefreshTask(
            _libraryManager,
            _providerManager,
            _applicationPaths,
            _httpClientFactory,
            _taskLogger);

        if (!await task.TryRestoreAllPostersAsync(cancellationToken).ConfigureAwait(false))
        {
            return Conflict(new { success = false, message = "A poster update is already running." });
        }

        return Ok(new { success = true, message = "All original clean posters restored." });
    }
}