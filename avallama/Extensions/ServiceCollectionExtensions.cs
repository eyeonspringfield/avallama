// Copyright (c) Márk Csörgő and Martin Bartos
// Licensed under the MIT License. See LICENSE file for details.

using System;
using System.Collections.Generic;
using System.Threading.RateLimiting;
using avallama.Constants.Application;
using avallama.ViewModels;
using avallama.Factories;
using avallama.Services;
using avallama.Services.Ollama;
using avallama.Services.Persistence;
using avallama.Services.Queue;
using avallama.Utilities;
using avallama.Utilities.Network;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace avallama.Extensions;

public static class ServiceCollectionExtensions
{
    extension(IServiceCollection collection)
    {
        public void AddCommonServices()
        {
            // Instantiate dependencies here
            // Singleton - Available in memory for the lifetime of the application
            // Transient - Created when needed, disposed when no longer in use

            // Take caution with cyclic dependencies, as they may not throw exceptions, but fail to instantiate anything while the application continues to run,
            // making debugging difficult, e.g., AppService -> MainViewModel -> PageFactory -> Func<...> -> HomeViewModel -> AppService

            // Weak reference messenger, which means they do not need to be manually deleted
            collection.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);

            collection.AddSingleton<IAvaloniaDispatcher, AvaloniaDispatcher>();

            // Temporary registration of both interfaces and concrete implementations until refactoring is done
            collection.AddSingleton<ApplicationService>();
            collection.AddSingleton<IApplicationService>(sp => sp.GetRequiredService<ApplicationService>());

            collection.AddSingleton<ConfigurationService>();
            collection.AddSingleton<IConfigurationService>(sp => sp.GetRequiredService<ConfigurationService>());

            collection.AddTransient<DatabaseInitService>();
            collection.AddTransient<IDatabaseInitService>(sp => sp.GetRequiredService<DatabaseInitService>());

            collection.AddSingleton<ConversationService>();
            collection.AddSingleton<IConversationService>(sp => sp.GetRequiredService<ConversationService>());

            collection.AddSingleton<OllamaProcessManager>();
            collection.AddSingleton<IOllamaProcessManager>(sp => sp.GetRequiredService<OllamaProcessManager>());

            collection.AddSingleton<OllamaApiClient>();
            collection.AddSingleton<IOllamaApiClient>(sp => sp.GetRequiredService<OllamaApiClient>());

            collection.AddSingleton<OllamaService>();
            collection.AddSingleton<IOllamaService>(sp => sp.GetRequiredService<OllamaService>());

            collection.AddSingleton<NetworkManager>();
            collection.AddSingleton<INetworkManager>(sp => sp.GetRequiredService<NetworkManager>());

            collection.AddSingleton<RateLimiter>(_ => new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = 5,
                TokensPerPeriod = 1,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                QueueLimit = 100,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            }));

            collection.AddTransient<OllamaRateLimitedHandler>();

            collection.AddHttpClient<IOllamaScraper, OllamaScraper>(client =>
                {
                    client.BaseAddress = new Uri("https://www.ollama.com");
                    client.Timeout = TimeSpan.FromSeconds(30);
                })
                .AddHttpMessageHandler<OllamaRateLimitedHandler>();

            // httpClient for quick connection verifications with a timeout of 2 seconds
            collection.AddHttpClient("OllamaCheckHttpClient", client => { client.Timeout = TimeSpan.FromSeconds(2); });

            // httpClient for heavy operations (model initialization, message generation etc.) with a timeout of 5 minutes
            collection.AddHttpClient("OllamaHeavyHttpClient", client => { client.Timeout = TimeSpan.FromMinutes(5); });

            collection.AddSingleton<ModelCacheService>();
            collection.AddSingleton<IModelCacheService>(sp => sp.GetRequiredService<ModelCacheService>());

            collection.AddTransient<UpdateService>();
            collection.AddTransient<IUpdateService>(sp => sp.GetRequiredService<UpdateService>());

            collection.AddSingleton<DialogService>();
            collection.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());

            collection.AddSingleton<ModelDownloadQueueService>();
            collection.AddSingleton<IModelDownloadQueueService>(sp => sp.GetRequiredService<ModelDownloadQueueService>());

            collection.AddSingleton<MainViewModel>();
            collection.AddSingleton<HomeViewModel>();
            collection.AddTransient<GreetingViewModel>();
            collection.AddTransient<SettingsViewModel>();
            collection.AddSingleton<ModelManagerViewModel>();
            collection.AddTransient<GuideViewModel>();
            collection.AddTransient<ScraperViewModel>();

            collection.AddSingleton<PageFactory>();

            // Delegate dependency injection for PageFactory
            // This ensures that all dependencies are handled in App.axaml.cs according to the factory pattern
            // Func<ApplicationPage, PageViewModel> - returns a PageViewModel for a given ApplicationPage
            collection.AddSingleton<Func<ApplicationPage, PageViewModel>>(serviceProvider => page => page switch
            {
                ApplicationPage.Greeting => serviceProvider.GetRequiredService<GreetingViewModel>(),
                ApplicationPage.Home => serviceProvider.GetRequiredService<HomeViewModel>(),
                ApplicationPage.Guide => serviceProvider.GetRequiredService<GuideViewModel>(),
                ApplicationPage.Settings => serviceProvider.GetRequiredService<SettingsViewModel>(),
                ApplicationPage.ModelManager => serviceProvider.GetRequiredService<ModelManagerViewModel>(),
                ApplicationPage.Scraper => serviceProvider.GetRequiredService<ScraperViewModel>(),
                _ => throw new InvalidOperationException() // if there is no Page registered yet, throw an exception
            });

            collection.AddSingleton<IReadOnlyDictionary<ApplicationDialog, DialogRegistration>>(
                /* ServiceProvider */ _ => new Dictionary<ApplicationDialog, DialogRegistration>
                {
                    // since dialogs have been moved, no generic custom dialogs are registered here, but this can be
                    // useful in the future if we have personalized views that need to appear in a separate dialog window

                    // Example for future:
                    // [ApplicationDialog.SomeCustom] = new(
                    //     () => new SomeCustomView(),
                    //     () => serviceProvider.GetRequiredService<SomeCustomDialogViewModel>())
                });
        }
    }
}
