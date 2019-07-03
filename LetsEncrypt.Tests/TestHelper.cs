﻿using LetsEncrypt.Logic.Config;

namespace LetsEncrypt.Tests
{
    public static class TestHelper
    {
        private const string TestUser = "User.McUserface@example.com";

        public const string DevelopmentStorageConnectionString = "UseDevelopmentStorage=true";

        public const string TestContainerName = "letsencrypt-tests";

        public static IAcmeOptions GetStagingOptions() => new AcmeOptions(true, TestUser);

        public static IAcmeOptions GetProductionOptions() => new AcmeOptions(false)
        {
            Email = TestUser
        };
    }
}
