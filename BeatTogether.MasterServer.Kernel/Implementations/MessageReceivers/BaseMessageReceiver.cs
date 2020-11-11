﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BeatTogether.MasterServer.Kernel.Abstractions;
using BeatTogether.MasterServer.Kernel.Delegates;
using BeatTogether.MasterServer.Messaging.Abstractions.Messages;
using BeatTogether.MasterServer.Messaging.Implementations.Messages;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace BeatTogether.MasterServer.Kernel.Implementations
{
    public abstract class BaseMessageReceiver<TService> : IMessageReceiver
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IMultipartMessageService _multipartMessageService;
        private readonly IMessageDispatcher _messageDispatcher;
        private readonly ILogger _logger;

        private readonly Dictionary<Type, MessageHandler<TService>> _messageHandlers;

        public BaseMessageReceiver(
            IServiceProvider serviceProvider,
            IMultipartMessageService multipartMessageService,
            IMessageDispatcher messageDispatcher)
        {
            _serviceProvider = serviceProvider;
            _multipartMessageService = multipartMessageService;
            _messageDispatcher = messageDispatcher;
            _logger = Log.ForContext<BaseMessageReceiver<TService>>();

            _messageHandlers = new Dictionary<Type, MessageHandler<TService>>();

            AddMessageHandler<MultipartMessage>(
                (service, session, message) => _multipartMessageService.HandleMultipartMessage(session, message)
            );
            AddMessageHandler<AcknowledgeMessage>(
                (service, session, message) => HandleAcknowledgeMessage(session, message)
            );
        }

        #region Public Methods

        public async Task OnReceived(ISession session, IMessage message)
        {
            var messageType = message.GetType();
            if (!_messageHandlers.TryGetValue(messageType, out var messageHandler))
            {
                _logger.Warning(
                    "Failed to retrieve message handler for message of type " +
                    $"'{messageType.Name}'."
                );
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<TService>();
            await messageHandler(service, session, message)
                .ConfigureAwait(false);
        }

        #endregion

        #region Protected Methods

        protected void AddMessageHandler<TMessage>(MessageHandler<TService, TMessage> messageHandler)
            where TMessage : class, IMessage
            => _messageHandlers[typeof(TMessage)] =
                    (service, session, message) => messageHandler(service, session, (TMessage)message);

        protected void AddMessageHandler<TRequest, TResponse>(MessageHandler<TService, TRequest, TResponse> messageHandler)
            where TRequest : class, IMessage
            where TResponse : class, IMessage
            => AddMessageHandler<TRequest>(async (service, session, request) =>
            {
                var response = await messageHandler(service, session, request);
                if (response == null)
                    return;
                if (request is IReliableRequest reliableRequest &&
                    response is IReliableResponse reliableResponse)
                {
                    if (reliableResponse.ResponseId == 0)
                        reliableResponse.ResponseId = reliableRequest.RequestId;
                }
                _messageDispatcher.Send(session, response);
            });

        #endregion

        #region Private Methods

        private Task HandleAcknowledgeMessage(ISession session, AcknowledgeMessage message)
        {
            _logger.Verbose(
                $"Handling {nameof(AcknowledgeMessage)} " +
                $"(ResponseId={message.ResponseId}, " +
                $"MessageHandled={message.MessageHandled})."
            );
            // TODO: Properly acknowledge that requests were handled (and retry if they weren't)
            return Task.CompletedTask;
        }

        #endregion
    }
}
