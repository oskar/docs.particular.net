﻿namespace Core4.Recoverability.Immediate.ConfigurationSource
{
    using NServiceBus;

    class DisableUsage
    {
        DisableUsage(Configure configure)
        {
            #region DisableImmediateRetriesConfigurationSourceUsage

            configure.CustomConfigurationSource(new DisableConfigurationSource());

            #endregion
        }
    }
}