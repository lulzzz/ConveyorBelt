﻿using BeeHive;
using ConveyorBelt.Tooling.Internal;
using ConveyorBelt.Tooling.Parsing;
using Microsoft.ServiceBus.Messaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConveyorBelt.Tooling.EventHub
{
    public class EventHubConsumer : IEventProcessorFactory, IDisposable
    {
        public static readonly ConcurrentDictionary<string, Lazy<EventHubConsumer>> Consumers =
            new ConcurrentDictionary<string, Lazy<EventHubConsumer>>();

        private readonly NestBatchPusher _pusher;
        public DiagnosticsSourceSummary Source { get; }
        private readonly IParser _parser;
        private readonly EventProcessorHost _eventProcessorHost;

        /// <summary>
        /// Custom Source Properties:
        ///     1- Parser
        ///     2- EventHubName
        ///     3- StorageConnectionString
        /// </summary>
        /// <param name="pusher"></param>
        /// <param name="source"></param>
        public EventHubConsumer(NestBatchPusher pusher, DiagnosticsSourceSummary source)
        {
            this._pusher = pusher;
            this.Source = source;
            _parser = FactoryHelper.Create<IParser>(source.DynamicProperties["Parser"].ToString());

            _eventProcessorHost = new EventProcessorHost(
                "ConveyorBelt",
                source.DynamicProperties["EventHubName"].ToString(), 
                EventHubConsumerGroup.DefaultGroupName, 
                source.ConnectionString, 
                source.DynamicProperties["StorageConnectionString"].ToString());

            var options = new EventProcessorOptions();
            options.ExceptionReceived += (sender, e) => { TheTrace.TraceError(e.Exception.ToString()); };
            _eventProcessorHost.RegisterEventProcessorFactoryAsync(this, options).Wait();
        }

        public IEventProcessor CreateEventProcessor(PartitionContext context)
        {
            return new EventProcessor(_pusher, _parser, Source);
        }

        public void Dispose()
        {
            _eventProcessorHost.UnregisterEventProcessorAsync().Wait();
        }

        internal class EventProcessor : IEventProcessor
        {
            private readonly NestBatchPusher _elasticsearchBatchPusher;
            private readonly IParser _parser;
            private readonly DiagnosticsSourceSummary _source;
            private readonly Stopwatch _timer = Stopwatch.StartNew();

            private readonly TimeSpan _checkpointInterval = TimeSpan.FromMinutes(1);

            public EventProcessor(NestBatchPusher elasticsearchBatchPusher,
                IParser parser,
                DiagnosticsSourceSummary source)
            {
                this._elasticsearchBatchPusher = elasticsearchBatchPusher;
                this._parser = parser;
                this._source = source;
            }

            public Task CloseAsync(PartitionContext context, CloseReason reason)
            {
                return Task.FromResult(false);
            }

            public Task OpenAsync(PartitionContext context)
            {
                return Task.FromResult(false);
            }

            public async Task ProcessEventsAsync(PartitionContext context, IEnumerable<EventData> messages)
            {
                try
                {
                    var lazyEnumerable = messages.SelectMany(ev => _parser.Parse(ev.GetBodyStream, null, _source));
                    await _elasticsearchBatchPusher.PushAll(lazyEnumerable, _source);

                    if (_timer.Elapsed > _checkpointInterval)
                    {
                        _timer.Restart();
                        await context.CheckpointAsync();
                    }
                }
                catch (Exception e)
                {
                    TheTrace.TraceError(e.ToString());
                }                
            }
        }
    }
}
