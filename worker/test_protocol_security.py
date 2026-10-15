import io
import json
import unittest

from feed_worker import run


class ProtocolSecurityTests(unittest.TestCase):
    def test_bad_frame_does_not_kill_worker_or_consume_following_ping(self):
        frames = ("[" * 2000 + "]" * 2000, '{"generation":' + "9" * 5000 + "}")
        ping = {"command": "ping", "requestId": "after-invalid-frame", "generation": 2}
        for frame in frames:
            with self.subTest(kind="deep" if frame.startswith("[") else "large-integer"):
                output = io.StringIO()
                run(io.StringIO(frame + "\n" + json.dumps(ping) + "\n"), output)
                events = [json.loads(line) for line in output.getvalue().splitlines()]
                self.assertEqual([event["type"] for event in events], ["error", "pong"])
                self.assertEqual(events[-1]["requestId"], ping["requestId"])


if __name__ == "__main__":
    unittest.main()
