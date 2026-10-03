using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Xrat.Airy
{
    /// <summary>Receives UDP datagrams on a background thread and hands each one to a callback.</summary>
    public sealed class UdpPacketReceiver : IDisposable
    {
        public delegate void PacketHandler(ReadOnlySpan<byte> payload, IPAddress source);

        readonly IPEndPoint _localEndPoint;
        readonly PacketHandler _onPacket;
        readonly Action<Exception> _onError;
        readonly int _receiveBufferBytes;
        Socket _socket;
        Thread _thread;
        volatile bool _running;

        public UdpPacketReceiver(IPAddress bindAddress, int port, PacketHandler onPacket, Action<Exception> onError = null,
            int receiveBufferBytes = 4 * 1024 * 1024)
        {
            _localEndPoint = new IPEndPoint(bindAddress ?? IPAddress.Any, port);
            _onPacket = onPacket ?? throw new ArgumentNullException(nameof(onPacket));
            _onError = onError;
            _receiveBufferBytes = receiveBufferBytes;
        }

        public bool IsRunning => _running;
        public IPEndPoint LocalEndPoint => _localEndPoint;

        public void Start()
        {
            if (_running)
                return;

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                // ~1.9 MB/s of MSOP traffic; a large kernel buffer rides out editor hitches.
                socket.ReceiveBufferSize = _receiveBufferBytes;
                socket.ReceiveTimeout = 250;
                socket.Bind(_localEndPoint);
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            _socket = socket;
            _running = true;
            _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = $"Airy UDP {_localEndPoint.Port}" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            _socket?.Close();
            if (_thread != null && _thread != Thread.CurrentThread)
                _thread.Join(1000);
            _thread = null;
            _socket = null;
        }

        public void Dispose() => Stop();

        void ReceiveLoop()
        {
            var buffer = new byte[65536];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            Socket socket = _socket;

            while (_running)
            {
                int length;
                try
                {
                    length = socket.ReceiveFrom(buffer, ref remote);
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut
                                                || e.SocketErrorCode == SocketError.WouldBlock
                                                || e.SocketErrorCode == SocketError.ConnectionReset)
                {
                    continue; // timeout lets us notice Stop(); ConnectionReset is a stray ICMP on Windows
                }
                catch (Exception e) when (!_running && (e is SocketException || e is ObjectDisposedException))
                {
                    break; // socket closed by Stop()
                }
                catch (Exception e)
                {
                    _onError?.Invoke(e);
                    break;
                }

                try
                {
                    _onPacket(new ReadOnlySpan<byte>(buffer, 0, length), ((IPEndPoint)remote).Address);
                }
                catch (Exception e)
                {
                    _onError?.Invoke(e);
                }
            }
            _running = false;
        }
    }
}
