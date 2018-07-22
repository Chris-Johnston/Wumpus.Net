﻿using System;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Voltaic;
using Voltaic.Serialization;
using Wumpus.Events;
using Wumpus.Requests;
using Wumpus.Serialization;

namespace Wumpus
{
    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Disconnecting
    }

    public enum StopReason
    {
        Unknown,
        AuthFailed,
        ShardsTooSmall,
        UrlNotFound,
        Exception,
        Canceled
    }

    public class WumpusGatewayClient : IDisposable
    {
        public const int InitialBackoffMillis = 1000; // 1 second
        public const int MaxBackoffMillis = 120000; // 2 mins
        public const double BackoffMultiplier = 1.75; // 1.75x
        public const double BackoffJitter = 0.25; // 1.5x to 2.0x
        public const int ConnectionTimeoutMillis = 20000; // 20 seconds
        
        public event Action Connected;
        public event Action<Exception> Disconnected;
        public event Action<StopReason, Exception> Stopped;
        public event Action<GatewayPayload, ReadOnlyMemory<byte>> ReceivedPayload;
        public event Action<GatewayPayload, ReadOnlyMemory<byte>> SentPayload;

        private static Utf8String LibraryName { get; } = new Utf8String("Wumpus.Net");
        private static Utf8String OsName { get; } =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? new Utf8String("Windows") :
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? new Utf8String("Linux") :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? new Utf8String("OSX") :
            new Utf8String("Unknown");

        private readonly WumpusEtfSerializer _serializer;
        private readonly ClientWebSocket _client;
        private readonly SemaphoreSlim _stateLock;

        // Instance
        private ResizableMemory<byte> _receiveBuffer;
        private ConnectionState _state;
        private Task _connectionTask;
        private CancellationTokenSource _connectionCts;
        private Utf8String _sessionId;

        // Run (Start/Stop)
        private int _lastSeq;
        private string _url;
        private int? _shardId;
        private int? _totalShards;

        // Connection (For each WebSocket connection)
        private BlockingCollection<GatewayPayload> _sendQueue;

        public AuthenticationHeaderValue Authorization { get; set; }

        public WumpusGatewayClient(WumpusEtfSerializer serializer = null)
        {
            _serializer = serializer ?? new WumpusEtfSerializer();
            _receiveBuffer = new ResizableMemory<byte>(10 * 1024); // 10 KB
            _client = new ClientWebSocket();
            _stateLock = new SemaphoreSlim(1, 1);
            _connectionTask = Task.CompletedTask;
            _connectionCts = new CancellationTokenSource();
        }

        public void Run(string url, int? shardId = null, int? totalShards = null, UpdateStatusParams initialPresence = null)
            => RunAsync(url, shardId, totalShards, initialPresence).GetAwaiter().GetResult();
        public async Task RunAsync(string url, int? shardId = null, int? totalShards = null, UpdateStatusParams initialPresence = null)
        {
            Task exceptionSignal;
            await _stateLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopAsyncInternal().ConfigureAwait(false);

                _url = url;
                _shardId = shardId;
                _totalShards = totalShards;
                _connectionCts = new CancellationTokenSource();
                
                _connectionTask = RunTaskAsync(initialPresence, _connectionCts.Token);
                exceptionSignal = _connectionTask;
            }
            finally
            {
                _stateLock.Release();
            }
            await exceptionSignal.ConfigureAwait(false);
        }
        private async Task RunTaskAsync(UpdateStatusParams initialPresence, CancellationToken cancelToken)
        {
            Task[] tasks = null;
            bool isRecoverable = true;
            int backoffMillis = InitialBackoffMillis;
            var jitter = new Random();

            while (isRecoverable)
            {
                Exception disconnectEx = null;
                try
                {
                    cancelToken.ThrowIfCancellationRequested();
                    _state = ConnectionState.Connecting;

                    var uri = new Uri(_url + $"?v={DiscordGatewayConstants.APIVersion}&encoding=etf");// &compress=zlib-stream"); // TODO: Enable
                    try
                    {
                        await _client.ConnectAsync(uri, cancelToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        throw;
                    }

                    // Receive HELLO
                    var evnt = await ReceiveAsync(cancelToken); // We should not timeout on receiving HELLO
                    if (!(evnt.Data is HelloEvent helloEvent))
                        throw new Exception("First event was not a HELLO event");
                    int heartbeatRate = helloEvent.HeartbeatInterval;

                    var readySignal = new TaskCompletionSource<bool>();

                    // Start async loops
                    tasks = new[]
                    {
                        RunSendAsync(cancelToken),
                        RunHeartbeatAsync(heartbeatRate, cancelToken),
                        RunReceiveAsync(readySignal, cancelToken)
                    };

                    // Send IDENTIFY/RESUME
                    SendIdentify(initialPresence);
                    var timeoutTask = Task.Delay(ConnectionTimeoutMillis);
                    if (await Task.WhenAny(timeoutTask, readySignal.Task).ConfigureAwait(false) == timeoutTask)
                        throw new TimeoutException("Timed out waiting for READY or InvalidSession");

                    // Success
                    _lastSeq = 0;
                    backoffMillis = InitialBackoffMillis;
                    _state = ConnectionState.Connected;
                    Connected?.Invoke();

                    await Task.WhenAny(tasks).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    disconnectEx = ex;
                    isRecoverable = IsRecoverable(ex);
                    if (!isRecoverable)
                        throw;
                } 
                finally
                {
                    var oldState = _state;
                    _state = ConnectionState.Disconnecting;

                    // Wait for the other task to complete
                    _connectionCts?.Cancel();
                    if (tasks != null)
                    {
                        for (int i = 0; i < tasks.Length; i++)
                        {
                            try { await tasks[i].ConfigureAwait(false); }
                            catch (OperationCanceledException) { }
                        }
                    }

                    // receiveTask and sendTask must have completed before we can send/receive from a different thread
                    try { await _client.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false); }
                    catch { } // We don't actually care if sending a close msg fails // TODO: Maybe log it?

                    _sessionId = null;
                    _state = ConnectionState.Disconnected;
                    if (oldState == ConnectionState.Connected)
                        Disconnected?.Invoke(disconnectEx);

                    if (isRecoverable)
                    {
                        await Task.Delay(backoffMillis);
                        backoffMillis = (int)(backoffMillis * (BackoffMultiplier + (jitter.NextDouble() * BackoffJitter * 2.0 - BackoffJitter)));
                        if (backoffMillis > MaxBackoffMillis)
                            backoffMillis = MaxBackoffMillis;
                    }
                }
            }
        }
        private Task RunReceiveAsync(TaskCompletionSource<bool> readySignal, CancellationToken cancelToken)
        {
            return Task.Run(async () =>
            {
                while (!cancelToken.IsCancellationRequested)
                    HandleEvent(await ReceiveAsync(cancelToken).ConfigureAwait(false));
            });
        }
        private Task RunSendAsync(CancellationToken cancelToken)
        {
            return Task.Run(async () =>
            {
                try
                {
                    _sendQueue = new BlockingCollection<GatewayPayload>();
                    while (!cancelToken.IsCancellationRequested)
                    {
                        var payload = _sendQueue.Take(cancelToken);
                        var writer = _serializer.Write(payload);
                        await _client.SendAsync(writer.AsSegment(), WebSocketMessageType.Binary, true, cancelToken);
                        SentPayload?.Invoke(payload, writer.AsReadOnlyMemory());
                    }
                }
                finally
                {
                    _sendQueue = null;
                }
            });
        }
        private Task RunHeartbeatAsync(int rate, CancellationToken cancelToken)
        {
            return Task.Run(async () =>
            {
                while (!cancelToken.IsCancellationRequested)
                {
                    SendHeartbeat();
                    await Task.Delay(rate, cancelToken).ConfigureAwait(false);
                }
            });
        }

        private bool IsRecoverable(Exception ex)
        {
            switch (ex)
            {
                case WebSocketException wsEx:
                    return wsEx.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely;
            }
            return false;
        }

        public void Stop()
            => StopAsync().GetAwaiter().GetResult();
        public async Task StopAsync()
        {
            await _stateLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopAsyncInternal().ConfigureAwait(false);
            }
            finally
            {
                _stateLock.Release();
            }
        }
        private async Task StopAsyncInternal()
        {
            _connectionCts?.Cancel(); // Cancel any connection attempts or active connections

            await _connectionTask.ConfigureAwait(false); // Wait for current connection to complete
            _connectionTask = Task.CompletedTask;

            // Double check that the connection task terminated successfully
            var state = _state;
            if (state != ConnectionState.Disconnected)
                throw new InvalidOperationException($"Client did not successfully disconnect (State = {state}).");
        }

        public void Dispose()
        {
            Stop();
            _client.Dispose();
        }

        private async Task<GatewayPayload> ReceiveAsync(CancellationToken cancelToken)
        {
            _receiveBuffer.Clear();

            WebSocketReceiveResult result;
            do
            {
                var buffer = _receiveBuffer.GetSegment(10 * 1024); // 10 KB
                result = await _client.ReceiveAsync(buffer, cancelToken).ConfigureAwait(false);
                _receiveBuffer.Advance(result.Count);

                if (result.CloseStatus != null)
                {
                    if (!string.IsNullOrEmpty(result.CloseStatusDescription))
                        throw new Exception($"WebSocket was closed: {result.CloseStatus.Value} ({result.CloseStatusDescription})"); // TODO: Exception type?
                    else
                        throw new Exception($"WebSocket was closed: {result.CloseStatus.Value}"); // TODO: Exception type?
                }
            }
            while (!result.EndOfMessage);

            var payload = _serializer.Read<GatewayPayload>(_receiveBuffer.AsReadOnlySpan());

            if (payload.Sequence.HasValue)
                _lastSeq = payload.Sequence.Value;

            ReceivedPayload?.Invoke(payload, _receiveBuffer.AsReadOnlyMemory());
            return payload;
        }
        private void HandleEvent(GatewayPayload evnt)
        {
            switch (evnt.Operation)
            {
                case GatewayOperation.Dispatch:
                    HandleDispatchEvent(evnt);
                    break;
                case GatewayOperation.InvalidSession:
                    _sessionId = null;
                    break;
            }
        }
        private void HandleDispatchEvent(GatewayPayload evnt)
        {
            switch (evnt.DispatchType)
            {
                case GatewayDispatchType.Ready:
                    if (!(evnt.Data is GatewayReadyEvent readyEvent))
                        throw new Exception("Failed to deserialize READY event"); // TODO: Exception type?
                    _sessionId = readyEvent.SessionId;
                    break;
            }
        }

        public void Send(GatewayPayload payload)
        {
            if (!_connectionCts.IsCancellationRequested)
                _sendQueue?.Add(payload);
        }
        private void SendIdentify(UpdateStatusParams initialPresence)
        {
            if (_sessionId == (Utf8String)null) // IDENTITY
            {
                Send(new GatewayPayload
                {
                    Operation = GatewayOperation.Identify,
                    Data = new IdentifyParams
                    {
                        Compress = false, // We don't want payload compression
                        LargeThreshold = 50,
                        Presence = Optional.FromNullable(initialPresence),
                        Properties = new IdentityConnectionProperties
                        {
                            Os = OsName,
                            Browser = LibraryName,
                            Device = LibraryName
                        },
                        Shard = _shardId != null && _totalShards != null ? new int[] { _shardId.Value, _totalShards.Value } : Optional.Create<int[]>(),
                        Token = Authorization != null ? new Utf8String(Authorization.Parameter) : null
                    }
                });
            }
            else // RESUME
            {
                Send(new GatewayPayload
                {
                    Operation = GatewayOperation.Resume,
                    Data = new ResumeParams
                    {
                        Sequence = _lastSeq,
                        SessionId = _sessionId // TODO: Handle READY and get sessionId
                    }
                });
            }
        }
        public void SendHeartbeat() => Send(new GatewayPayload
        {
            Operation = GatewayOperation.Heartbeat,
            Data = _lastSeq == 0 ? (int?)null : _lastSeq
        });
    }
}
