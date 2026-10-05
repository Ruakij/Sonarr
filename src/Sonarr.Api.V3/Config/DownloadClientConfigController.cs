using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Configuration;
using Sonarr.Http;
using Sonarr.Http.REST.Attributes;

namespace Sonarr.Api.V3.Config
{
    [V3ApiController("config/downloadclient")]
    public class DownloadClientConfigController : ConfigController<DownloadClientConfigResource>
    {
        public DownloadClientConfigController(IConfigService configService)
            : base(configService)
        {
            SharedValidator.RuleFor(c => c.MinimumTitleSimilarity)
                           .InclusiveBetween(0, 100);
        }

        [RestPutById]
        [Consumes("application/json")]
        public override ActionResult<DownloadClientConfigResource> SaveConfig([FromBody] DownloadClientConfigResource resource)
        {
            // Null values are skipped when saving, an empty timeout has to be stored explicitly to disable it again
            resource.ManualImportTimeout ??= -1;

            return base.SaveConfig(resource);
        }

        protected override DownloadClientConfigResource ToResource(IConfigService model)
        {
            return DownloadClientConfigResourceMapper.ToResource(model);
        }
    }
}
