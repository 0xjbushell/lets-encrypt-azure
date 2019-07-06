﻿using Microsoft.Azure.Services.AppAuthentication;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LetsEncrypt.Logic
{
    public class MsiTokenProvider : DelegatingHandler
    {
        private readonly AzureServiceTokenProvider _tokenProvider;
        private readonly Func<HttpRequestMessage, string> _resourceProvider;
        private readonly string _tenant;

        public MsiTokenProvider(AzureServiceTokenProvider tokenProvider, string tenant, Func<HttpRequestMessage, string> resourceProvider)
            : base(new HttpClientHandler())
        {
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
            _resourceProvider = resourceProvider ?? throw new ArgumentNullException(nameof(resourceProvider));
            _tenant = tenant;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var auth = await _tokenProvider.GetAuthenticationResultAsync(_resourceProvider(request), _tenant);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.AccessToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
