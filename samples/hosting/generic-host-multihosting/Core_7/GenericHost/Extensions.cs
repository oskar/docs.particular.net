using System.Collections.Concurrent;
using NServiceBus.Configuration.AdvancedExtensibility;

namespace NServiceBus
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using NServiceBus.ObjectBuilder;

    /// <summary>
    /// Extension methods to configure NServiceBus for the .NET Core generic host.
    /// </summary>
    public static class HostBuilderExtensions
    {
        /// <summary>
        /// Configures the host to start an NServiceBus endpoint.
        /// </summary>
        public static IHostBuilder UseMultipleNServiceBus(this IHostBuilder hostBuilder, Func<HostBuilderContext, EndpointConfiguration> endpointConfigurationBuilder)
        {
            hostBuilder.ConfigureServices((ctx, serviceCollection) =>
            {
                var endpointConfiguration = endpointConfigurationBuilder(ctx);
                var endpointName = endpointConfiguration.GetSettings().Get<string>("NServiceBus.Routing.EndpointName");
                var childCollection = new ServiceCollection();
                var startableEndpoint = EndpointWithExternallyManagedContainer.Create(endpointConfiguration, new ServiceCollectionAdapter(childCollection));

                sessionProvider = sessionProvider ?? new SessionProvider();

                serviceCollection.AddSingleton<ISessionProvider>(sessionProvider);
                serviceCollection.AddSingleton<IHostedService>(serviceProvider => new NServiceBusHostedService(startableEndpoint, serviceProvider, serviceCollection, childCollection, sessionProvider, endpointName));
            });

            return hostBuilder;
        }

        static SessionProvider sessionProvider;
    }

    public interface ISessionProvider
    {
        IMessageSession GetSession(string endpointName);
    }

    class SessionProvider : ISessionProvider
    {
        public IMessageSession GetSession(string endpointName)
        {
            if (sessions.TryGetValue(endpointName, out var session))
            {
                return session;
            }

            throw new InvalidOperationException($"No session found with name '{endpointName}'");
        }

        public IDisposable Manage(IMessageSession messageSession, string endpointName)
        {
            return new Scope(messageSession, endpointName, sessions);
        }

        sealed class Scope : IDisposable
        {
            private readonly string endpointName;
            private ConcurrentDictionary<string, IMessageSession> sessions;

            public Scope(IMessageSession session, string endpointName, ConcurrentDictionary<string, IMessageSession> sessions)
            {
                this.sessions = sessions;
                this.endpointName = endpointName;

                this.sessions.TryAdd(endpointName, session);
            }

            public void Dispose()
            {
                sessions.TryRemove(endpointName, out _);
            }
        }

        private ConcurrentDictionary<string, IMessageSession> sessions = new ConcurrentDictionary<string, IMessageSession>();
    }


    class NServiceBusHostedService : IHostedService
    {
        public NServiceBusHostedService(IStartableEndpointWithExternallyManagedContainer startableEndpoint,
            IServiceProvider serviceProvider, IServiceCollection parentServiceCollection, ServiceCollection childCollection,
            SessionProvider sessionProvider,
            string endpointName)
        {
            this.parentServiceCollection = parentServiceCollection;
            this.endpointName = endpointName;
            this.sessionProvider = sessionProvider;
            this.childCollection = childCollection;
            this.startableEndpoint = startableEndpoint;
            this.serviceProvider = serviceProvider;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            AddSourceServicesFrom(childCollection, parentServiceCollection, serviceProvider);

            childServiceProvider = childCollection.BuildServiceProvider();

            endpoint = await startableEndpoint.Start(new ServiceProviderAdapter(childServiceProvider))
                .ConfigureAwait(false);

            managementScope = sessionProvider.Manage(endpoint, endpointName);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await endpoint.Stop().ConfigureAwait(false);
            managementScope.Dispose();
            await childServiceProvider.DisposeAsync().ConfigureAwait(false);
        }

        private static void AddSourceServicesFrom(
            IServiceCollection targetCollection,
            IServiceCollection sourceCollection,
            IServiceProvider sourceProvider)
        {
            foreach (var desc in sourceCollection)
            {
                ServiceDescriptor newDesc;

                if (desc.Lifetime == ServiceLifetime.Singleton && desc.ImplementationInstance == null && !desc.ServiceType.IsGenericTypeDefinition && !desc.ImplementationType?.IsGenericTypeDefinition == true)
                {
                    //convert singletons whose instance isn't known to a redirect call to the source provider

                    newDesc = new ServiceDescriptor(
                        serviceType: desc.ServiceType,
                        factory: x => sourceProvider.GetService(desc.ServiceType),
                        lifetime: ServiceLifetime.Singleton);
                }
                else
                {
                    newDesc = desc;
                }

                targetCollection.Add(newDesc);
            }
        }

        IEndpointInstance endpoint;
        readonly IStartableEndpointWithExternallyManagedContainer startableEndpoint;
        readonly IServiceProvider serviceProvider;
        private ServiceCollection childCollection;
        private ServiceProvider childServiceProvider;
        private SessionProvider sessionProvider;
        private string endpointName;
        private IDisposable managementScope;
        private IServiceCollection parentServiceCollection;
    }

     class ServiceCollectionAdapter : IConfigureComponents
    {
        public ServiceCollectionAdapter(IServiceCollection serviceCollection)
        {
            this.serviceCollection = serviceCollection;
        }

        public void ConfigureComponent(Type concreteComponent, DependencyLifecycle dependencyLifecycle)
        {
            if (serviceCollection.Any(s => s.ServiceType == concreteComponent))
            {
                return;
            }

            var serviceLifetime = Map(dependencyLifecycle);
            serviceCollection.Add(new ServiceDescriptor(concreteComponent, concreteComponent, serviceLifetime));
            RegisterInterfaces(concreteComponent,serviceLifetime);
        }

        public void ConfigureComponent<T>(DependencyLifecycle dependencyLifecycle)
        {
            ConfigureComponent(typeof(T), dependencyLifecycle);
        }

        public void ConfigureComponent<T>(Func<T> componentFactory, DependencyLifecycle dependencyLifecycle)
        {
            var componentType = typeof(T);
            if (serviceCollection.Any(s => s.ServiceType == componentType))
            {
                return;
            }

            var serviceLifetime = Map(dependencyLifecycle);
            serviceCollection.Add(new ServiceDescriptor(componentType, p => componentFactory(), serviceLifetime));
            RegisterInterfaces(componentType, serviceLifetime);
        }

        public void ConfigureComponent<T>(Func<IBuilder, T> componentFactory, DependencyLifecycle dependencyLifecycle)
        {
            var componentType = typeof(T);
            if (serviceCollection.Any(s => s.ServiceType == componentType))
            {
                return;
            }

            var serviceLifetime = Map(dependencyLifecycle);
            serviceCollection.Add(new ServiceDescriptor(componentType, p => componentFactory(new ServiceProviderAdapter(p)), serviceLifetime));
            RegisterInterfaces(componentType, serviceLifetime);
        }

        public bool HasComponent<T>()
        {
            return HasComponent(typeof(T));
        }

        public bool HasComponent(Type componentType)
        {
            return serviceCollection.Any(sd => sd.ServiceType == componentType);
        }

        public void RegisterSingleton(Type lookupType, object instance)
        {
            serviceCollection.AddSingleton(lookupType, instance);
        }

        public void RegisterSingleton<T>(T instance)
        {
            RegisterSingleton(typeof(T), instance);
        }

        void RegisterInterfaces(Type component, ServiceLifetime serviceLifetime)
        {
            var interfaces = component.GetInterfaces();
            foreach (var serviceType in interfaces)
            {
                // see https://andrewlock.net/how-to-register-a-service-with-multiple-interfaces-for-in-asp-net-core-di/
                serviceCollection.Add(new ServiceDescriptor(serviceType, sp => sp.GetService(component), serviceLifetime));
            }
        }

        IServiceCollection serviceCollection;

        static ServiceLifetime Map(DependencyLifecycle lifetime)
        {
            switch (lifetime)
            {
                case DependencyLifecycle.SingleInstance: return ServiceLifetime.Singleton;
                case DependencyLifecycle.InstancePerCall: return ServiceLifetime.Transient;
                case DependencyLifecycle.InstancePerUnitOfWork: return ServiceLifetime.Scoped;
                default: throw new NotSupportedException();
            }
        }
    }

      class ServiceProviderAdapter : IBuilder
    {
        public ServiceProviderAdapter(IServiceProvider serviceProvider)
        {
            this.serviceProvider = serviceProvider;
        }

        public object Build(Type typeToBuild)
        {
            return serviceProvider.GetService(typeToBuild) ?? throw new Exception($"Unable to build {typeToBuild.FullName}. Ensure the type has been registered correctly with the container.");
        }

        public T Build<T>()
        {
            return (T)Build(typeof(T));
        }

        public IEnumerable<T> BuildAll<T>()
        {
            return (IEnumerable<T>)BuildAll(typeof(T));
        }

        public IEnumerable<object> BuildAll(Type typeToBuild)
        {
            return serviceProvider.GetServices(typeToBuild);
        }

        public void BuildAndDispatch(Type typeToBuild, Action<object> action)
        {
            action(Build(typeToBuild));
        }

        public IBuilder CreateChildBuilder()
        {
            return new ChildScopeAdapter(serviceProvider.CreateScope());
        }

        public void Dispose()
        {
            //no-op
        }

        public void Release(object instance)
        {
            //no-op
        }

        IServiceProvider serviceProvider;

        class ChildScopeAdapter : IBuilder
        {
            public ChildScopeAdapter(IServiceScope serviceScope)
            {
                this.serviceScope = serviceScope;
            }

            public object Build(Type typeToBuild)
            {
                return serviceScope.ServiceProvider.GetService(typeToBuild) ?? throw new Exception($"Unable to build {typeToBuild.FullName}. Ensure the type has been registered correctly with the container.");
            }

            public T Build<T>()
            {
                return (T)Build(typeof(T));
            }

            public IEnumerable<T> BuildAll<T>()
            {
                return (IEnumerable<T>)BuildAll(typeof(T));
            }

            public IEnumerable<object> BuildAll(Type typeToBuild)
            {
                return serviceScope.ServiceProvider.GetServices(typeToBuild);
            }

            public void BuildAndDispatch(Type typeToBuild, Action<object> action)
            {
                action(Build(typeToBuild));
            }

            public IBuilder CreateChildBuilder()
            {
                throw new InvalidOperationException();
            }

            public void Dispose()
            {
                serviceScope.Dispose();
            }

            public void Release(object instance)
            {
                //no-op
            }

            IServiceScope serviceScope;
        }
    }
}