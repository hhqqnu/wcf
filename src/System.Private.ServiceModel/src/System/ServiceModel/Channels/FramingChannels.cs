// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics.Contracts;
using System.IO;
using System.Runtime;
using System.ServiceModel.Security;
using System.Threading.Tasks;

namespace System.ServiceModel.Channels
{
    internal abstract class FramingDuplexSessionChannel : TransportDuplexSessionChannel
    {
        private static EndpointAddress s_anonymousEndpointAddress = new EndpointAddress(EndpointAddress.AnonymousUri, new AddressHeader[0]);
        private IConnection _connection;
        private bool _exposeConnectionProperty;

        private FramingDuplexSessionChannel(ChannelManagerBase manager, IConnectionOrientedTransportFactorySettings settings,
            EndpointAddress localAddress, Uri localVia, EndpointAddress remoteAddresss, Uri via, bool exposeConnectionProperty)
            : base(manager, settings, localAddress, localVia, remoteAddresss, via)
        {
            _exposeConnectionProperty = exposeConnectionProperty;
        }

        protected FramingDuplexSessionChannel(ChannelManagerBase factory, IConnectionOrientedTransportFactorySettings settings,
            EndpointAddress remoteAddresss, Uri via, bool exposeConnectionProperty)
            : this(factory, settings, s_anonymousEndpointAddress, settings.MessageVersion.Addressing == AddressingVersion.None ? null : new Uri("http://www.w3.org/2005/08/addressing/anonymous"),
            remoteAddresss, via, exposeConnectionProperty)
        {
            this.Session = FramingConnectionDuplexSession.CreateSession(this, settings.Upgrade);
        }

        protected IConnection Connection
        {
            get
            {
                return _connection;
            }
            set
            {
                _connection = value;
            }
        }

        protected override bool IsStreamedOutput
        {
            get { return false; }
        }

        protected override void CloseOutputSessionCore(TimeSpan timeout)
        {
            Connection.Write(SessionEncoder.EndBytes, 0, SessionEncoder.EndBytes.Length, true, timeout);
        }

        protected override async Task CloseOutputSessionCoreAsync(TimeSpan timeout)
        {
            var tcs = new TaskCompletionSource<bool>();
            AsyncCompletionResult result = Connection.BeginWrite(SessionEncoder.EndBytes, 0, SessionEncoder.EndBytes.Length, true, timeout, OnIoComplete, tcs);
            if (result == AsyncCompletionResult.Completed)
            {
                tcs.TrySetResult(true);
            }

            await tcs.Task;
            Connection.EndWrite();
        }

        internal static void OnIoComplete(object state)
        {
            if (state == null)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperArgumentNull("state");
            }

            var tcs = state as TaskCompletionSource<bool>;
            if (tcs == null)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperArgument("state", SR.SPS_InvalidAsyncResult);
            }

