﻿using System;
using System.Net.Sockets;
using ECommon.IoC;
using ECommon.Logging;

namespace ECommon.Socketing
{
    public class SocketService
    {
        private ILogger _logger;
        private Action<SocketInfo, Exception> _socketReceiveExceptionAction;

        public SocketService(Action<SocketInfo, Exception> socketReceiveExceptionAction)
        {
            _socketReceiveExceptionAction = socketReceiveExceptionAction;
            _logger = ObjectContainer.Resolve<ILoggerFactory>().Create(GetType().Name);
        }
        public void SendMessage(Socket targetSocket, byte[] message, Action<SendResult> messageSentCallback)
        {
            var wrappedMessage = SocketUtils.BuildMessage(message);
            if (wrappedMessage.Length > 0)
            {
                targetSocket.BeginSend(
                    wrappedMessage,
                    0,
                    wrappedMessage.Length,
                    SocketFlags.None,
                    new AsyncCallback(SendCallback),
                    new SendContext(targetSocket, wrappedMessage, messageSentCallback));
            }
        }
        public void ReceiveMessage(SocketInfo sourceSocket, Action<byte[]> messageReceivedCallback)
        {
            ReceiveInternal(new ReceiveState(sourceSocket, messageReceivedCallback), 4);
        }

        private void ReceiveInternal(ReceiveState receiveState, int size)
        {
            receiveState.SourceSocket.InnerSocket.BeginReceive(receiveState.Buffer, 0, size, 0, ReceiveCallback, receiveState);
        }
        private void SendCallback(IAsyncResult asyncResult)
        {
            var sendContext = (SendContext)asyncResult.AsyncState;
            try
            {
                sendContext.TargetSocket.EndSend(asyncResult);
                sendContext.MessageSendCallback(new SendResult(true, null));
            }
            catch (SocketException socketException)
            {
                sendContext.MessageSendCallback(new SendResult(false, socketException));
            }
            catch (Exception ex)
            {
                sendContext.MessageSendCallback(new SendResult(false, ex));
            }
        }
        private void ReceiveCallback(IAsyncResult asyncResult)
        {
            var receiveState = (ReceiveState)asyncResult.AsyncState;
            var sourceSocketInfo = receiveState.SourceSocket;
            var sourceSocket = sourceSocketInfo.InnerSocket;
            var receivedData = receiveState.Data;
            var bytesRead = 0;
            if (!sourceSocket.Connected)
            {
                return;
            }

            try
            {
                bytesRead = sourceSocket.EndReceive(asyncResult);
            }
            catch (SocketException socketException)
            {
                if (_socketReceiveExceptionAction != null)
                {
                    _socketReceiveExceptionAction(sourceSocketInfo, socketException);
                }
            }
            catch (Exception ex)
            {
                if (_socketReceiveExceptionAction != null)
                {
                    _socketReceiveExceptionAction(sourceSocketInfo, ex);
                }
            }

            if (bytesRead > 0)
            {
                if (receiveState.MessageSize == null)
                {
                    receiveState.MessageSize = SocketUtils.ParseMessageLength(receiveState.Buffer);
                    var size = receiveState.MessageSize <= ReceiveState.BufferSize ? receiveState.MessageSize.Value : ReceiveState.BufferSize;
                    ReceiveInternal(receiveState, size);
                }
                else
                {
                    for (var index = 0; index < bytesRead; index++)
                    {
                        receivedData.Add(receiveState.Buffer[index]);
                    }
                    if (receivedData.Count < receiveState.MessageSize.Value)
                    {
                        var remainSize = receiveState.MessageSize.Value - receivedData.Count;
                        var size = remainSize <= ReceiveState.BufferSize ? remainSize : ReceiveState.BufferSize;
                        ReceiveInternal(receiveState, size);
                    }
                    else
                    {
                        receiveState.MessageReceivedCallback(receivedData.ToArray());
                        receiveState.MessageSize = null;
                        receivedData.Clear();
                        ReceiveInternal(receiveState, 4);
                    }
                }
            }
        }
    }
}
