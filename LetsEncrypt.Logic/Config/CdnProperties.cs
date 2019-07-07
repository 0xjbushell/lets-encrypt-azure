﻿namespace LetsEncrypt.Logic.Config
{
    public class CdnProperties
    {
        public string Name { get; set; }

        public string ResourceGroupName { get; set; }

        public string[] Endpoints { get; set; } = new string[0];
    }
}
