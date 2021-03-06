﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Core;
using Microsoft.Health.SqlServer.Configs;
using Microsoft.Health.SqlServer.Features.Schema.Extensions;

namespace Microsoft.Health.SqlServer.Features.Schema
{
    /// <summary>
    /// The worker responsible for running the schema job.
    /// It inserts the instance schema information.
    /// It polls the specified time to update the instance schema information and deletes the expired instance schema information, if any.
    /// </summary>
    public class SchemaJobWorker
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly SqlServerDataStoreConfiguration _sqlServerDataStoreConfiguration;
        private readonly IMediator _mediator;
        private readonly ILogger _logger;

        public SchemaJobWorker(
            IServiceProvider services,
            SqlServerDataStoreConfiguration sqlServerDataStoreConfiguration,
            IMediator mediator,
            ILogger<SchemaJobWorker> logger)
        {
            EnsureArg.IsNotNull(services, nameof(services));
            EnsureArg.IsNotNull(sqlServerDataStoreConfiguration, nameof(sqlServerDataStoreConfiguration));
            EnsureArg.IsNotNull(mediator, nameof(mediator));
            EnsureArg.IsNotNull(logger, nameof(logger));

            _serviceProvider = services;
            _sqlServerDataStoreConfiguration = sqlServerDataStoreConfiguration;
            _mediator = mediator;
            _logger = logger;
        }

        public async Task ExecuteAsync(SchemaInformation schemaInformation, string instanceName, CancellationToken cancellationToken)
        {
            _logger.LogInformation($"Polling started at {Clock.UtcNow}");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var schemaDataStore = scope.ServiceProvider.GetRequiredService<ISchemaDataStore>();

                        int? previous = schemaInformation.Current;
                        schemaInformation.Current = await schemaDataStore.UpsertInstanceSchemaInformationAsync(instanceName, schemaInformation, cancellationToken);

                        // If there was a change in the schema version and this isn't the base schema
                        if (schemaInformation.Current != previous && schemaInformation.Current > 0)
                        {
                            var isFullSchemaSnapshot = previous == 0;

                            await _mediator.NotifySchemaUpgradedAsync((int)schemaInformation.Current, isFullSchemaSnapshot);
                        }

                        await schemaDataStore.DeleteExpiredInstanceSchemaAsync(cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    // The job failed.
                    _logger.LogError(ex, "Unhandled exception in the worker.");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_sqlServerDataStoreConfiguration.SchemaOptions.JobPollingFrequencyInSeconds), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Cancel requested.
                    break;
                }
            }
        }
    }
}
