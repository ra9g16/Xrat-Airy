"""Run with: python3 -m unittest discover -s tools/tests"""
import math
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import airy_protocol as ap  # noqa: E402
import airy_simulator as sim  # noqa: E402


class ProtocolTests(unittest.TestCase):
    def test_msop_round_trip_and_manual_example(self):
        channels = [(0, 0)] * 96
        channels[0] = (0x010D, 0x6E)  # manual 4.4.2.2 worked example
        firings = [(0x008B, channels), (0x008B + 40, channels), (0x008B + 80, channels), (0x008B + 120, channels)]
        packet = ap.encode_msop(7, 1700000000.5, firings)
        self.assertEqual(len(packet), ap.PACKET_SIZE)

        decoded = ap.decode_msop(packet)
        self.assertEqual(decoded.packet_count, 7)
        self.assertAlmostEqual(decoded.timestamp, 1700000000.5, places=6)
        self.assertEqual(len(decoded.blocks), 8)
        azimuth, block_channels = decoded.blocks[0]
        self.assertAlmostEqual(azimuth * ap.AZIMUTH_RESOLUTION, 1.39)
        self.assertAlmostEqual(block_channels[0][0] * ap.DISTANCE_RESOLUTION, 1.345)
        self.assertEqual(block_channels[0][1], 110)

    def test_difop_round_trip(self):
        info = ap.DifopInfo(motor_rpm=600, return_mode=0x00, sync_mode=0x03, sync_ok=True,
                            machine_voltage=11.98, temperature=-5.25, device_time=123.000456)
        decoded = ap.decode_difop(ap.encode_difop(info))
        self.assertEqual(decoded.motor_rpm, 600)
        self.assertEqual(decoded.lidar_ip, "192.168.1.200")
        self.assertEqual(decoded.return_mode, 0x00)
        self.assertTrue(decoded.sync_ok)
        self.assertAlmostEqual(decoded.machine_voltage, 11.98)
        self.assertAlmostEqual(decoded.temperature, -5.25)
        self.assertAlmostEqual(decoded.device_time, 123.000456, places=6)

    def test_figure12_convention(self):
        x, y, z = ap.to_xyz(1.0, 90.0, 0.0, optical_center_height=0.0)
        self.assertAlmostEqual(x, 0.0)
        self.assertAlmostEqual(y, -1.0)  # clockwise from +X: 90 deg points right (-Y)

    def test_simulator_pcap_reconstructs_room(self):
        firings = sim.build_revolution()
        self.assertEqual(len(firings), 900)
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "room.pcap")
            with ap.PcapWriter(path) as writer:
                for i, packet_firings in enumerate(sim.packets_per_revolution(firings)):
                    writer.write(100 + i * 1e-4, ap.encode_msop(i, 100.0, packet_firings), ap.MSOP_PORT)

            points = []

            def on_packet(_, port, payload):
                self.assertEqual(port, ap.MSOP_PORT)
                packet = ap.decode_msop(payload)
                for block, (azimuth, channels) in enumerate(packet.blocks):
                    first = (block & 1) * 48
                    for i, (distance, _) in enumerate(channels):
                        if distance:
                            elevation = (first + i) * ap.NOMINAL_VERTICAL_STEP
                            points.append(ap.to_xyz(distance * ap.DISTANCE_RESOLUTION, azimuth / 100, elevation))

            self.assertEqual(ap.read_pcap_udp(path, on_packet), 225)

        self.assertGreater(len(points), 80000)
        # Every point lies inside the room (within quantisation) and nothing pokes through the ceiling.
        for x, y, z in points:
            self.assertGreaterEqual(x, sim.ROOM_MIN[0] - 0.01)
            self.assertLessEqual(x, sim.ROOM_MAX[0] + 0.01)
            self.assertGreaterEqual(y, sim.ROOM_MIN[1] - 0.01)
            self.assertLessEqual(y, sim.ROOM_MAX[1] + 0.01)
            self.assertLessEqual(z, sim.CEILING + 0.01)
        # The zenith beam hits the ceiling straight up.
        self.assertTrue(any(abs(x) < 0.05 and abs(y) < 0.05 and abs(z - sim.CEILING) < 0.01 for x, y, z in points))
        # Something lands on the ball surface.
        on_ball = [p for p in points if abs(math.dist(p, sim.BALL_CENTER) - sim.BALL_RADIUS) < 0.01]
        self.assertGreater(len(on_ball), 50)


if __name__ == "__main__":
    unittest.main()
