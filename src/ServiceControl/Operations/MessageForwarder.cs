﻿namespace ServiceControl.Operations
{
    using System.Threading.Tasks;
    using NServiceBus;
    using NServiceBus.Routing;
    using NServiceBus.Transport;

    public interface IForwardMessages
    {
        Task Forward(MessageContext messageContext, string forwardingAddress);
    }

    public class MessageForwarder : IForwardMessages
    {
        public MessageForwarder(IDispatchMessages messageDispatcher)
        {
            this.messageDispatcher = messageDispatcher;
        }

        public Task Forward(MessageContext messageContext, string forwardingAddress)
        {
            var outgoingMessage = new OutgoingMessage(
                messageContext.MessageId,
                messageContext.Headers,
                messageContext.Body);

            // Forwarded messages should last as long as possible
            outgoingMessage.Headers.Remove(Headers.TimeToBeReceived);

            var transportOperations = new TransportOperations(
                new TransportOperation(outgoingMessage, new UnicastAddressTag(forwardingAddress))
            );

            return messageDispatcher.Dispatch(
                transportOperations,
                messageContext.TransportTransaction,
                messageContext.Extensions
            );
        }

        IDispatchMessages messageDispatcher;
    }
}
