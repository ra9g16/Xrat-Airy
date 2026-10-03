using System;
using System.Diagnostics;
using System.Threading;

namespace Xrat.Airy
{
    /// <summary>Replays the UDP payloads of a pcap file on a background thread, paced by capture timestamps.</summary>
    public sealed class PcapPlayer : IDisposable
    {
        public delegate void PacketHandler(ReadOnlySpan<byte> payload, in PcapUdpPacket packet);

        readonly string _path;
        readonly PacketHandler _onPacket;
        readonly Action<Exception> _onError;
        Thread _thread;
        volatile bool _running;

        /// <summary>Playback speed multiplier; 0 or less replays as fast as possible.</summary>
        public volatile float Speed = 1f;
        public volatile bool Loop = true;
        /// <summary>Raised on the playback thread each time the file restarts.</summary>
        public event Action Looped;

        public PcapPlayer(string path, PacketHandler onPacket, Action<Exception> onError = null)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _onPacket = onPacket ?? throw new ArgumentNullException(nameof(onPacket));
            _onError = onError;
        }

        public bool IsRunning => _running;
        public string Path => _path;

        public void Start()
        {
            if (_running)
                return;
            // Open once up front so a bad path or format fails on the caller's thread.
            using (new PcapReader(_path)) { }

            _running = true;
            _thread = new Thread(PlayLoop) { IsBackground = true, Name = "Airy pcap player" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            if (_thread != null && _thread != Thread.CurrentThread)
                _thread.Join(1000);
            _thread = null;
        }

        public void Dispose() => Stop();

        void PlayLoop()
        {
            try
            {
                do
                {
                    PlayOnce();
                    if (_running && Loop)
                        Looped?.Invoke();
                } while (_running && Loop);
            }
            catch (Exception e)
            {
                _onError?.Invoke(e);
            }
            _running = false;
        }

        void PlayOnce()
        {
            using (var reader = new PcapReader(_path))
            {
                var clock = Stopwatch.StartNew();
                double firstTimestamp = double.NaN;
                int packets = 0;

                while (_running && reader.TryReadNext(out PcapUdpPacket packet))
                {
                    packets++;
                    if (double.IsNaN(firstTimestamp))
                        firstTimestamp = packet.Timestamp;

                    float speed = Speed;
                    if (speed > 0f)
                    {
                        double due = (packet.Timestamp - firstTimestamp) / speed;
                        double wait = due - clock.Elapsed.TotalSeconds;
                        if (wait > 0.002)
                            Thread.Sleep((int)(wait * 1000));
                    }

                    _onPacket(packet.Payload.AsSpan(), in packet);
                }

                if (packets == 0)
                    throw new InvalidOperationException($"No UDP packets found in '{_path}'.");
            }
        }
    }
}
