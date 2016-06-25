﻿using log4net;
using Sif.Framework.Model;
using Sif.Framework.Model.DataModels;
using Sif.Framework.Model.Settings;
using Sif.Framework.Providers;
using Sif.Framework.Service;
using Sif.Framework.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Sif.Framework.Providers
{
    public class ProviderFactory
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly Object locked = new Object();

        private static ProviderFactory factory = null;

        // Active Providers for event publishing. These providers run in the background as an independent thread.
        private Dictionary<string, IService> providers = new Dictionary<string, IService>();

        private Dictionary<string, Thread> providerThreads = new Dictionary<string, Thread>();

        // Known providers that can be instantiated for standard request/response
        private Dictionary<string, ProviderClassInfo> providerClasses = new Dictionary<string, ProviderClassInfo>();

        public static ProviderFactory createFactory()
        {
            lock (locked)
            {
                log.Debug("Total Threads running before initialising provider Factory: " + Process.GetCurrentProcess().Threads.Count + " threads.");
                if (factory == null)
                {
                    try
                    {
                        factory = new ProviderFactory();
                    }
                    catch (Exception ex)
                    {
                        log.Error("Failed to initialise provider factory. Provider won't run.", ex);
                        factory = null;
                    }
                }
                log.Debug("Total Threads running after initialising Provider Factory: " + Process.GetCurrentProcess().Threads.Count + " threads.");
                return factory;
            }
        }

        /**
         * This will shut down each provider class that make up this provider
         */
        public static void shutdown()
        {
            lock (locked)
            {
                if (factory == null)
                {
                    return;
                }

                log.Debug("Finalising providers:");
                foreach (string name in factory.providers.Keys)
                {
                    try
                    {
                        log.Debug("--- " + name);
                        factory.providers[name].Finalise();
                    }
                    catch (Exception ex)
                    {
                        log.Warn(ex.Message, ex);
                    }
                }

                log.Debug("Stopping provider threads:");
                foreach (string name in factory.providerThreads.Keys)
                {
                    try
                    {
                        log.Debug("--- " + name);
                        factory.providerThreads[name].Abort();
                    }
                    catch (Exception ex)
                    {
                        log.Warn(ex.Message, ex);
                    }
                }
            }
            log.Info("All providers are shut down.");
        }

        /**
         * Returns an lazy loaded instance of this provider factory.
         * 
         * @return See Desc.
         */
        public static ProviderFactory getInstance()
        {
            if (factory == null)
            {
                return createFactory();
            }
            return factory;
        }

        public dynamic GetProvider(string name)
        {
            if (StringUtils.IsEmpty(name))
            {
                log.Error("The provider name is either null or empty. This is required! No Provider returned.");
                return null;
            }
            
            if (!providerClasses.Keys.Contains(name))
            {
                log.Error("No known provider for " + name);
                return null;
            }
            
            try
            {
                return providerClasses[name].GetClassInstance(null);
            }
            catch (Exception ex)
            {
                log.Error("Failed to instantiate a provider for " + name + ": " + ex.Message, ex);
                return null;
            }
        }

        /*---------------------*/
        /*-- Private Methods --*/
        /*---------------------*/
        private ProviderFactory()
        {
            ProviderSettings settings = SettingsManager.ProviderSettings as ProviderSettings;
            InitialiseProviders(settings);
            StartProviders(settings);
        }

        private void InitialiseProviders(ProviderSettings settings)
        {
            log.Debug("Initialising ProviderFactory (currently only supports Functional Services)");
            // settings.Classes only returns functional services at the moment, but can easily be extended to other types of services.
            foreach (Type type in settings.Classes)
            {
                log.Debug("Provider class to initialse: " + type.FullName);
                try
                {
                    ProviderClassInfo providerClassInfo = new ProviderClassInfo(type, new Type[] { });

                    IService provider = providerClassInfo.GetClassInstance();

                    if (StringUtils.IsEmpty(provider.getServiceName()))
                    {
                        log.Error("The provider is returning null or empty string from getServiceName(). Provider '" + provider.GetType().FullName + " not added to provider factory.");
                        continue;
                    }

                    log.Info("Adding provider for '" + provider.getServiceName() + "', using provider class '" + provider.GetType().FullName + "'.");

                    // First add it to the standard request/response dictionary
                    providerClasses[provider.getServiceName()] = providerClassInfo;

                    // Add it to dictionary of background threads
                    providers[provider.getServiceName()] = provider;

                    // Add it to dictionary of background threads
                    providerThreads[provider.getServiceName()] = new Thread(new ThreadStart(provider.Run));
                    // Each thread is essentially a global instance of a service whose responsibility is to maintain the services using timed tasks etc. - it never recieves any REST calls.
                }
                catch (Exception ex)
                {

                    log.Error("Cannot create Provider Class " + type.FullName + ": " + ex.Message, ex);
                }
            }
        }

        private void StartProviders(ProviderSettings settings)
        {
            int delay = settings.StartupDelay;  //delay between threads in seconds
            log.Debug("Start up delay between providers is: " + delay + " seconds");

            int i = 0;
            foreach (string serviceName in providerThreads.Keys)
            {
                log.Debug("Starting thread for " + serviceName);
                Timer timer = new Timer((o) => {
                    providerThreads[serviceName].Start();
                }, null, (i * delay), Timeout.Infinite);
                i += 1000;
            }
        }
    }
}
