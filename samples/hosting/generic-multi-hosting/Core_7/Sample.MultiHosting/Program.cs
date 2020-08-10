﻿using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NServiceBus;

class Program
{
    static async Task Main(string[] args)
    {
        Console.Title = "Samples.MultiHosting";

        var endpointOneBuilder = ConfigureEndpointOne(Host.CreateDefaultBuilder(args)).Build();
        var endpointTwoBuilder = ConfigureEndpointTwo(Host.CreateDefaultBuilder(args)).Build();
        
        var endpointOneTask = endpointOneBuilder.RunAsync();
        var endpointTwoTask = endpointTwoBuilder.RunAsync();
        
        await Task.WhenAll(endpointOneTask, endpointTwoTask);
    }
    
    static IHostBuilder ConfigureEndpointOne(IHostBuilder builder)
    {
        builder.UseConsoleLifetime();
        builder.ConfigureLogging((ctx, logging) =>
        {
            logging.AddConfiguration(ctx.Configuration.GetSection("Logging"));
            logging.AddEventLog();
            logging.AddConsole();
        });
        
        builder.UseNServiceBus(ctx =>
        {
            var endpointConfiguration = new EndpointConfiguration("Instance1");
            var scanner = endpointConfiguration.AssemblyScanner();
            scanner.ExcludeAssemblies("Instance2");
            
            endpointConfiguration.UseTransport<LearningTransport>();
            endpointConfiguration.DefineCriticalErrorAction(OnCriticalError);

            return endpointConfiguration;
        });

        return builder;
    }
    
    static IHostBuilder ConfigureEndpointTwo(IHostBuilder builder)
    {
        builder.UseConsoleLifetime();
        builder.ConfigureLogging((ctx, logging) =>
        {
            logging.AddConfiguration(ctx.Configuration.GetSection("Logging"));
            logging.AddEventLog();
            logging.AddConsole();
        });
        
        builder.UseNServiceBus(ctx =>
        {
            var endpointConfiguration = new EndpointConfiguration("Instance2");
            var scanner = endpointConfiguration.AssemblyScanner();
            scanner.ExcludeAssemblies("Instance1");
            
            endpointConfiguration.UseTransport<LearningTransport>();
            endpointConfiguration.DefineCriticalErrorAction(OnCriticalError);

            return endpointConfiguration;
        });
        
        return builder;
    }

    static async Task OnCriticalError(ICriticalErrorContext context)
    {
        var fatalMessage = "The following critical error was " +
                           $"encountered: {Environment.NewLine}{context.Error}{Environment.NewLine}Process is shutting down. " +
                           $"StackTrace: {Environment.NewLine}{context.Exception.StackTrace}";
        
        EventLog.WriteEntry(".NET Runtime", fatalMessage, EventLogEntryType.Error);

        try
        {
            await context.Stop().ConfigureAwait(false);
        }
        finally
        {
            Environment.FailFast(fatalMessage, context.Exception);
        }
    }
}