            tcs.TrySetResult(true);
        }

        protected override void CompleteClose(TimeSpan timeout)
        {
            this.ReturnConnectionIfNecessary(false, timeout);
        }

        protected override void PrepareMessage(Message message)
        {
            if (_exposeConnectionProperty)
            {
                message.Properties[ConnectionMessageProperty.Name] = _connection;
            }
            base.PrepareMessage(message);
        }

        protected override void OnSendCore(Message message, TimeSpan timeout)
        {
            bool allowOutputBatching;
            ArraySegment<byte> messageData;

            allowOutputBatching = message.Properties.AllowOutputBatching;
            messageData = this.EncodeMessage(message);

            this.Connection.Write(messageData.Array, messageData.Offset, messageData.Count, !allowOutputBatching,
                timeout, this.BufferManager);
        }

        protected override AsyncCompletionResult BeginCloseOutput(TimeSpan timeout, Action<object> callback, object state)
        {
            return this.Connection.BeginWrite(SessionEncoder.EndBytes, 0, SessionEncoder.EndBytes.Length,
                    true, timeout, callback, state);
        }

        protected override void FinishWritingMessage()
        {
            this.Connection.EndWrite();
        }

        protected override AsyncCompletionResult StartWritingBufferedMessage(Message message, ArraySegment<byte> messageData, bool allowOutputBatching, TimeSpan timeout, Action<object> callback, object state)
        {
            return this.Connection.BeginWrite(messageData.Array, messageData.Offset, messageData.Count,
                    !allowOutputBatching, timeout, callback, state);
        }

        protected override AsyncCompletionResult StartWritingStreamedMessage(Message message, TimeSpan timeout, Action<object> callback, object state)
        {
            Contract.Assert(false, "Streamed output should never be called in this channel.");
            throw new InvalidOperationException();
        }

        protected override ArraySegment<byte> EncodeMessage(Message message)
        {
            ArraySegment<byte> messageData = MessageEncoder.WriteMessage(message,
                int.MaxValue, this.BufferManager, SessionEncoder.MaxMessageFrameSize);

            messageData = SessionEncoder.EncodeMessageFrame(messageData);

            return messageData;
        }

        internal class FramingConnectionDuplexSession : ConnectionDuplexSession
        {
            private FramingConnectionDuplexSession(FramingDuplexSessionChannel channel)
                : base(channel)
            {
            }

            public static FramingConnectionDuplexSession CreateSession(FramingDuplexSessionChannel channel,
                StreamUpgradeProvider upgrade)
            {
                StreamSecurityUpgradeProvider security = upgrade as StreamSecurityUpgradeProvider;
                if (security == null)
                {
                    return new FramingConnectionDuplexSession(channel);
                }

                throw ExceptionHelper.PlatformNotSupported("SecureConnectionDuplexSession is not supported.");
            }
        }
    }

    internal class ClientFramingDuplexSessionChannel : FramingDuplexSessionChannel
    {
        private IConnectionOrientedTransportChannelFactorySettings _settings;
        private ClientDuplexDecoder _decoder;
        private StreamUpgradeProvider _upgrade;
        private ConnectionPoolHelper _connectionPoolHelper;
        private bool _flowIdentity;

        public ClientFramingDuplexSessionChannel(ChannelManagerBase factory, IConnectionOrientedTransportChannelFactorySettings settings,
            EndpointAddress remoteAddresss, Uri via, IConnectionInitiator connectionInitiator, ConnectionPool connectionPool,
            bool exposeConnectionProperty, bool flowIdentity)
            : base(factory, settings, remoteAddresss, via, exposeConnectionProperty)
        {
            _settings = settings;
            this.MessageEncoder = settings.MessageEncoderFactory.CreateSessionEncoder();
            _upgrade = settings.Upgrade;
            _flowIdentity = flowIdentity;
            _connectionPoolHelper = new DuplexConnectionPoolHelper(this, connectionPool, connectionInitiator);
        }

        private ArraySegment<byte> CreatePreamble()
        {
            EncodedVia encodedVia = new EncodedVia(this.Via.AbsoluteUri);
            EncodedContentType encodedContentType = EncodedContentType.Create(this.MessageEncoder.ContentType);

            // calculate preamble length
            int startSize = ClientDuplexEncoder.ModeBytes.Length + SessionEncoder.CalcStartSize(encodedVia, encodedContentType);
            int preambleEndOffset = 0;
            if (_upgrade == null)
            {
                preambleEndOffset = startSize;
                startSize += ClientDuplexEncoder.PreambleEndBytes.Length;
            }

            byte[] startBytes = Fx.AllocateByteArray(startSize);
            Buffer.BlockCopy(ClientDuplexEncoder.ModeBytes, 0, startBytes, 0, ClientDuplexEncoder.ModeBytes.Length);
            SessionEncoder.EncodeStart(startBytes, ClientDuplexEncoder.ModeBytes.Length, encodedVia, encodedContentType);
            if (preambleEndOffset > 0)
            {
                Buffer.BlockCopy(ClientDuplexEncoder.PreambleEndBytes, 0, startBytes, preambleEndOffset, ClientDuplexEncoder.PreambleEndBytes.Length);
            }

            return new ArraySegment<byte>(startBytes, 0, startSize);
        }

        protected override IAsyncResult OnBeginOpen(TimeSpan timeout, AsyncCallback callback, object state)
        {
            return OnOpenAsync(timeout).ToApm(callback, state);
        }

        protected override void OnEndOpen(IAsyncResult result)
        {
            result.ToApmEnd();
        }

        public override T GetProperty<T>()
        {
            T result = base.GetProperty<T>();

            if (result == null && _upgrade != null)
            {
                result = _upgrade.GetProperty<T>();
            }

            return result;
        }

        private async Task<IConnection> SendPreambleAsync(IConnection connection, ArraySegment<byte> preamble, TimeSpan timeout)
        {
            var timeoutHelper = new TimeoutHelper(timeout);

            // initialize a new decoder
            _decoder = new ClientDuplexDecoder(0);
            byte[] ackBuffer = new byte[1];
            var tcs = new TaskCompletionSource<bool>();
            var result = connection.BeginWrite(preamble.Array, preamble.Offset, preamble.Count, true, timeoutHelper.RemainingTime(), FramingDuplexSessionChannel.OnIoComplete, tcs);
            if (result == AsyncCompletionResult.Completed)
            {
                tcs.SetResult(true);
            }

            await tcs.Task;
            connection.EndWrite();

            // read ACK
            tcs = new TaskCompletionSource<bool>();
            //ackBuffer

            result = connection.BeginRead(0, ackBuffer.Length, timeoutHelper.RemainingTime(), OnIoComplete, tcs);
            if (result == AsyncCompletionResult.Completed)
            {
                tcs.SetResult(true);
            }

            await tcs.Task;
            int ackBytesRead = connection.EndRead();
            Buffer.BlockCopy((Array)connection.AsyncReadBuffer, 0, (Array)ackBuffer, 0, ackBytesRead);

            if (!ConnectionUpgradeHelper.ValidatePreambleResponse(ackBuffer, ackBytesRead, _decoder, Via))
            {
                await ConnectionUpgradeHelper.DecodeFramingFaultAsync(_decoder, connection, Via,
                    MessageEncoder.ContentType, timeoutHelper.RemainingTime());
            }

            return connection;
        }


        private IConnection SendPreamble(IConnection connection, ArraySegment<byte> preamble, ref TimeoutHelper timeoutHelper)
        {
            // initialize a new decoder
            _decoder = new ClientDuplexDecoder(0);
            byte[] ackBuffer = new byte[1];
            connection.Write(preamble.Array, preamble.Offset, preamble.Count, true, timeoutHelper.RemainingTime());

            // read ACK
            int ackBytesRead = connection.Read(ackBuffer, 0, ackBuffer.Length, timeoutHelper.RemainingTime());
            if (!ConnectionUpgradeHelper.ValidatePreambleResponse(ackBuffer, ackBytesRead, _decoder, Via))
            {
                ConnectionUpgradeHelper.DecodeFramingFault(_decoder, connection, Via,
                    MessageEncoder.ContentType, ref timeoutHelper);
            }

            return connection;
        }

        private IAsyncResult BeginSendPreamble(IConnection connection, ArraySegment<byte> preamble, ref TimeoutHelper timeoutHelper,
            AsyncCallback callback, object state)
        {
            return SendPreambleAsync(connection, preamble, timeoutHelper.RemainingTime()).ToApm(callback, state);
        }

        private IConnection EndSendPreamble(IAsyncResult result)
        {
            return result.ToApmEnd<IConnection>();
        }

        protected internal override async Task OnOpenAsync(TimeSpan timeout)
        {
            IConnection connection;
            try
            {
                connection = await _connectionPoolHelper.EstablishConnectionAsync(timeout);
            }
            catch (TimeoutException exception)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(
                    new TimeoutException(SR.Format(SR.TimeoutOnOpen, timeout), exception));
            }

            bool connectionAccepted = false;
            try
            {
                AcceptConnection(connection);
                connectionAccepted = true;
            }
            finally
            {
                if (!connectionAccepted)
                {
                    _connectionPoolHelper.Abort();
                }
            }
        }

        protected override void OnOpen(TimeSpan timeout)
        {
            IConnection connection;
            try
            {
                connection = _connectionPoolHelper.EstablishConnection(timeout);
            }
            catch (TimeoutException exception)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(
                    new TimeoutException(SR.Format(SR.TimeoutOnOpen, timeout), exception));
            }

            bool connectionAccepted = false;
            try
            {
                AcceptConnection(connection);
                connectionAccepted = true;
            }
            finally
            {
                if (!connectionAccepted)
                {
                    _connectionPoolHelper.Abort();
                }
            }
        }

        protected override void ReturnConnectionIfNecessary(bool abort, TimeSpan timeout)
        {
            lock (ThisLock)
            {
                if (abort)
                {
                    _connectionPoolHelper.Abort();
                }
                else
                {
                    _connectionPoolHelper.Close(timeout);
                }
            }
        }

        private void AcceptConnection(IConnection connection)
        {
            base.SetMessageSource(new ClientDuplexConnectionReader(this, connection, _decoder, _settings, MessageEncoder));

            lock (ThisLock)
            {
                if (this.State != CommunicationState.Opening)
                {
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(
                        new CommunicationObjectAbortedException(SR.Format(SR.DuplexChannelAbortedDuringOpen, this.Via)));
                }

                this.Connection = connection;
            }
        }

        protected override void PrepareMessage(Message message)
        {
            base.PrepareMessage(message);
        }

        internal class DuplexConnectionPoolHelper : ConnectionPoolHelper
        {
            private ClientFramingDuplexSessionChannel _channel;
            private ArraySegment<byte> _preamble;

            public DuplexConnectionPoolHelper(ClientFramingDuplexSessionChannel channel,
                ConnectionPool connectionPool, IConnectionInitiator connectionInitiator)
                : base(connectionPool, connectionInitiator, channel.Via)
            {
                _channel = channel;
                _preamble = channel.CreatePreamble();
            }

            protected override TimeoutException CreateNewConnectionTimeoutException(TimeSpan timeout, TimeoutException innerException)
            {
                return new TimeoutException(SR.Format(SR.OpenTimedOutEstablishingTransportSession,
                        timeout, _channel.Via.AbsoluteUri), innerException);
            }

            protected override IConnection AcceptPooledConnection(IConnection connection, ref TimeoutHelper timeoutHelper)
            {
                return _channel.SendPreamble(connection, _preamble, ref timeoutHelper);
            }

            protected override Task<IConnection> AcceptPooledConnectionAsync(IConnection connection, ref TimeoutHelper timeoutHelper)
            {
                return _channel.SendPreambleAsync(connection, _preamble, timeoutHelper.RemainingTime());
            }
        }
    }

    // used by StreamedFramingRequestChannel and ClientFramingDuplexSessionChannel
    internal class ConnectionUpgradeHelper
    {
        public static async Task DecodeFramingFaultAsync(ClientFramingDecoder decoder, IConnection connection,
            Uri via, string contentType, TimeSpan timeout)
        {
            var timeoutHelper = new TimeoutHelper(timeout);
            ValidateReadingFaultString(decoder);

            var tcs = new TaskCompletionSource<bool>();
            var result = connection.BeginRead(0, Math.Min(FaultStringDecoder.FaultSizeQuota, connection.AsyncReadBufferSize),
                timeoutHelper.RemainingTime(), FramingDuplexSessionChannel.OnIoComplete, tcs);
            if (result == AsyncCompletionResult.Completed)
            {
                tcs.TrySetResult(true);
            }

            await tcs.Task;

            int offset = 0;
            int size = connection.EndRead();
            while (size > 0)
            {
                int bytesDecoded = decoder.Decode(connection.AsyncReadBuffer, offset, size);
                offset += bytesDecoded;
                size -= bytesDecoded;

                if (decoder.CurrentState == ClientFramingDecoderState.Fault)
                {
                    ConnectionUtilities.CloseNoThrow(connection, timeoutHelper.RemainingTime());
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(
                        FaultStringDecoder.GetFaultException(decoder.Fault, via.ToString(), contentType));
                }
                else
                {
                    if (decoder.CurrentState != ClientFramingDecoderState.ReadingFaultString)
                    {
                        throw new Exception("invalid framing client state machine");
                    }
                    if (size == 0)
                    {
                        offset = 0;
                        tcs = new TaskCompletionSource<bool>();
                        result = connection.BeginRead(0, Math.Min(FaultStringDecoder.FaultSizeQuota, connection.AsyncReadBufferSize),
                            timeoutHelper.RemainingTime(), FramingDuplexSessionChannel.OnIoComplete, tcs);
                        if (result == AsyncCompletionResult.Completed)
                        {
                            tcs.TrySetResult(true);
                        }

                        await tcs.Task;
                        size = connection.EndRead();
                    }
                }
            }

            throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(decoder.CreatePrematureEOFException());
        }

        public static void DecodeFramingFault(ClientFramingDecoder decoder, IConnection connection,
            Uri via, string contentType, ref TimeoutHelper timeoutHelper)
        {
            ValidateReadingFaultString(decoder);

            int offset = 0;
            byte[] faultBuffer = Fx.AllocateByteArray(FaultStringDecoder.FaultSizeQuota);
            int size = connection.Read(faultBuffer, offset, faultBuffer.Length, timeoutHelper.RemainingTime());

            while (size > 0)
            {
                int bytesDecoded = decoder.Decode(faultBuffer, offset, size);
                offset += bytesDecoded;
                size -= bytesDecoded;

                if (decoder.CurrentState == ClientFramingDecoderState.Fault)
                {
                    ConnectionUtilities.CloseNoThrow(connection, timeoutHelper.RemainingTime());
                    throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(
                        FaultStringDecoder.GetFaultException(decoder.Fault, via.ToString(), contentType));
                }
                else
                {
                    if (decoder.CurrentState != ClientFramingDecoderState.ReadingFaultString)
                    {
                        throw new Exception("invalid framing client state machine");
                    }
                    if (size == 0)
                    {
                        offset = 0;
                        size = connection.Read(faultBuffer, offset, faultBuffer.Length, timeoutHelper.RemainingTime());
                    }
                }
            }

            throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(decoder.CreatePrematureEOFException());
        }

        public static bool InitiateUpgrade(StreamUpgradeInitiator upgradeInitiator, ref IConnection connection,
            ClientFramingDecoder decoder, IDefaultCommunicationTimeouts defaultTimeouts, ref TimeoutHelper timeoutHelper)
        {
            string upgradeContentType = upgradeInitiator.GetNextUpgrade();

            while (upgradeContentType != null)
            {
                EncodedUpgrade encodedUpgrade = new EncodedUpgrade(upgradeContentType);
                // write upgrade request framing for synchronization
                connection.Write(encodedUpgrade.EncodedBytes, 0, encodedUpgrade.EncodedBytes.Length, true, timeoutHelper.RemainingTime());
                byte[] buffer = new byte[1];

                // read upgrade response framing 
                int size = connection.Read(buffer, 0, buffer.Length, timeoutHelper.RemainingTime());

                if (!ValidateUpgradeResponse(buffer, size, decoder)) // we have a problem
                {
                    return false;
                }

                // initiate wire upgrade
                ConnectionStream connectionStream = new ConnectionStream(connection, defaultTimeouts);
                Stream upgradedStream = upgradeInitiator.InitiateUpgrade(connectionStream);

                // and re-wrap connection
                connection = new StreamConnection(upgradedStream, connectionStream);

                upgradeContentType = upgradeInitiator.GetNextUpgrade();
            }

            return true;
        }

        private static void ValidateReadingFaultString(ClientFramingDecoder decoder)
        {
            if (decoder.CurrentState != ClientFramingDecoderState.ReadingFaultString)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new System.ServiceModel.Security.MessageSecurityException(
                    SR.ServerRejectedUpgradeRequest));
            }
        }

        public static bool ValidatePreambleResponse(byte[] buffer, int count, ClientFramingDecoder decoder, Uri via)
        {
            if (count == 0)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(
                    new ProtocolException(SR.Format(SR.ServerRejectedSessionPreamble, via),
                    decoder.CreatePrematureEOFException()));
            }

            // decode until the framing byte has been processed (it always will be)
            while (decoder.Decode(buffer, 0, count) == 0)
            {
                // do nothing
            }

            if (decoder.CurrentState != ClientFramingDecoderState.Start) // we have a problem
            {
                return false;
            }

            return true;
        }

        private static bool ValidateUpgradeResponse(byte[] buffer, int count, ClientFramingDecoder decoder)
        {
            if (count == 0)
            {
                throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(new MessageSecurityException(SR.ServerRejectedUpgradeRequest, decoder.CreatePrematureEOFException()));
            }

            // decode until the framing byte has been processed (it always will be)
            while (decoder.Decode(buffer, 0, count) == 0)
            {
                // do nothing
            }

            if (decoder.CurrentState != ClientFramingDecoderState.UpgradeResponse) // we have a problem
            {
                return false;
            }

            return true;
        }
    }
}
