﻿using LetsEncrypt.Logic.Config.Properties;
using LetsEncrypt.Logic.Extensions;
using LetsEncrypt.Logic.Providers.CertificateStores;
using LetsEncrypt.Logic.Providers.ChallengeResponders;
using LetsEncrypt.Logic.Providers.TargetResources;
using LetsEncrypt.Logic.Storage;
using Microsoft.Azure.KeyVault;
using Microsoft.Azure.KeyVault.Models;
using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LetsEncrypt.Logic.Config
{
    public class RenewalOptionParser : IRenewalOptionParser
    {
        private readonly IAzureHelper _azureHelper;
        private readonly ILogger _log;

        public RenewalOptionParser(
            IAzureHelper azureHelper,
            ILogger log)
        {
            _azureHelper = azureHelper ?? throw new ArgumentNullException(nameof(azureHelper));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public async Task<IChallengeResponder> ParseChallengeResponderAsync(CertificateRenewalOptions cfg, CancellationToken cancellationToken)
        {
            var certStore = ParseCertificateStore(cfg);
            var target = ParseTargetResource(cfg);
            var cr = cfg.ChallengeResponder ?? new GenericEntry
            {
                Type = "storageAccount",
                Properties = JObject.FromObject(new StorageProperties
                {
                    AccountName = ConvertToValidStorageAccountName(target.Name),
                    KeyVaultName = certStore.Name
                })
            };
            switch (cr.Type.ToLowerInvariant())
            {
                case "storageaccount":
                    var props = cr.Properties?.ToObject<StorageProperties>() ?? new StorageProperties
                    {
                        KeyVaultName = cr.Name,
                        AccountName = ConvertToValidStorageAccountName(cr.Name)
                    };

                    // try MSI first, must do check if we can read to know if we have access
                    var accountName = props.AccountName;
                    if (string.IsNullOrEmpty(accountName))
                        accountName = ConvertToValidStorageAccountName(target.Name);

                    var provider = new AzureServiceTokenProvider();
                    var state = new TokenRenewerState
                    {
                        AzureHelper = _azureHelper,
                        TokenProvider = provider
                    };
                    var tokenAndFrequency = await StorageMsiTokenRenewerAsync(state, cancellationToken);
                    var token = new TokenCredential(tokenAndFrequency.Token, StorageMsiTokenRenewerAsync, state, tokenAndFrequency.Frequency.Value);
                    var storage = new AzureBlobStorageProvider(token, accountName, props.ContainerName);
                    // verify that MSI access works, fallback otherwise
                    // not ideal since it's a readonly check -> we need Blob Contributor but user could set Blob Reader and this check would pass
                    try
                    {
                        await storage.ExistsAsync("1.txt", cancellationToken);
                    }
                    catch (StorageException e) when (e.RequestInformation.HttpStatusCode == (int)HttpStatusCode.Forbidden)
                    {
                        _log.LogWarning($"MSI access to storage {accountName} failed. Attempting fallbacks via connection string. (You can ignore this warning if you don't use MSI authentication).");
                        var connectionString = props.ConnectionString;
                        if (string.IsNullOrEmpty(connectionString))
                        {
                            // falback to secret in keyvault
                            var keyVaultName = props.KeyVaultName;
                            if (string.IsNullOrEmpty(keyVaultName))
                                keyVaultName = certStore.Name;

                            _log.LogInformation($"No connection string in config, checking keyvault {keyVaultName} secret {props.SecretName}");
                            connectionString = await GetSecretAsync(keyVaultName, props.SecretName, cancellationToken);
                        }
                        if (string.IsNullOrEmpty(connectionString))
                            throw new InvalidOperationException($"MSI access failed for {accountName} and could not find fallback connection string for storage access. Unable to proceed with Let's encrypt challenge");

                        storage = new AzureBlobStorageProvider(connectionString, props.ContainerName);
                    }
                    return new AzureStorageHttpChallengeResponder(storage);
                default:
                    throw new NotImplementedException(cr.Type);
            }
        }

        public ICertificateStore ParseCertificateStore(CertificateRenewalOptions cfg)
        {
            var target = ParseTargetResource(cfg);
            var store = cfg.CertificateStore ?? new GenericEntry
            {
                Type = "keyVault",
                Name = target.Name
            };

            switch (store.Type.ToLowerInvariant())
            {
                case "keyvault":
                    // all optional
                    var props = store.Properties?.ToObject<KeyVaultProperties>() ?? new KeyVaultProperties
                    {
                        Name = store.Name
                    };
                    var certificateName = props.CertificateName;
                    if (string.IsNullOrEmpty(certificateName))
                        certificateName = cfg.HostNames.First().Replace(".", "-");
                    var keyVaultName = props.Name;
                    if (string.IsNullOrEmpty(keyVaultName))
                    {
                        keyVaultName = target.Name;
                    }
                    return new KeyVaultCertificateStore(GetKeyVaultClient(), keyVaultName, certificateName);
                default:
                    throw new NotImplementedException(store.Type);
            }
        }

        public ITargetResource ParseTargetResource(CertificateRenewalOptions cfg)
        {
            switch (cfg.TargetResource.Type.ToLowerInvariant())
            {
                case "cdn":
                    var props = cfg.TargetResource.Properties == null
                        ? new CdnProperties
                        {
                            Endpoints = new[] { cfg.TargetResource.Name },
                            Name = cfg.TargetResource.Name,
                            ResourceGroupName = cfg.TargetResource.Name
                        }
                        : cfg.TargetResource.Properties.ToObject<CdnProperties>();

                    if (string.IsNullOrEmpty(props.Name))
                        throw new ArgumentException($"CDN section is missing required property {nameof(props.Name)}");

                    var rg = props.ResourceGroupName;
                    if (string.IsNullOrEmpty(rg))
                        rg = props.Name;
                    var endpoints = props.Endpoints;
                    if (endpoints.IsNullOrEmpty())
                        endpoints = new[] { props.Name };

                    return new CdnTargetResoure(_azureHelper, rg, props.Name, endpoints);
                default:
                    throw new NotImplementedException(cfg.TargetResource.Type);
            }
        }

        /// <summary>
        /// Given a valid azure resource name converts it to the equivalent storage name by removing all dashes
        /// as per the usual convention used everywhere.
        /// </summary>
        /// <param name="resourceName"></param>
        /// <returns></returns>
        private string ConvertToValidStorageAccountName(string resourceName)
            => resourceName?.Replace("-", "");

        private IKeyVaultClient GetKeyVaultClient()
        {
            var tokenProvider = new AzureServiceTokenProvider();
            return new KeyVaultClient(new KeyVaultClient.AuthenticationCallback(tokenProvider.KeyVaultTokenCallback));
        }

        private async Task<string> GetSecretAsync(string keyVaultName, string secretName, CancellationToken cancellationToken)
        {
            try
            {
                var secret = await GetKeyVaultClient().GetSecretAsync($"https://{keyVaultName}.vault.azure.net", secretName, cancellationToken);
                return secret.Value;
            }
            catch (KeyVaultErrorException ex)
            {
                if (ex.Response.StatusCode == HttpStatusCode.NotFound ||
                    ex.Body.Error.Code == "SecretNotFound")
                    return null;

                throw;
            }
            catch (HttpRequestException e)
            {
                _log.LogError(e, $"Unable to get secret from keyvault {keyVaultName}");
                throw;
            }
        }

        private static async Task<NewTokenAndFrequency> StorageMsiTokenRenewerAsync(object state, CancellationToken cancellationToken)
        {
            var s = (TokenRenewerState)state;
            const string StorageResource = "https://storage.azure.com/";
            var authResult = await s.TokenProvider.GetAuthenticationResultAsync(StorageResource, await s.AzureHelper.GetTenantIdAsync(cancellationToken), cancellationToken);
            var next = authResult.ExpiresOn - DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5);
            if (next.Ticks < 0)
                next = default;

            return new NewTokenAndFrequency(authResult.AccessToken, next);
        }

        private class TokenRenewerState
        {
            public AzureServiceTokenProvider TokenProvider { get; set; }

            public IAzureHelper AzureHelper { get; set; }
        }
    }
}
