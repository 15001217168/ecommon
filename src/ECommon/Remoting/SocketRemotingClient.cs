﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using ECommon.Extensions;
using ECommon.Components;
using ECommon.Logging;
using ECommon.Scheduling;
using ECommon.Socketing;
using ECommon.Remoting.Exceptions;

namespace ECommon.Remoting
{
    public class SocketRemotingClient
    {
        private readonly string _address;
        private readonly int _port;
        private readonly ClientSocket _clientSocket;
        private readonly ConcurrentDictionary<long, ResponseFuture> _responseFutureDict;
        private readonly BlockingCollection<byte[]> _responseMessageQueue;
        private readonly IScheduleService _scheduleService;
        private readonly ILogger _logger;
        private readonly Worker _processResponseMessageWorker;
        private int _scanTimeoutRequestTaskId;

        public SocketRemotingClient() : this(SocketUtils.GetLocalIPV4().ToString(), 5000) { }
        public SocketRemotingClient(string address, int port)
        {
            _address = address;
            _port = port;
            _clientSocket = new ClientSocket();
            _responseFutureDict = new ConcurrentDictionary<long, ResponseFuture>();
            _responseMessageQueue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());
            _scheduleService = ObjectContainer.Resolve<IScheduleService>();
            _processResponseMessageWorker = new Worker(ProcessResponseMessage);
            _logger = ObjectContainer.Resolve<ILoggerFactory>().Create(GetType().FullName);
            _clientSocket.Connect(address, port);
        }

        public void Start()
        {
            _clientSocket.Start(responseMessage => _responseMessageQueue.Add(responseMessage));
            _processResponseMessageWorker.Start();
            _scanTimeoutRequestTaskId = _scheduleService.ScheduleTask(ScanTimeoutRequest, 1000 * 3, 1000);
        }
        public void Shutdown()
        {
            _clientSocket.Shutdown();
            _processResponseMessageWorker.Stop();
            _scheduleService.ShutdownTask(_scanTimeoutRequestTaskId);
        }
        public RemotingResponse InvokeSync(RemotingRequest request, int timeoutMillis)
        {
            var message = RemotingUtil.BuildRequestMessage(request);
            var taskCompletionSource = new TaskCompletionSource<RemotingResponse>();
            var responseFuture = new ResponseFuture(request, timeoutMillis, taskCompletionSource);
            var response = default(RemotingResponse);

            if (!_responseFutureDict.TryAdd(request.Sequence, responseFuture))
            {
                throw new Exception(string.Format("Try to add response future failed. request sequence:{0}", request.Sequence));
            }
            try
            {
                _clientSocket.SendMessage(message, sendResult => SendMessageCallback(responseFuture, request, _address, sendResult));
                response = taskCompletionSource.Task.WaitResult<RemotingResponse>(timeoutMillis);
            }
            catch (Exception ex)
            {
                throw new RemotingSendRequestException(_address, request, ex);
            }

            if (response == null)
            {
                if (responseFuture.SendRequestSuccess)
                {
                    throw new RemotingTimeoutException(_address, request, timeoutMillis);
                }
                else
                {
                    throw new RemotingSendRequestException(_address, request, responseFuture.SendException);
                }
            }
            return response;
        }
        public Task<RemotingResponse> InvokeAsync(RemotingRequest request, int timeoutMillis)
        {
            var message = RemotingUtil.BuildRequestMessage(request);
            var taskCompletionSource = new TaskCompletionSource<RemotingResponse>();
            var responseFuture = new ResponseFuture(request, timeoutMillis, taskCompletionSource);

            if (!_responseFutureDict.TryAdd(request.Sequence, responseFuture))
            {
                throw new Exception(string.Format("Try to add response future failed. request sequence:{0}", request.Sequence));
            }
            try
            {
                _clientSocket.SendMessage(message, sendResult => SendMessageCallback(responseFuture, request, _address, sendResult));
            }
            catch (Exception ex)
            {
                throw new RemotingSendRequestException(_address, request, ex);
            }

            return taskCompletionSource.Task;
        }
        public void InvokeOneway(RemotingRequest request, int timeoutMillis)
        {
            request.IsOneway = true;
            var message = RemotingUtil.BuildRequestMessage(request);
            try
            {
                _clientSocket.SendMessage(message, x => { });
            }
            catch (Exception ex)
            {
                throw new RemotingSendRequestException(_address, request, ex);
            }
        }

        private void ProcessResponseMessage()
        {
            var responseMessage = _responseMessageQueue.Take();
            var remotingResponse = RemotingUtil.ParseResponse(responseMessage);

            ResponseFuture responseFuture;
            if (_responseFutureDict.TryRemove(remotingResponse.Sequence, out responseFuture))
            {
                responseFuture.CompleteRequestTask(remotingResponse);
            }
            else
            {
                _logger.ErrorFormat("Remoting response returned, but the responseFuture was removed already. request sequence:{0}", remotingResponse.Sequence);
            }
        }
        private void ScanTimeoutRequest()
        {
            var timeoutResponseFutureKeyList = new List<long>();
            foreach (var entry in _responseFutureDict)
            {
                if (entry.Value.IsTimeout())
                {
                    timeoutResponseFutureKeyList.Add(entry.Key);
                }
            }
            foreach (var key in timeoutResponseFutureKeyList)
            {
                ResponseFuture responseFuture;
                if (_responseFutureDict.TryRemove(key, out responseFuture))
                {
                    responseFuture.CompleteRequestTask(null);
                    _logger.WarnFormat("Removed timeout request:{0}", responseFuture.Request);
                }
            }
        }
        private void SendMessageCallback(ResponseFuture responseFuture, RemotingRequest request, string address, SendResult sendResult)
        {
            responseFuture.SendRequestSuccess = sendResult.Success;
            responseFuture.SendException = sendResult.Exception;
            if (!sendResult.Success)
            {
                responseFuture.CompleteRequestTask(null);
                _responseFutureDict.Remove(request.Sequence);
                _logger.ErrorFormat("Send request {0} to channel <{1}> failed, exception:{2}", request, address, sendResult.Exception);
            }
        }
    }
}
