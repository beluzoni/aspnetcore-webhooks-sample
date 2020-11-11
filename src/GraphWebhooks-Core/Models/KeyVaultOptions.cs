﻿// <copyright file="KeyVaultOptions.cs" company="Microsoft">
// Copyright (c) Microsoft. All rights reserved.
// </copyright>

namespace GraphWebhooks_Core.Models
{
    public class KeyVaultOptions
    {
        public string ClientId { get; set; }
        public string ClientSecret { get; set; }
        public string CertificateName { get; set; }
        public string TenantId { get; set; }
        public string KeyVaultUrl { get; set; }
    }
}
