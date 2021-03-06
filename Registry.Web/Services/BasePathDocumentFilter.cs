﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Registry.Web.Models.Configuration;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Registry.Web.Services
{
    public class BasePathDocumentFilter : IDocumentFilter
    {
        private readonly AppSettings _settings;

        public BasePathDocumentFilter(IOptions<AppSettings> settings)
        {
            _settings = settings.Value;
        }

        public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
        {
            if (!string.IsNullOrWhiteSpace(_settings.ExternalUrlOverride))
                swaggerDoc.Servers = new List<OpenApiServer>
                {
                    new OpenApiServer
                    {
                        Url = _settings.ExternalUrlOverride
                    }
                };
        }
    }
}
